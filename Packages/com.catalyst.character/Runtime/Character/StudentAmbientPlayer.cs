using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Random = UnityEngine.Random;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace catalyst.xr67
{
    /// <summary>
    /// Ambient animation driver for a foreground student. Plays the current
    /// mode's base loop (Reading / Listening, from a
    /// <see cref="StudentBehaviourProfile"/>) at a per-student random speed
    /// and phase — or, when the set has a Loops list, rotates between those
    /// loops at random — and occasionally crossfades into a one-shot picked by
    /// weight from the mode's pool — minus this student's
    /// <see cref="excludedOneShots"/>, so a kid on the far-left seat never
    /// plays look-left. Runs on a manual-update mixer PlayableGraph, no
    /// AnimatorController needed. Works the same in Edit Mode: tick
    /// <see cref="playInEditMode"/> and use <see cref="previewMode"/> or the
    /// context menu to watch loops, mode crossfades and one-shots in the
    /// Scene view. <see cref="StudentGazeController"/> layers gaze on top
    /// via <see cref="PoseSampled"/> and the suppress flag. Don't combine
    /// with RuntimeClipLoop or AnimationSequencePlayer on the same character.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class StudentAmbientPlayer : MonoBehaviour, IPoseSampler, IIdleMotionSettingsSource
    {
        [Tooltip("Behaviour data: per-mode base loop, one-shot pool and gaze tuning. Shareable across students.")]
        public StudentBehaviourProfile profile;

        [Tooltip("Mode this student starts in when entering Play Mode.")]
        public StudentMode startMode = StudentMode.Listening;

        [Tooltip("One-shots this student never plays, e.g. mocap_lookLeft on a seat where looking left makes no sense.")]
        public List<AnimationClip> excludedOneShots = new List<AnimationClip>();

        [Tooltip("Advance the behaviour continuously in Edit Mode.")]
        public bool playInEditMode;

        [Tooltip("Mode used while previewing in Edit Mode. Changing it previews the mode crossfade.")]
        public StudentMode previewMode = StudentMode.Listening;

        [Tooltip("Pin feet to each clip's IK goals — fixes the foot slide humanoid retargeting introduces. Disable if ankles look wrong on a specific clip.")]
        public bool applyFootIK = true;

        [Tooltip("Play Mode: add a HandCollisionGuard to this character when it has none, so hands and forearms steer around the torso while loops / one-shots crossfade instead of cutting through it.")]
        public bool guardHandsDuringBlends = true;

        [Tooltip("Play Mode: add a FootStepper to this character when it has none, so the feet stay planted while body poses crossfade and take a small step (one foot, then the other) instead of sliding.")]
        public bool stepFeetDuringBlends = true;

        /// <summary>
        /// 0 while an authored pose plays on its own, rising to 1 in the middle
        /// of a full-body crossfade (loop → loop, loop ↔ one-shot). Updated each
        /// evaluation; HandCollisionGuard scales its correction by it.
        /// </summary>
        public float PoseBlendActivity { get; private set; }

        /// <summary>0 at both ends of a blend, 1 in the middle, flat-topped so the whole crossing is covered.</summary>
        private static float Bell(float w)
        {
            float u = 2f * Mathf.Clamp01(w) - 1f;
            u *= u;
            return 1f - u * u;
        }

        /// <summary>Current behaviour mode.</summary>
        public StudentMode Mode { get; private set; }

        /// <summary>Raised when a mode switch begins (the crossfade starts).</summary>
        public event Action<StudentMode> ModeChanged;

        /// <summary>A one-shot started: the entry and why ("timer", "speech", "speech (queued)", "manual").</summary>
        public event Action<StudentBehaviourProfile.OneShotEntry, string> OneShotStarted;

        /// <summary>A one-shot finished and the base loop owns the body again. The bool is true when it was cut short (phase / mode change) rather than played out.</summary>
        public event Action<StudentBehaviourProfile.OneShotEntry, bool> OneShotEnded;

        /// <summary>Planned length of the active one-shot (blend in + hold + blend out, seconds), 0 when none.</summary>
        public float ActiveOneShotDuration =>
            shotPhase == ShotPhase.None || activeShot == null ? 0f
            : ShotBlendIn(activeShot) + shotHold + ShotBlendOut(activeShot);

        /// <summary>Playback speed rolled for the active one-shot.</summary>
        public float ActiveOneShotSpeed => shotSpeed;

        /// <summary>The idle loop playing now (the set's Base Loop, or the current pick of its Loops rotation).</summary>
        public AnimationClip BaseLoopClip =>
            baseInput >= 0 && baseInput < graphClips.Count ? graphClips[baseInput]
            : ActiveSet != null ? ActiveSet.baseLoop : null;

        /// <summary>
        /// The idle loop changed: the clip now playing, why ("rotation" = the
        /// set's random loop rotation, "phase" / "mode" = a set switch), and
        /// the seconds it will hold before the rotation moves on (0 = until the
        /// set changes).
        /// </summary>
        public event Action<AnimationClip, string, float> LoopChanged;

        /// <summary>
        /// Usable loops of the current rotation (empty when the set plays its
        /// Base Loop alone). With mood blending: every phase loop that has
        /// weight at the eased mood.
        /// </summary>
        public IReadOnlyList<StudentBehaviourProfile.LoopEntry> CurrentLoops
        {
            get
            {
                if (!MoodBlending) return loopEntries;
                BuildBlend();
                return blendEntries.Count > 0 ? (IReadOnlyList<StudentBehaviourProfile.LoopEntry>)blendEntries : loopEntries;
            }
        }

        /// <summary>Seconds until the loop rotation moves to another loop (0 when it does not rotate).</summary>
        public float LoopTimeLeft => CurrentLoops.Count > 1 ? Mathf.Max(loopTimeLeft, 0f) : 0f;

        /// <summary>True when idle loops are picked across the phase sets by the eased mood (profile Blend Loops By Mood).</summary>
        public bool MoodBlending => usePhases && profile != null && profile.blendLoopsByMood && profile.HasPhases;

        /// <summary>The slowly eased mood the loop picks follow (0..100; -1 before any phase or mood).</summary>
        public float EasedMood => moodEased;

        /// <summary>
        /// The LLM's mood for the latest reply (0..100). With mood blending the
        /// body eases toward it over the profile's Mood Ease Seconds.
        /// </summary>
        public void SetMood(float mood)
        {
            moodTarget = Mathf.Clamp(mood, 0f, 100f);
            if (moodEased < 0f) moodEased = usePhases && profile != null ? profile.MoodFor(phase) : moodTarget;
        }

        /// <summary>The loop mix at the eased mood, e.g. "Idle03 45%, Idle_HandsFront 30%" (for logs).</summary>
        public string LoopMixText()
        {
            if (!MoodBlending) return null;
            BuildBlend();
            float total = 0f;
            foreach (float w in blendWeights) total += w;
            if (total <= 0f) return null;
            var order = new List<int>();
            for (int i = 0; i < blendEntries.Count; i++) order.Add(i);
            order.Sort((a, b) => blendWeights[b].CompareTo(blendWeights[a]));
            var parts = new List<string>();
            foreach (int i in order) parts.Add($"{blendEntries[i].clip.name} {100f * blendWeights[i] / total:0}%");
            return string.Join(", ", parts);
        }

        /// <summary>Result of <see cref="PlayGesture"/>.</summary>
        public enum GestureResult
        {
            /// <summary>The one-shot started now.</summary>
            Started,
            /// <summary>Another one-shot is fading out (cut for this one); it starts the moment that one is gone.</summary>
            Queued,
            /// <summary>No phase set has a one-shot with this id.</summary>
            NotFound,
            /// <summary>No graph (profile missing / no clips).</summary>
            Unavailable,
        }

        /// <summary>Display name of a one-shot entry: its label, else its clip name.</summary>
        public static string EntryName(StudentBehaviourProfile.OneShotEntry entry)
        {
            if (entry == null) return "none";
            string clip = entry.clip != null ? entry.clip.name : "no clip";
            return string.IsNullOrEmpty(entry.label) ? clip : $"{entry.label} ({clip})";
        }

        /// <summary>Raised each frame right after the pose was evaluated — the Edit Mode hook for gaze.</summary>
        public event Action PoseSampled;

        bool IPoseSampler.IsPreviewing => isActiveAndEnabled && playInEditMode;

        /// <summary>The profile's idle settings for the current mode drive ProceduralIdleMotion on this student.</summary>
        /// <remarks>Always the Reading / Listening mode's settings, also while a conversation phase drives the clips: the stance / breathing tuning belongs to the character, not the phase.</remarks>
        IdleMotionSettings IIdleMotionSettingsSource.ActiveIdleSettings => profile != null ? profile.Get(Mode).idle : null;

        UnityEngine.Object IIdleMotionSettingsSource.IdleSettingsAsset => profile;

        public bool IsOneShotActive => shotPhase != ShotPhase.None;

        /// <summary>True while the idle loop is crossfading (phase change or loop rotation).</summary>
        public bool IsLoopBlending => prevBaseInput >= 0;

        /// <summary>
        /// What the body pose was made of at the last evaluation:
        /// (<paramref name="prev"/> → <paramref name="current"/> by
        /// <paramref name="loopWeight"/>), with a full-body one-shot
        /// <paramref name="shot"/> on top at <paramref name="shotWeight"/>.
        /// prev / shot are null when not in play.
        /// </summary>
        public void GetPoseMix(out AnimationClip prev, out AnimationClip current, out float loopWeight,
            out AnimationClip shot, out float shotWeight)
        {
            prev = mixPrev;
            current = mixBase;
            loopWeight = mixLoopWeight;
            shot = mixShot;
            shotWeight = mixShotWeight;
        }

        private AnimationClip mixPrev, mixBase, mixShot;
        private float mixLoopWeight = 1f, mixShotWeight;

        /// <summary>The loop a running crossfade comes from, null when none is running.</summary>
        public AnimationClip BlendingFromClip =>
            prevBaseInput >= 0 && prevBaseInput < graphClips.Count ? graphClips[prevBaseInput] : null;

        /// <summary>One-shot blend in, never below the profile's Min One Shot Blend In.</summary>
        private float ShotBlendIn(StudentBehaviourProfile.OneShotEntry entry) =>
            Mathf.Max(entry != null ? entry.blendIn : 0f, profile != null ? profile.minOneShotBlendIn : 0f, 0.001f);

        /// <summary>One-shot blend out, never below the profile's Min One Shot Blend Out.</summary>
        private float ShotBlendOut(StudentBehaviourProfile.OneShotEntry entry) =>
            Mathf.Max(entry != null ? entry.blendOut : 0f, profile != null ? profile.minOneShotBlendOut : 0f, 0.001f);

        /// <summary>Starts the quiet time in which the loop rotation holds still (profile Settle Seconds).</summary>
        private void Settle()
        {
            if (profile != null) settleLeft = Mathf.Max(settleLeft, profile.settleSeconds);
        }

        /// <summary>True once a conversation phase was set — the phase's set then replaces the Reading / Listening mode until <see cref="SetMode"/> is called.</summary>
        public bool UsesPhases => usePhases;

        /// <summary>Conversation phase last set through <see cref="SetPhase"/>.</summary>
        public ConversationPhase Phase => phase;

        /// <summary>The set currently driving the loop and pool (phase set when phases are in use).</summary>
        public StudentBehaviourProfile.ModeSet CurrentSet => ActiveSet;

        /// <summary>Label (or clip name) of the one-shot playing now, null when none.</summary>
        public string ActiveOneShotName =>
            shotPhase == ShotPhase.None || activeShot == null ? null
            : !string.IsNullOrEmpty(activeShot.label) ? activeShot.label
            : activeShot.clip != null ? activeShot.clip.name : null;

        /// <summary>True while the active one-shot asks the gaze layer to stand down.</summary>
        public bool ActiveOneShotSuppressesGaze =>
            shotPhase != ShotPhase.None && activeShot != null && activeShot.suppressGaze;

        private enum ShotPhase { None, BlendIn, Hold, BlendOut }

        private PlayableGraph graph;
        private AnimationMixerPlayable mixer;
        private readonly List<AnimationClipPlayable> playables = new List<AnimationClipPlayable>();
        private readonly List<AnimationClip> graphClips = new List<AnimationClip>();
        private readonly Dictionary<AnimationClip, int> clipToInput = new Dictionary<AnimationClip, int>();
        private readonly List<AnimationClip> desiredClips = new List<AnimationClip>();
        private readonly HashSet<AnimationClip> desiredSet = new HashSet<AnimationClip>();
        private Animator boundAnimator;

        // Masked one-shots (e.g. face-only clips): a second mixer on layer 1
        // of a layer mixer, so the clip only touches what its AvatarMask lets
        // through while the base loop keeps the full body. Layer 0 is the
        // mixer above, unchanged.
        private AnimationLayerMixerPlayable layerMixer;
        private AnimationMixerPlayable maskedMixer;
        private readonly List<AnimationClipPlayable> maskedPlayables = new List<AnimationClipPlayable>();
        private readonly List<AnimationClip> maskedClips = new List<AnimationClip>();
        private readonly Dictionary<AnimationClip, int> maskedClipToInput = new Dictionary<AnimationClip, int>();
        private readonly List<AnimationClip> desiredMasked = new List<AnimationClip>();
        private readonly HashSet<AnimationClip> desiredMaskedSet = new HashSet<AnimationClip>();
        private AvatarMask appliedLayerMask;
        private bool shotMasked;

        private bool usePhases;
        private ConversationPhase phase;

        private int baseInput = -1, prevBaseInput = -1;
        private float baseSpeed = 1f, prevBaseSpeed = 1f;
        // Slow speed drift of the loops (profile Loop Speed Drift): each loop
        // gets its own noise seed so the two sides of a crossfade drift apart.
        private float driftClock, baseDriftSeed = 17.3f, prevDriftSeed = 71.9f;

        /// <summary>Speed multiplier of the slow drift for a noise seed, around 1.</summary>
        private float Drift(float seed)
        {
            if (profile == null || profile.loopSpeedDrift <= 0f) return 1f;
            float n = Mathf.PerlinNoise(seed, driftClock / Mathf.Max(profile.loopSpeedDriftPeriod, 1f));
            return 1f + profile.loopSpeedDrift * Mathf.Clamp(2f * n - 1f, -1f, 1f);
        }
        private float baseTime, prevBaseTime;
        private float modeBlendElapsed, modeBlendDuration;

        // Loop rotation of the active set (empty = its Base Loop alone).
        private readonly List<StudentBehaviourProfile.LoopEntry> loopEntries = new List<StudentBehaviourProfile.LoopEntry>();
        private StudentBehaviourProfile.LoopEntry currentLoop;
        private float loopTimeLeft;
        private bool timerOneShots = true;
        private float settleLeft;      // seconds of quiet left before the loop rotation may switch

        // Mood blending: loops of all phase sets, weighted by the eased mood.
        private float moodTarget = -1f, moodEased = -1f;
        private float fitCheckIn;
        private bool loopShortenedByMood;
        private readonly List<StudentBehaviourProfile.LoopEntry> blendEntries = new List<StudentBehaviourProfile.LoopEntry>();
        private readonly List<float> blendWeights = new List<float>();
        private readonly List<float> blendShare = new List<float>();   // the representative entry's own contribution

        private readonly List<StudentBehaviourProfile.OneShotEntry> pool = new List<StudentBehaviourProfile.OneShotEntry>();
        private float poolTotalWeight;
        private StudentBehaviourProfile.OneShotEntry activeShot;
        private ShotPhase shotPhase;
        private int shotInput = -1;
        private float shotSpeed = 1f, shotTime, shotPhaseElapsed, shotHold, shotWeight;
        private float nextShotIn;
        private bool appliedFootIK = true;

#if UNITY_EDITOR
        private double lastEditorTime;
#endif

        private StudentBehaviourProfile.ModeSet ActiveSet =>
            profile == null ? null : usePhases ? profile.Get(phase) : profile.Get(Mode);

        /// <summary>
        /// True when another pose driver on this character owns the Animator.
        /// Two playable graphs bound to one Animator alternately pose it every
        /// tick — visible as constant twitching that no tuning fixes.
        /// </summary>
        private bool ConflictingDriverPresent()
        {
            AnimationSequencePlayer sequence = GetComponent<AnimationSequencePlayer>();
            if (sequence != null && sequence.isActiveAndEnabled)
            {
                Debug.LogWarning($"StudentAmbientPlayer on '{name}': an enabled AnimationSequencePlayer is on this character too — they both drive the Animator and the character will twitch. Standing down; remove one of them (sequencer = recording stack, ambient player = live-actor stack).", this);
                return true;
            }
            RuntimeClipLoop loop = GetComponent<RuntimeClipLoop>();
            if (loop != null && loop.enabled)
                Debug.LogWarning($"StudentAmbientPlayer on '{name}': an enabled RuntimeClipLoop overrides this driver — remove or disable it.", this);
            return false;
        }

        private void OnEnable()
        {
            Mode = Application.isPlaying ? startMode : previewMode;
            if (ConflictingDriverPresent()) return;
            if (Application.isPlaying)
            {
                if (guardHandsDuringBlends && GetComponent<HandCollisionGuard>() == null)
                    gameObject.AddComponent<HandCollisionGuard>();
                if (stepFeetDuringBlends && GetComponent<FootStepper>() == null)
                    gameObject.AddComponent<FootStepper>();
                if (EnsureGraph()) Step(0f);
            }
#if UNITY_EDITOR
            else
            {
                EditorApplication.update += EditorTick;
                EditorApplication.projectChanged += OnProjectChanged;
                lastEditorTime = EditorApplication.timeSinceStartup;
                if (EnsureGraph()) Step(0f);
            }
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorTick;
            EditorApplication.projectChanged -= OnProjectChanged;
#endif
            DestroyGraph();
        }

#if UNITY_EDITOR
        /// <summary>
        /// A reimport re-creates clip data behind the SAME managed object, so
        /// EnsureGraph's identity check cannot detect it and the cached graph
        /// would keep evaluating dead data (frozen pose). Rebuild on any
        /// project change.
        /// </summary>
        private void OnProjectChanged()
        {
            DestroyGraph();
            if (!Application.isPlaying && isActiveAndEnabled && EnsureGraph())
                Step(0f);
        }
#endif

        /// <summary>
        /// While true the graph is kept alive but not evaluated (and timers do
        /// not advance) — set by IntroSequencePlayer while a Timeline drives
        /// this Animator. Cheaper than disabling: no graph destroy/rebuild and
        /// no Animator rebind on both ends of the intro.
        /// </summary>
        public bool Suspended { get; set; }

        private void Update()
        {
            if (!Application.isPlaying || Suspended) return;
            Step(Time.deltaTime);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying || !isActiveAndEnabled) return;
            AnimationSequencePlayer sequence = GetComponent<AnimationSequencePlayer>();
            if (sequence != null && sequence.isActiveAndEnabled) return;
            if (!EnsureGraph()) return;
            if (Mode != previewMode) SetMode(previewMode);
            else Step(0f);
        }

        private void EditorTick()
        {
            if (this == null || Application.isPlaying) return;

            double now = EditorApplication.timeSinceStartup;
            float delta = (float)(now - lastEditorTime);
            lastEditorTime = now;

            if (!playInEditMode) return;
            AnimationSequencePlayer sequence = GetComponent<AnimationSequencePlayer>();
            if (sequence != null && sequence.isActiveAndEnabled) return;
            Step(delta);
            SceneView.RepaintAll();
        }
#endif

        /// <summary>Switches behaviour mode, crossfading base loops and re-arming the one-shot scheduler.</summary>
        public void SetMode(StudentMode mode)
        {
            bool wasPhases = usePhases;
            usePhases = false;
            if (!EnsureGraph()) { Mode = mode; return; }
            if (!wasPhases && mode == Mode && baseInput >= 0) return;
            Mode = mode;
            BeginMode(instant: false);
            ModeChanged?.Invoke(mode);
        }

        /// <summary>
        /// Switches to a conversation phase's set (profile Calm / Transition /
        /// Agitated; Listening when that phase has no base loop): crossfades
        /// the base loop, lets an active one-shot blend out, re-arms the pool.
        /// </summary>
        public void SetPhase(ConversationPhase newPhase)
        {
            bool changed = !usePhases || newPhase != phase;
            usePhases = true;
            phase = newPhase;
            if (profile != null)
            {
                if (moodEased < 0f) moodEased = profile.MoodFor(newPhase);
                // A mood that doesn't belong to the reported phase at all (e.g.
                // no mood yet, or the scenario ending in Calm) is pulled into it.
                if (moodTarget < 0f || PhaseShare(moodTarget, newPhase) <= 0f) moodTarget = profile.MoodFor(newPhase);
            }
            if (!EnsureGraph()) return;
            if (!changed && baseInput >= 0) return;
            BeginMode(instant: false);
        }

        /// <summary>
        /// Plays one gesture from the current pool now (weighted pick) — e.g.
        /// when the character starts speaking. False when the pool is empty or
        /// a one-shot is already playing (that one keeps going).
        /// </summary>
        public bool PlayGestureNow(string reason = "speech")
        {
            if (!EnsureGraph() || pool.Count == 0) return false;
            if (shotPhase == ShotPhase.BlendOut)
            {
                // Typically the previous phase's shot, cut by SetPhase: start
                // the gesture the moment it has faded, no pop.
                gestureQueued = true;
                queuedEntry = null;
                queuedReason = reason + " (queued)";
                return true;
            }
            if (shotPhase != ShotPhase.None) return false;
            StartShot(PickShot(), reason);
            return shotPhase != ShotPhase.None;
        }

        /// <summary>
        /// Plays the one-shot with this LLM id (see
        /// <see cref="StudentBehaviourProfile.GestureId"/>), looked up in the
        /// current phase's set first, then any phase set. A one-shot already
        /// playing is cut (blends out) and this one follows it — a picked
        /// gesture means something, a random one does not.
        /// </summary>
        public GestureResult PlayGesture(string id, string reason, out StudentBehaviourProfile.OneShotEntry entry)
        {
            entry = null;
            if (!EnsureGraph()) return GestureResult.Unavailable;
            entry = profile.FindGesture(id, phase, out _);
            if (entry == null || excludedOneShots.Contains(entry.clip)) { entry = null; return GestureResult.NotFound; }

            if (shotPhase == ShotPhase.BlendIn || shotPhase == ShotPhase.Hold)
            {
                shotPhase = ShotPhase.BlendOut;
                shotPhaseElapsed = ShotBlendOut(activeShot) * (1f - shotWeight);
                shotCut = true;
            }
            if (shotPhase == ShotPhase.BlendOut)
            {
                gestureQueued = true;
                queuedEntry = entry;
                queuedReason = reason + " (queued)";
                return GestureResult.Queued;
            }
            StartShot(entry, reason);
            return shotPhase != ShotPhase.None ? GestureResult.Started : GestureResult.Unavailable;
        }

        /// <summary>True while a gesture waits for the fading one-shot to finish.</summary>
        public bool GestureQueued => gestureQueued;

        private string queuedReason = "speech (queued)";

        private bool gestureQueued;
        private StudentBehaviourProfile.OneShotEntry queuedEntry;   // null = weighted pick from the pool

        [ContextMenu("Phase/Calm")] private void PhaseCalm() => SetPhase(ConversationPhase.Calm);
        [ContextMenu("Phase/Transition")] private void PhaseTransition() => SetPhase(ConversationPhase.Transition);
        [ContextMenu("Phase/Agitated")] private void PhaseAgitated() => SetPhase(ConversationPhase.Agitated);

        /// <summary>Plays one specific clip from the current pool now (testing).</summary>
        public void TriggerOneShot(AnimationClip clip)
        {
            if (!EnsureGraph()) return;
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].clip != clip) continue;
                StartShot(pool[i], "manual");
                return;
            }
            Debug.LogWarning($"StudentAmbientPlayer: '{(clip != null ? clip.name : "null")}' is not in the current mode's pool (or is excluded).", this);
        }

        [ContextMenu("Next Idle Loop")]
        private void NextIdleLoop()
        {
            if (loopEntries.Count < 2)
                Debug.Log($"StudentAmbientPlayer: the current set has {loopEntries.Count} loop(s) in its Loops list — nothing to rotate to.", this);
            loopTimeLeft = 0f;
        }

        [ContextMenu("Trigger Random One-Shot")]
        private void TriggerRandomOneShot()
        {
            if (!EnsureGraph() || pool.Count == 0) return;
            StartShot(PickShot(), "manual");
        }

        private void Step(float dt)
        {
            if (!EnsureGraph() || baseInput < 0) return;

            if (appliedFootIK != applyFootIK)
            {
                for (int i = 0; i < playables.Count; i++) playables[i].SetApplyFootIK(applyFootIK);
                appliedFootIK = applyFootIK;
            }

            UpdateShotScheduler(dt);
            UpdateLoopRotation(dt);

            if (prevBaseInput >= 0)
            {
                modeBlendElapsed += dt;
                if (modeBlendElapsed >= modeBlendDuration)
                {
                    ZeroInput(prevBaseInput);
                    prevBaseInput = -1;
                }
            }

            driftClock += dt;
            baseTime += dt * baseSpeed * Drift(baseDriftSeed);
            SetClipTime(baseInput, baseTime);
            if (prevBaseInput >= 0)
            {
                prevBaseTime += dt * prevBaseSpeed * Drift(prevDriftSeed);
                SetClipTime(prevBaseInput, prevBaseTime);
            }
            if (shotPhase != ShotPhase.None && shotInput >= 0)
            {
                shotTime += dt * shotSpeed;
                if (shotMasked) SetMaskedClipTime(shotInput, shotTime);
                else SetClipTime(shotInput, shotTime);
            }

            float wMode = prevBaseInput >= 0
                ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(modeBlendElapsed / modeBlendDuration))
                : 1f;
            float wShot = shotPhase != ShotPhase.None ? shotWeight : 0f;
            // A masked shot rides on layer 1: the body keeps its base loop.
            float wBodyShot = shotMasked ? 0f : wShot;

            // How "in between" two body poses we are (0 = an authored pose,
            // 1 = mid-crossfade) — HandCollisionGuard only corrects then.
            float activity = 0f;
            if (prevBaseInput >= 0) activity = Bell(wMode) * (1f - wBodyShot);
            if (!shotMasked && (shotPhase == ShotPhase.BlendIn || shotPhase == ShotPhase.BlendOut))
                activity = Mathf.Max(activity, Bell(shotWeight));
            PoseBlendActivity = activity;
            mixPrev = prevBaseInput >= 0 ? graphClips[prevBaseInput] : null;
            mixBase = graphClips[baseInput];
            mixLoopWeight = wMode;
            mixShot = !shotMasked && shotPhase != ShotPhase.None && activeShot != null ? activeShot.clip : null;
            mixShotWeight = mixShot != null ? wBodyShot : 0f;

            mixer.SetInputWeight(baseInput, (1f - wBodyShot) * wMode);
            if (prevBaseInput >= 0) mixer.SetInputWeight(prevBaseInput, (1f - wBodyShot) * (1f - wMode));
            if (shotPhase != ShotPhase.None && shotInput >= 0)
            {
                if (shotMasked)
                {
                    maskedMixer.SetInputWeight(shotInput, 1f);
                    layerMixer.SetInputWeight(1, wShot);
                }
                else
                {
                    mixer.SetInputWeight(shotInput, wShot);
                }
            }

            // Suppress root motion for this evaluation only, so previews never
            // permanently modify the Animator.
            bool rootMotion = boundAnimator.applyRootMotion;
            boundAnimator.applyRootMotion = false;
            graph.Evaluate(0f);
            boundAnimator.applyRootMotion = rootMotion;
            FilterClipFace();

            PoseSampled?.Invoke();
        }

        // Facial shapes the clips may carry (blinks excluded: ProceduralIdleMotion owns them).
        private static readonly string[] ClipFacePrefixes = { "Brow_", "Eye_", "Nose_", "Cheek_", "Mouth_", "Jaw_", "Tongue_", "Lip_", "V_" };
        private SkinnedMeshRenderer faceMesh;
        private int[] clipFaceIndices;

        /// <summary>
        /// Scales the facial blendshapes the clips just wrote by the profile's
        /// Clip Face Weight, right after the evaluation — before lip sync,
        /// mood and procedural face life (all LateUpdate) add theirs.
        /// </summary>
        private void FilterClipFace()
        {
            if (!Application.isPlaying || profile == null || profile.clipFaceWeight >= 0.999f) return;
            if (clipFaceIndices == null)
            {
                faceMesh = null;
                int most = 0;
                foreach (SkinnedMeshRenderer smr in boundAnimator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    int count = smr.sharedMesh != null ? smr.sharedMesh.blendShapeCount : 0;
                    if (count > most) { most = count; faceMesh = smr; }
                }
                var found = new List<int>();
                if (faceMesh != null)
                {
                    Mesh mesh = faceMesh.sharedMesh;
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        string shape = mesh.GetBlendShapeName(i);
                        if (shape.StartsWith("Eye_Blink", StringComparison.Ordinal)) continue;
                        foreach (string prefix in ClipFacePrefixes)
                            if (shape.StartsWith(prefix, StringComparison.Ordinal)) { found.Add(i); break; }
                    }
                }
                clipFaceIndices = found.ToArray();
            }
            if (faceMesh == null) return;
            float keep = Mathf.Clamp01(profile.clipFaceWeight);
            foreach (int i in clipFaceIndices)
            {
                float w = faceMesh.GetBlendShapeWeight(i);
                if (w != 0f) faceMesh.SetBlendShapeWeight(i, w * keep);
            }
        }

        private void UpdateShotScheduler(float dt)
        {
            switch (shotPhase)
            {
                case ShotPhase.None:
                    if (pool.Count == 0 || !timerOneShots) return;
                    nextShotIn -= dt;
                    if (nextShotIn <= 0f) StartShot(PickShot(), "timer");
                    break;

                case ShotPhase.BlendIn:
                {
                    shotPhaseElapsed += dt;
                    float blendIn = ShotBlendIn(activeShot);
                    shotWeight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(shotPhaseElapsed / blendIn));
                    if (shotPhaseElapsed >= blendIn)
                    {
                        shotPhase = ShotPhase.Hold;
                        shotPhaseElapsed = 0f;
                        shotWeight = 1f;
                    }
                    break;
                }

                case ShotPhase.Hold:
                    shotPhaseElapsed += dt;
                    if (shotPhaseElapsed >= shotHold)
                    {
                        shotPhase = ShotPhase.BlendOut;
                        shotPhaseElapsed = 0f;
                    }
                    break;

                case ShotPhase.BlendOut:
                {
                    shotPhaseElapsed += dt;
                    float blendOut = ShotBlendOut(activeShot);
                    shotWeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(shotPhaseElapsed / blendOut));
                    if (shotPhaseElapsed >= blendOut) EndShot();
                    break;
                }
            }
        }

        private void StartShot(StudentBehaviourProfile.OneShotEntry entry, string reason)
        {
            if (entry == null || entry.clip == null) return;
            bool masked = entry.mask != null;
            int input = masked ? MaskedInputFor(entry.clip) : InputFor(entry.clip);
            if (input < 0) return;
            if (shotInput >= 0 && (shotInput != input || shotMasked != masked)) ZeroShotInput();

            if (masked && appliedLayerMask != entry.mask)
            {
                layerMixer.SetLayerMaskFromAvatarMask(1, entry.mask);
                appliedLayerMask = entry.mask;
            }

            activeShot = entry;
            shotMasked = masked;
            shotInput = input;
            shotSpeed = Mathf.Max(Random.Range(entry.speedRange.x, entry.speedRange.y), 0.01f);
            shotTime = entry.startOffset;
            shotPhaseElapsed = 0f;
            shotWeight = 0f;
            shotPhase = ShotPhase.BlendIn;
            shotHold = entry.holdSeconds > 0f
                ? entry.holdSeconds
                : Mathf.Max((entry.clip.length - entry.startOffset) / shotSpeed - ShotBlendIn(entry) - ShotBlendOut(entry), 0f);

            shotCut = false;

            try { OneShotStarted?.Invoke(entry, reason); }
            catch (Exception e) { Debug.LogError($"StudentAmbientPlayer: OneShotStarted handler threw — {e.Message}", this); }
        }

        private bool shotCut;   // the active one-shot was sent into its blend-out early (phase / mode change)

        private void RaiseShotEnded(StudentBehaviourProfile.OneShotEntry entry, bool cut)
        {
            if (entry == null) return;
            try { OneShotEnded?.Invoke(entry, cut); }
            catch (Exception e) { Debug.LogError($"StudentAmbientPlayer: OneShotEnded handler threw — {e.Message}", this); }
        }

        private void EndShot()
        {
            if (!shotMasked) Settle();
            StudentBehaviourProfile.OneShotEntry ended = activeShot;
            ZeroShotInput();
            shotInput = -1;
            activeShot = null;
            shotPhase = ShotPhase.None;
            shotWeight = 0f;
            RaiseShotEnded(ended, shotCut);
            StudentBehaviourProfile.ModeSet set = ActiveSet;
            if (set != null) nextShotIn = Random.Range(set.oneShotInterval.x, set.oneShotInterval.y);
            if (gestureQueued)
            {
                gestureQueued = false;
                StudentBehaviourProfile.OneShotEntry next = queuedEntry;
                queuedEntry = null;
                if (next != null) StartShot(next, queuedReason);
                else if (pool.Count > 0) StartShot(PickShot(), queuedReason);
            }
        }

        /// <summary>
        /// Moves the set's loop rotation on: when the current loop's hold runs
        /// out, crossfade to another loop of the set (weighted, never the same
        /// one twice in a row). Waits while a full-body one-shot owns the pose
        /// or a crossfade is still running.
        /// </summary>
        private void UpdateLoopRotation(float dt)
        {
            if (settleLeft > 0f && (shotPhase == ShotPhase.None || shotMasked)) settleLeft -= dt;
            bool blend = MoodBlending;
            if (blend)
            {
                EaseMood(dt);
                CheckLoopFit(dt);
            }
            else if (loopEntries.Count < 2) return;
            if (currentLoop == null && blend) return;   // Listening fallback: its Base Loop alone
            loopTimeLeft -= dt;
            if (loopTimeLeft > 0f) return;
            if (prevBaseInput >= 0) return;
            if (shotPhase != ShotPhase.None && !shotMasked) return;
            if (settleLeft > 0f) return;   // a one-shot or phase change just happened — let the body rest

            StudentBehaviourProfile.LoopEntry next = PickLoop(currentLoop);
            int input = next != null ? InputFor(next.clip) : -1;
            if (input < 0) { loopTimeLeft = 1f; return; }

            currentLoop = next;
            loopTimeLeft = RollHold(next);
            string reason = loopShortenedByMood ? "mood" : "rotation";
            loopShortenedByMood = false;
            if (input != baseInput)
            {
                CrossfadeBase(input, RollSpeed(next.speedRange), Random.Range(0f, next.clip.length), next.blendSeconds);
                RaiseLoopChanged(next.clip, reason, loopTimeLeft);
            }
        }

        private void EaseMood(float dt)
        {
            if (moodEased < 0f) moodEased = profile.MoodFor(phase);
            if (moodTarget < 0f) moodTarget = moodEased;
            float tau = profile.moodEaseSeconds;
            moodEased = tau <= 0f ? moodTarget : Mathf.Lerp(moodEased, moodTarget, 1f - Mathf.Exp(-dt / tau));
        }

        /// <summary>
        /// Once a second: when the eased mood has moved on so far that the loop
        /// playing now barely fits (under ~10% of the mix), cut its hold short
        /// so the rotation replaces it within a few seconds — the gradual
        /// counterpart of a hard phase switch.
        /// </summary>
        private void CheckLoopFit(float dt)
        {
            fitCheckIn -= dt;
            if (fitCheckIn > 0f) return;
            fitCheckIn = 1f;
            if (currentLoop == null || loopShortenedByMood) return;
            BuildBlend();
            float total = 0f, mine = 0f;
            for (int i = 0; i < blendEntries.Count; i++)
            {
                total += blendWeights[i];
                if (blendEntries[i].clip == currentLoop.clip) mine = blendWeights[i];
            }
            if (total <= 0f || blendEntries.Count < 2 || mine / total >= 0.1f) return;
            float soon = Random.Range(2f, 6f);
            if (loopTimeLeft > soon) loopTimeLeft = soon;
            loopShortenedByMood = true;
        }

        /// <summary>How much this phase's loops count at the given mood (0..1).</summary>
        private float PhaseShare(float mood, ConversationPhase p)
        {
            profile.PhaseWeights(mood, out float c, out float t, out float a);
            return p == ConversationPhase.Calm ? c : p == ConversationPhase.Transition ? t : a;
        }

        private static readonly ConversationPhase[] BlendPhases =
            { ConversationPhase.Calm, ConversationPhase.Transition, ConversationPhase.Agitated };

        /// <summary>
        /// Loops of all authored phase sets at the eased mood: weight =
        /// phase share × the loop's own weight, merged by clip (a clip in two
        /// sets adds up; its hold / speed / blend come from the set that
        /// contributes most).
        /// </summary>
        private void BuildBlend()
        {
            blendEntries.Clear();
            blendWeights.Clear();
            blendShare.Clear();
            if (profile == null) return;
            float mood = moodEased >= 0f ? moodEased : profile.MoodFor(phase);
            foreach (ConversationPhase p in BlendPhases)
            {
                float share = PhaseShare(mood, p);
                if (share <= 0f) continue;
                StudentBehaviourProfile.ModeSet set = profile.Authored(p);
                if (set == null || set.loops == null) continue;
                foreach (StudentBehaviourProfile.LoopEntry loop in set.loops)
                {
                    if (loop == null || loop.clip == null || loop.weight <= 0f || InputFor(loop.clip) < 0) continue;
                    float w = share * loop.weight;
                    int at = -1;
                    for (int i = 0; i < blendEntries.Count; i++)
                        if (blendEntries[i].clip == loop.clip) { at = i; break; }
                    if (at < 0)
                    {
                        blendEntries.Add(loop);
                        blendWeights.Add(w);
                        blendShare.Add(w);
                        continue;
                    }
                    blendWeights[at] += w;
                    if (w > blendShare[at]) { blendEntries[at] = loop; blendShare[at] = w; }
                }
            }
        }

        /// <summary>The blend's entry for this clip, or null when it has no weight at the eased mood.</summary>
        private StudentBehaviourProfile.LoopEntry BlendEntryFor(AnimationClip clip)
        {
            if (clip == null) return null;
            for (int i = 0; i < blendEntries.Count; i++)
                if (blendEntries[i].clip == clip && blendWeights[i] > 0f) return blendEntries[i];
            return null;
        }

        /// <summary>Keeps the time already left on a loop that carries on, within its (new) hold range.</summary>
        private void KeepHold(StudentBehaviourProfile.LoopEntry entry)
        {
            float hi = Mathf.Max(Mathf.Max(entry.holdSeconds.x, entry.holdSeconds.y), 1f);
            if (loopTimeLeft <= 0f) loopTimeLeft = RollHold(entry);
            else if (loopTimeLeft > hi) loopTimeLeft = hi;
        }

        private StudentBehaviourProfile.LoopEntry PickLoop(StudentBehaviourProfile.LoopEntry avoid)
        {
            if (MoodBlending)
            {
                BuildBlend();
                if (blendEntries.Count > 0)
                {
                    AnimationClip avoidClip = avoid != null && blendEntries.Count > 1 ? avoid.clip : null;
                    float sum = 0f;
                    for (int i = 0; i < blendEntries.Count; i++)
                        if (blendEntries[i].clip != avoidClip) sum += blendWeights[i];
                    float pick = Random.value * sum, run = 0f;
                    for (int i = 0; i < blendEntries.Count; i++)
                    {
                        if (blendEntries[i].clip == avoidClip) continue;
                        run += blendWeights[i];
                        if (pick <= run) return blendEntries[i];
                    }
                    for (int i = blendEntries.Count - 1; i >= 0; i--)
                        if (blendEntries[i].clip != avoidClip) return blendEntries[i];
                }
            }

            float total = 0f;
            for (int i = 0; i < loopEntries.Count; i++)
                if (loopEntries[i] != avoid) total += loopEntries[i].weight;
            if (total <= 0f) return loopEntries.Count > 0 ? loopEntries[0] : null;
            float roll = Random.value * total, acc = 0f;
            for (int i = 0; i < loopEntries.Count; i++)
            {
                if (loopEntries[i] == avoid) continue;
                acc += loopEntries[i].weight;
                if (roll <= acc) return loopEntries[i];
            }
            for (int i = loopEntries.Count - 1; i >= 0; i--)
                if (loopEntries[i] != avoid) return loopEntries[i];
            return loopEntries[0];
        }

        private static float RollHold(StudentBehaviourProfile.LoopEntry entry)
        {
            float lo = Mathf.Min(entry.holdSeconds.x, entry.holdSeconds.y);
            float hi = Mathf.Max(entry.holdSeconds.x, entry.holdSeconds.y);
            return Mathf.Max(Random.Range(lo, hi), 1f);
        }

        private static float RollSpeed(Vector2 range)
        {
            float speed = Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
            return speed > 0f ? speed : 1f;
        }

        /// <summary>Starts a crossfade from the current base loop to <paramref name="input"/>.</summary>
        private void CrossfadeBase(int input, float speed, float time, float blendSeconds)
        {
            // A crossfade still running: its outgoing loop is dropped (it is
            // the faintest of the three) so no input keeps a stale weight.
            if (prevBaseInput >= 0 && prevBaseInput != input) ZeroInput(prevBaseInput);
            prevBaseInput = baseInput;
            prevBaseSpeed = baseSpeed;
            prevDriftSeed = baseDriftSeed;
            baseDriftSeed = Random.Range(0f, 1000f);
            prevBaseTime = baseTime;
            modeBlendDuration = Mathf.Max(blendSeconds, 0.001f);
            modeBlendElapsed = 0f;
            baseInput = input;
            baseSpeed = speed;
            baseTime = time;
        }

        private void RaiseLoopChanged(AnimationClip clip, string reason, float hold)
        {
            try { LoopChanged?.Invoke(clip, reason, hold); }
            catch (Exception e) { Debug.LogError($"StudentAmbientPlayer: LoopChanged handler threw — {e.Message}", this); }
        }

        private StudentBehaviourProfile.OneShotEntry PickShot()
        {
            float roll = Random.value * poolTotalWeight;
            float accumulated = 0f;
            for (int i = 0; i < pool.Count; i++)
            {
                accumulated += Mathf.Max(pool[i].weight, 0f);
                if (roll <= accumulated) return pool[i];
            }
            return pool[pool.Count - 1];
        }

        /// <summary>Applies the current mode: base loop, pool, scheduler. Instant on (re)build, crossfaded on SetMode.</summary>
        private void BeginMode(bool instant)
        {
            StudentBehaviourProfile.ModeSet set = ActiveSet;
            if (set == null) return;

            // Cut any active one-shot through its normal blend-out.
            if (shotPhase == ShotPhase.BlendIn || shotPhase == ShotPhase.Hold)
            {
                shotPhase = ShotPhase.BlendOut;
                shotPhaseElapsed = ShotBlendOut(activeShot) * (1f - shotWeight);
                shotCut = true;
            }

            // Loop: the set's Loops rotation (the loop playing now carries on
            // when the new set lists it too — no needless crossfade), else
            // its Base Loop.
            loopEntries.Clear();
            if (set.loops != null)
                foreach (StudentBehaviourProfile.LoopEntry loop in set.loops)
                    if (loop != null && loop.clip != null && loop.weight > 0f && InputFor(loop.clip) >= 0) loopEntries.Add(loop);

            int newInput;
            float newSpeed, newTime;
            currentLoop = null;
            bool blend = MoodBlending;
            if (blend) BuildBlend();
            bool carriesOn = false;
            if (blend ? blendEntries.Count > 0 : loopEntries.Count > 0)
            {
                // The loop playing now carries on (with the time it has left)
                // when it still has weight — with mood blending a phase change
                // never cuts it; CheckLoopFit retires it once the mood moves on.
                if (!instant && baseInput >= 0)
                {
                    if (blend) currentLoop = BlendEntryFor(graphClips[baseInput]);
                    else
                        foreach (StudentBehaviourProfile.LoopEntry loop in loopEntries)
                            if (InputFor(loop.clip) == baseInput) { currentLoop = loop; break; }
                    carriesOn = currentLoop != null;
                }
                if (carriesOn)
                {
                    newInput = baseInput;
                    newSpeed = baseSpeed;
                    newTime = baseTime;
                    KeepHold(currentLoop);
                }
                else
                {
                    currentLoop = PickLoop(null);
                    newInput = InputFor(currentLoop.clip);
                    newSpeed = RollSpeed(currentLoop.speedRange);
                    newTime = Random.Range(0f, currentLoop.clip.length);
                    loopTimeLeft = RollHold(currentLoop);
                }
                loopShortenedByMood = false;
            }
            else
            {
                newInput = InputFor(set.baseLoop);
                newSpeed = Random.Range(set.baseSpeedRange.x, set.baseSpeedRange.y);
                newTime = RollBaseTime(set);
                loopTimeLeft = 0f;
            }

            if (!instant && baseInput >= 0 && newInput == baseInput)
            {
                // Same loop carries on; a crossfade into it keeps running (no snap).
            }
            else if (instant || baseInput < 0)
            {
                if (prevBaseInput >= 0) ZeroInput(prevBaseInput);
                prevBaseInput = -1;
                if (instant && shotInput >= 0)
                {
                    StudentBehaviourProfile.OneShotEntry dropped = shotPhase != ShotPhase.None ? activeShot : null;
                    ZeroShotInput();
                    shotInput = -1;
                    activeShot = null;
                    shotPhase = ShotPhase.None;
                    shotWeight = 0f;
                    RaiseShotEnded(dropped, true);
                }
                if (instant || baseInput < 0)
                {
                    baseTime = newTime;
                    baseSpeed = newSpeed;
                }
                baseInput = newInput;
            }
            else
            {
                CrossfadeBase(newInput, newSpeed, newTime, set.modeBlendSeconds);
            }
            if (baseInput >= 0)
                RaiseLoopChanged(graphClips[baseInput], usePhases ? "phase" : "mode", CurrentLoops.Count > 1 ? loopTimeLeft : 0f);

            if (!instant && !carriesOn) Settle();
            timerOneShots = set.timerOneShots;
            pool.Clear();
            poolTotalWeight = 0f;
            for (int i = 0; i < set.oneShots.Count; i++)
            {
                StudentBehaviourProfile.OneShotEntry entry = set.oneShots[i];
                if (entry == null || entry.clip == null || entry.weight <= 0f) continue;
                if (excludedOneShots.Contains(entry.clip)) continue;
                pool.Add(entry);
                poolTotalWeight += entry.weight;
            }
            nextShotIn = Random.Range(set.oneShotInterval.x, set.oneShotInterval.y);
        }

        /// <summary>Random phase for playing loops; the authored anchor frame (± per-kid jitter) when Base Start Offset is set.</summary>
        private static float RollBaseTime(StudentBehaviourProfile.ModeSet set)
        {
            if (set.baseLoop == null) return 0f;
            if (set.baseStartOffset < 0f) return Random.Range(0f, set.baseLoop.length);
            return set.baseStartOffset + Random.Range(-set.basePhaseJitter, set.basePhaseJitter);
        }

        private void SetClipTime(int input, float time)
        {
            AnimationClip clip = graphClips[input];
            float local = clip.isLooping ? Mathf.Repeat(time, clip.length) : Mathf.Clamp(time, 0f, clip.length);
            playables[input].SetTime(local);
        }

        private void SetMaskedClipTime(int input, float time)
        {
            AnimationClip clip = maskedClips[input];
            float local = clip.isLooping ? Mathf.Repeat(time, clip.length) : Mathf.Clamp(time, 0f, clip.length);
            maskedPlayables[input].SetTime(local);
        }

        private int InputFor(AnimationClip clip)
        {
            return clip != null && clipToInput.TryGetValue(clip, out int input) ? input : -1;
        }

        private int MaskedInputFor(AnimationClip clip)
        {
            return clip != null && maskedClipToInput.TryGetValue(clip, out int input) ? input : -1;
        }

        private void ZeroInput(int input)
        {
            if (input >= 0) mixer.SetInputWeight(input, 0f);
        }

        /// <summary>Takes the active one-shot's input to zero, on whichever layer it plays.</summary>
        private void ZeroShotInput()
        {
            if (shotInput < 0) return;
            if (shotMasked)
            {
                if (maskedMixer.IsValid()) maskedMixer.SetInputWeight(shotInput, 0f);
                if (layerMixer.IsValid()) layerMixer.SetInputWeight(1, 0f);
            }
            else
            {
                ZeroInput(shotInput);
            }
            shotMasked = false;
        }

        /// <summary>One mixer input per distinct clip the profile can play on this student.</summary>
        private bool EnsureGraph()
        {
            if (profile == null) return false;

            desiredClips.Clear();
            desiredSet.Clear();
            desiredMasked.Clear();
            desiredMaskedSet.Clear();
            foreach (StudentBehaviourProfile.ModeSet set in AllSets())
            {
                if (set == null) continue;
                AddDesired(set.baseLoop);
                if (set.loops != null)
                    foreach (StudentBehaviourProfile.LoopEntry loop in set.loops)
                        if (loop != null) AddDesired(loop.clip);
            }
            foreach (StudentBehaviourProfile.ModeSet set in AllSets())
                if (set != null && set.oneShots != null) AddDesiredPool(set.oneShots);
            if (desiredClips.Count == 0) return false;

            bool valid = graph.IsValid() && graphClips.Count == desiredClips.Count && maskedClips.Count == desiredMasked.Count;
            for (int i = 0; i < desiredClips.Count && valid; i++)
                if (graphClips[i] != desiredClips[i]) valid = false;
            for (int i = 0; i < desiredMasked.Count && valid; i++)
                if (maskedClips[i] != desiredMasked[i]) valid = false;
            if (valid) return true;

            DestroyGraph();

            boundAnimator = GetComponent<Animator>();
            if (boundAnimator == null) boundAnimator = GetComponentInChildren<Animator>(true);
            if (boundAnimator == null) return false;

            graph = PlayableGraph.Create("StudentAmbientPlayer");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            mixer = AnimationMixerPlayable.Create(graph, desiredClips.Count);
            for (int i = 0; i < desiredClips.Count; i++)
            {
                AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, desiredClips[i]);
                playable.SetApplyFootIK(applyFootIK);
                graph.Connect(playable, 0, mixer, i);
                playables.Add(playable);
                graphClips.Add(desiredClips[i]);
                clipToInput[desiredClips[i]] = i;
            }

            maskedMixer = AnimationMixerPlayable.Create(graph, desiredMasked.Count);
            for (int i = 0; i < desiredMasked.Count; i++)
            {
                AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, desiredMasked[i]);
                playable.SetApplyFootIK(false);
                graph.Connect(playable, 0, maskedMixer, i);
                maskedPlayables.Add(playable);
                maskedClips.Add(desiredMasked[i]);
                maskedClipToInput[desiredMasked[i]] = i;
            }

            layerMixer = AnimationLayerMixerPlayable.Create(graph, 2);
            graph.Connect(mixer, 0, layerMixer, 0);
            graph.Connect(maskedMixer, 0, layerMixer, 1);
            layerMixer.SetInputWeight(0, 1f);
            layerMixer.SetInputWeight(1, 0f);
            appliedLayerMask = null;

            AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "AmbientPose", boundAnimator);
            output.SetSourcePlayable(layerMixer);

            BeginMode(instant: true);
            return true;
        }

        // EnsureGraph runs every Step — no per-frame allocation for the set list.
        private readonly StudentBehaviourProfile.ModeSet[] allSets = new StudentBehaviourProfile.ModeSet[5];

        private StudentBehaviourProfile.ModeSet[] AllSets()
        {
            allSets[0] = profile.reading;
            allSets[1] = profile.listening;
            allSets[2] = profile.calm;
            allSets[3] = profile.transition;
            allSets[4] = profile.agitated;
            return allSets;
        }

        private void AddDesired(AnimationClip clip)
        {
            if (clip == null || !desiredSet.Add(clip)) return;
            desiredClips.Add(clip);
        }

        private void AddDesiredPool(List<StudentBehaviourProfile.OneShotEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                StudentBehaviourProfile.OneShotEntry entry = entries[i];
                if (entry == null || entry.clip == null || excludedOneShots.Contains(entry.clip)) continue;
                if (entry.mask != null)
                {
                    if (desiredMaskedSet.Add(entry.clip)) desiredMasked.Add(entry.clip);
                }
                else
                {
                    AddDesired(entry.clip);
                }
            }
        }

        private void DestroyGraph()
        {
            if (graph.IsValid()) graph.Destroy();
            playables.Clear();
            graphClips.Clear();
            clipToInput.Clear();
            maskedPlayables.Clear();
            maskedClips.Clear();
            maskedClipToInput.Clear();
            appliedLayerMask = null;
            baseInput = prevBaseInput = shotInput = -1;
            shotPhase = ShotPhase.None;
            shotMasked = false;
            gestureQueued = false;
            queuedEntry = null;
            activeShot = null;
            shotWeight = 0f;
            loopEntries.Clear();
            currentLoop = null;
        }
    }
}
