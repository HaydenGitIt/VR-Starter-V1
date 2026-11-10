using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace catalyst.xr67
{
    /// <summary>
    /// Puts the LLM's agitation number (0 = happy and calm, 100 = as worked up
    /// as he gets, reported with every reply) on the hero's body and face.
    ///
    /// Body — while a reply is being spoken, the scenario's
    /// <see cref="ScenarioDefinition.speakingMoods"/> bands pick a loop for
    /// the reported agitation and the hero's AnimationSequencePlayer
    /// crossfades into it, back to the ambient sequence when the speech ends.
    /// No band (or a band without loops) = the ambient sequence keeps
    /// playing, exactly what he does without this component.
    ///
    /// Face — the agitation is smoothed and mapped onto two blendshape sets:
    /// <see cref="happyShapes"/> fade out as agitation rises and
    /// <see cref="angryShapes"/> fade in, with a neutral stretch between, so
    /// 0 reads as a slight friendly smile, 100 as a frown and everything in
    /// between as a blend. Written through ProceduralIdleMotion's held
    /// expression layer, so the resting smile, drift and micro-events keep
    /// playing on top and the lipsync visemes are untouched. The face holds
    /// its level between replies; when the scenario ends it keeps it (On
    /// Scenario End = Keep Current) or fades back (Settle Into End Phase).
    ///
    /// Timing — the agitation arrives with the end of the LLM stream, the
    /// voice starts a couple of seconds later (TTS), so the loop is normally
    /// chosen before the first word. If the voice ever starts first, the loop
    /// starts the moment the number lands.
    ///
    /// Wiring — add to the character container to tune it in the inspector;
    /// the session/scenario logic adds one with these defaults at runtime when
    /// the character has none, calls <see cref="BeginScenario"/> /
    /// <see cref="EndScenario"/>, and feeds it <see cref="ReplyPending"/> when
    /// the user has spoken and <see cref="SetAgitation"/> when the reply's
    /// number lands. Speech start/end is taken from the NpcSpeechPlayer found
    /// on the character.
    /// </summary>
    [DisallowMultipleComponent]
    public class AgitationResponder : MonoBehaviour
    {
        [Serializable]
        public class FaceShapeWeight
        {
            [Tooltip("CC blendshape on the body mesh, e.g. Mouth_Smile_L. Must be one ProceduralIdleMotion drives (brows, squint, cheeks, smile, frown, press, dimple, sneer, nostrils, wide eyes).")]
            public string blendShape;

            [Tooltip("Weight at full strength of this set, blendshape units 0..100.")]
            [Range(0f, 100f)]
            public float weight;

            public FaceShapeWeight() { }
            public FaceShapeWeight(string shape, float w) { blendShape = shape; weight = w; }
        }

        [Header("References (found on the character when empty)")]
        [Tooltip("Character being driven. Found on this GameObject or its parents when left empty.")]
        [SerializeField] private CharacterReferenceHolder character;

        [Tooltip("Ambient sequence player of the hero — the speaking loops are layered over it. Found under the character when left empty.")]
        [SerializeField] private AnimationSequencePlayer sequencePlayer;

        [Tooltip("Facial life layer of the hero — the mood expression is held through it. Found under the character when left empty.")]
        [SerializeField] private ProceduralIdleMotion idleMotion;

        [Tooltip("Speech playback — the loops run exactly while a reply is spoken. Found on the character, else in the scene, when left empty.")]
        [SerializeField] private NpcSpeechPlayer speechPlayer;

        [Tooltip("Live-actor animation driver — receives the conversation phase (idle loop + gesture pool from its profile's Calm / Transition / Agitated sets). Found under the character when left empty.")]
        [SerializeField] private StudentAmbientPlayer ambientPlayer;

        [Header("Conversation phase (body)")]
        [Tooltip("Phase the character starts in (from Start, so a scripted opening line already uses it) and returns to when a scenario begins. Only applied when the profile authors at least one phase set.")]
        [SerializeField] private ConversationPhase startPhase = ConversationPhase.Agitated;

        /// <summary>What the body and face do when the scenario ends.</summary>
        public enum ScenarioEnd
        {
            /// <summary>Stay in the phase and mood the conversation left her in.</summary>
            KeepCurrent,
            /// <summary>Settle into End Phase regardless of how the conversation went (the face fades back to neutral).</summary>
            SettleIntoEndPhase,
        }

        [Tooltip("When the scenario ends: Keep Current = she stays in the phase and mood the last reply reported, so the ending follows how the user handled her. Settle Into End Phase = always switch to End Phase. Scene logic can still pick the ending with SettleInto (TalkingCharacterDriver.EndConversation).")]
        [SerializeField] private ScenarioEnd onScenarioEnd = ScenarioEnd.KeepCurrent;

        [Tooltip("Phase the character settles into when the scenario ends. Only used when On Scenario End is Settle Into End Phase.")]
        [SerializeField] private ConversationPhase endPhase = ConversationPhase.Calm;

        [Tooltip("When the LLM picks a one-shot for a reply (the reply's \"gesture\" field), play it as the character speaks that reply.")]
        [SerializeField] private bool playLlmGestures = true;

        [Tooltip("The LLM rates how well its pick fits the reply (0..100). Picks rated below this are ignored. Raise it for fewer, more obvious gestures; lower it for more.")]
        [Range(0, 100)]
        [SerializeField] private int minGestureFit = 55;

        [Tooltip("At least this many replies without a one-shot between two LLM one-shots. 0 = back-to-back allowed.")]
        [Min(0)]
        [SerializeField] private int minRepliesBetweenGestures = 1;

        [Tooltip("A pick the LLM rates at least this well ignores Min Replies Between Gestures (the seconds still apply) — a strong moment isn't lost because a weaker gesture played on the reply before. 101 = never.")]
        [Range(0, 101)]
        [SerializeField] private int strongGestureFit = 75;

        [Tooltip("At least this many seconds between the start of one LLM one-shot and the next.")]
        [Min(0f)]
        [SerializeField] private float minSecondsBetweenGestures = 12f;

        [Tooltip("When a reply comes without an LLM-picked one-shot (or for scripted lines), play a random one from the current phase's pool as the character starts speaking. Off = only the LLM's picks play.")]
        [SerializeField] private bool randomGestureOnSpeech;

        [Tooltip("Random delay (seconds) after the voice starts before the gesture begins, so it lands on her first words rather than before them. It also waits for a running idle-loop crossfade (e.g. the phase change of the same reply) to finish first.")]
        [SerializeField] private Vector2 gestureDelay = new Vector2(0.4f, 0.9f);

        [Header("Face")]
        [Tooltip("Master switch for the face blend on this character. The scenario's Drive Face From Agitation must be on as well.")]
        [SerializeField] private bool driveFace = true;

        [Tooltip("Scales both shape sets. 1 = the weights as authored below; keep it modest — big smiles read as creepy.")]
        [Range(0f, 2f)]
        [SerializeField] private float faceIntensity = 1f;

        [Tooltip("Seconds for the face to settle on a newly reported agitation (smooth damp time). Short = snappy mood swings, long = drifts.")]
        [Min(0.05f)]
        [SerializeField] private float faceSmoothSeconds = 1.2f;

        [Tooltip("Seconds for the mood expression to fade in when the scenario starts driving the face (and back out when it ends), so it never snaps on.")]
        [Min(0f)]
        [SerializeField] private float faceFadeSeconds = 2.5f;

        [Tooltip("Agitation (0..1) at which the happy set has fully faded out. Below it the smile shrinks gradually from full at 0.")]
        [Range(0.05f, 1f)]
        [SerializeField] private float happyFadesOutAt = 0.3f;

        [Tooltip("Agitation (0..1) at which the angry set starts to appear. Overlapping the happy fade gives a blend, a gap gives a neutral stretch.")]
        [Range(0f, 0.95f)]
        [SerializeField] private float angryStartsAt = 0.25f;

        [Tooltip("Agitation (0..1) at which the angry set is at full strength; anything above holds it there. Lower = the frown shows sooner and harder.")]
        [Range(0.3f, 1f)]
        [SerializeField] private float angryFullAt = 0.8f;

        [Tooltip("Face at agitation 0: a slight, friendly smile. Weights are at full strength; they scale down to 0 at Happy Fades Out At.")]
        [SerializeField] private FaceShapeWeight[] happyShapes = DefaultHappyShapes();

        [Tooltip("Face at high agitation: a frown with lowered brows. Weights are at full strength; they scale up from 0 at Angry Starts At to full at Angry Full At.")]
        [SerializeField] private FaceShapeWeight[] angryShapes = DefaultAngryShapes();

        [Tooltip("Agitation assumed before the first reply of a scenario (0..100) — where the face rests as the scenario opens and where it returns to when it ends.")]
        [Range(0, 100)]
        [SerializeField] private int restingAgitation = 30;

        [Header("Debug")]
        [Tooltip("Log every phase the LLM reports and which idle loop / gesture played because of it. Independent of Verbose Logging.")]
        [SerializeField] private bool logPhaseAndAnimations = true;

        [Tooltip("Log a [TestFlow] frame_hitch line when a frame takes longer than this (ms), with what the body was doing (one-shot, loop crossfade, speaking). At most one line a second. 0 = off.")]
        [Min(0f)]
        [SerializeField] private float logFrameHitchesOverMs = 50f;

        private float _hitchLogAt, _hitchWorstMs;
        private int _hitchCount;

        [SerializeField] private bool verboseLogging;

        /// <summary>Agitation reported with the latest reply of the running scenario, -1 before the first one.</summary>
        public int CurrentAgitation => _agitation;

        /// <summary>Loop currently layered over the ambient sequence, null when the sequence owns the pose.</summary>
        public AnimationClip ActiveLoop => _activeLoop;

        /// <summary>Conversation phase last applied to the body.</summary>
        public ConversationPhase CurrentPhase => _phase;

        private ConversationPhase _phase;
        private float _gestureAt = -1f;            // Time.time to fire the speech gesture, -1 = none scheduled
        private bool _gestureAwaitsReply;          // voice started before the reply's phase landed
        private string _pendingGesture;            // LLM-picked one-shot id waiting for (or due during) the voice
        private int _repliesSinceGesture = int.MaxValue / 2;   // replies since the last LLM one-shot played
        private float _lastGestureTime = -1000f;               // Time.time the last LLM one-shot started

        private IReadOnlyList<AgitationResponse> _moods;
        private bool _scenarioDrivesFace;
        private int _agitation = -1;
        private bool _replyPending;      // a reply is on its way and its agitation is not known yet
        private bool _speaking;
        private AgitationResponse _activeBand;
        private AnimationClip _activeLoop;
        private float _faceLevel, _faceVelocity;
        private float _facePresence;   // 0..1 fade of the whole mood expression in / out
        private bool _faceHeld;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            ResolveReferences();
        }

        private void ResolveReferences()
        {
            if (character == null) character = GetComponentInParent<CharacterReferenceHolder>();
            if (character == null) character = GetComponentInChildren<CharacterReferenceHolder>(true);

            GameObject root = character != null ? character.Root : gameObject;
            GameObject holder = character != null ? character.gameObject : gameObject;

            if (sequencePlayer == null) sequencePlayer = root.GetComponentInChildren<AnimationSequencePlayer>(true);
            if (sequencePlayer == null) sequencePlayer = holder.GetComponentInChildren<AnimationSequencePlayer>(true);

            if (idleMotion == null) idleMotion = root.GetComponentInChildren<ProceduralIdleMotion>(true);
            if (idleMotion == null) idleMotion = holder.GetComponentInChildren<ProceduralIdleMotion>(true);

            if (speechPlayer == null) speechPlayer = holder.GetComponentInChildren<NpcSpeechPlayer>(true);
            if (speechPlayer == null) speechPlayer = root.GetComponentInChildren<NpcSpeechPlayer>(true);
            if (speechPlayer == null) speechPlayer = FindFirstObjectByType<NpcSpeechPlayer>(FindObjectsInactive.Include);

            if (ambientPlayer == null) ambientPlayer = root.GetComponentInChildren<StudentAmbientPlayer>(true);
            if (ambientPlayer == null) ambientPlayer = holder.GetComponentInChildren<StudentAmbientPlayer>(true);

            if (sequencePlayer == null)
                Debug.LogWarning("AgitationResponder: no AnimationSequencePlayer on the character — speaking loops are off.", this);
            if (idleMotion == null)
                Debug.LogWarning("AgitationResponder: no ProceduralIdleMotion on the character — the face blend is off.", this);
            if (speechPlayer == null)
                Debug.LogWarning("AgitationResponder: no NpcSpeechPlayer found — the speaking loops cannot follow the voice.", this);
        }

        private void Start()
        {
            // Before any scenario: a scripted opening line already plays in
            // the start phase.
            if (PhasesAuthored) ApplyPhase(startPhase, "start");
        }

        private void OnEnable()
        {
            if (speechPlayer != null)
            {
                speechPlayer.OnSpeechStarted += HandleSpeechStarted;
                speechPlayer.OnSpeechFinished += HandleSpeechFinished;
            }
            if (ambientPlayer != null)
            {
                ambientPlayer.OneShotStarted += HandleOneShotStarted;
                ambientPlayer.OneShotEnded += HandleOneShotEnded;
                ambientPlayer.LoopChanged += HandleLoopChanged;
            }
        }

        private void OnDisable()
        {
            if (speechPlayer != null)
            {
                speechPlayer.OnSpeechStarted -= HandleSpeechStarted;
                speechPlayer.OnSpeechFinished -= HandleSpeechFinished;
            }
            if (ambientPlayer != null)
            {
                ambientPlayer.OneShotStarted -= HandleOneShotStarted;
                ambientPlayer.OneShotEnded -= HandleOneShotEnded;
                ambientPlayer.LoopChanged -= HandleLoopChanged;
            }
            StopLoop();
            ReleaseFace();
        }

        // ------------------------------------------------------------------ scenario hooks

        /// <summary>
        /// A scenario went live: take its bands and face switch, forget the
        /// previous run's agitation. Called by ScenarioLogic on start.
        /// </summary>
        public void BeginScenario(IReadOnlyList<AgitationResponse> moods, bool driveFaceForScenario)
        {
            _moods = moods;
            _scenarioDrivesFace = driveFaceForScenario;
            _agitation = -1;
            _replyPending = false;
            _pendingGesture = null;
            _repliesSinceGesture = int.MaxValue / 2;
            _lastGestureTime = -1000f;
            // A face still held from the previous run eases to the resting
            // level instead of snapping there.
            if (_facePresence <= 0f)
            {
                _faceLevel = restingAgitation / 100f;
                _faceVelocity = 0f;
            }
            StopLoop();
            if (PhasesAuthored) ApplyPhase(startPhase, "scenario begins");
            Log($"scenario begins — {CountBands(moods)} agitation band(s), face {(driveFaceForScenario && driveFace ? "on" : "off")}");
        }

        /// <summary>
        /// The scenario ended: speaking loops and pending gestures off. With
        /// On Scenario End = Keep Current the body stays in its phase and the
        /// face keeps the last reported mood; with Settle Into End Phase the
        /// body switches to End Phase and the face fades back to neutral.
        /// Called by CharacterSession.End.
        /// </summary>
        public void EndScenario()
        {
            _moods = null;
            _replyPending = false;
            _gestureAwaitsReply = false;
            _gestureAt = -1f;
            _pendingGesture = null;
            StopLoop();

            if (onScenarioEnd == ScenarioEnd.SettleIntoEndPhase)
            {
                // The face fades out in LateUpdate (Face Fade Seconds) rather than snapping off.
                _scenarioDrivesFace = false;
                if (PhasesAuthored) ApplyPhase(endPhase, "scenario ends");
                return;
            }

            // Keep Current: the face stays driven at the last agitation and
            // the body keeps its phase, so the ending is what the user made it.
            Flow("scenario_end",
                $"scenario ends — keeping phase \"{PhaseWord(_phase)}\" and agitation {(_agitation >= 0 ? _agitation.ToString() : "none reported")} (On Scenario End = Keep Current).",
                "phase", PhaseWord(_phase), "agitation", _agitation >= 0 ? (object)_agitation : null);
        }

        /// <summary>
        /// Scene logic decides how the character ends up: the body crossfades
        /// into <paramref name="phase"/> and the face and mood-blended loops
        /// ease to a mood that sits squarely in it. Works during or after a
        /// scenario. Call after the session ended (TalkingCharacterDriver.EndConversation
        /// does), or the next reply's phase replaces it.
        /// </summary>
        public void SettleInto(ConversationPhase phase)
        {
            StudentBehaviourProfile p = ambientPlayer != null ? ambientPlayer.profile : null;
            int mood = p != null
                ? Mathf.Clamp(Mathf.RoundToInt(p.MoodFor(phase)), 0, 100)
                : phase == ConversationPhase.Calm ? 10 : phase == ConversationPhase.Transition ? 45 : 85;
            _agitation = mood;
            if (ambientPlayer != null) ambientPlayer.SetMood(mood);
            _gestureAwaitsReply = false;
            if (PhasesAuthored) ApplyPhase(phase, "scene logic");
        }

        /// <summary>The one-shots the LLM may pick for this character (its profile's phase sets), empty when none.</summary>
        public List<StudentBehaviourProfile.LlmGesture> LlmGestureOptions()
        {
            StudentBehaviourProfile p = ambientPlayer != null ? ambientPlayer.profile : null;
            List<StudentBehaviourProfile.LlmGesture> options = p != null && playLlmGestures
                ? p.LlmGestures() : new List<StudentBehaviourProfile.LlmGesture>();
            var names = new List<string>();
            foreach (StudentBehaviourProfile.LlmGesture g in options)
                names.Add($"{g.id} ({string.Join("/", g.phases.ConvertAll(PhaseWord))})");
            Flow("gesture_catalog",
                options.Count > 0
                    ? $"one-shots offered to the LLM: {string.Join(", ", names)}."
                    : "no one-shots offered to the LLM (none authored with Offer To Llm, or Play Llm Gestures is off).",
                "gestures", options.ConvertAll(g => g.id),
                "described", options.ConvertAll(g => g.description ?? ""));
            return options;
        }

        /// <summary>
        /// The LLM's one-shot pick for the reply on its way (null = none) and
        /// how well it says it fits (0..100, -1 unknown). A pick that clears
        /// <see cref="minGestureFit"/> and the cooldowns plays as the
        /// character speaks that reply; anything else is logged as skipped.
        /// Call once per reply.
        /// </summary>
        public void PlayChosenGesture(string id, int fit = -1)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                _repliesSinceGesture++;
                return;
            }
            if (!playLlmGestures || ambientPlayer == null) return;

            string phase = PhaseWord(_phase);
            string reason = null, human = null;
            float since = Time.time - _lastGestureTime;
            if (fit >= 0 && fit < minGestureFit)
            {
                reason = "low fit";
                human = $"fit {fit} is below Min Gesture Fit {minGestureFit}";
            }
            else if (_repliesSinceGesture < minRepliesBetweenGestures && fit < strongGestureFit)
            {
                reason = "cooldown";
                human = $"only {_repliesSinceGesture} repl{(_repliesSinceGesture == 1 ? "y" : "ies")} since the last one-shot (min {minRepliesBetweenGestures})";
            }
            else if (since < minSecondsBetweenGestures)
            {
                reason = "cooldown";
                human = $"only {since:0} s since the last one-shot (min {minSecondsBetweenGestures:0} s)";
            }

            if (reason != null)
            {
                _repliesSinceGesture++;
                Flow("gesture_skipped", $"LLM picked one-shot '{id}' — not played: {human}.",
                    "reason", reason, "gesture", id, "fit", fit >= 0 ? fit : (object)null, "min_fit", minGestureFit,
                    "replies_since", _repliesSinceGesture > 9999 ? null : (object)(_repliesSinceGesture - 1), "seconds_since", since < 999f ? since : (object)null,
                    "phase", phase, "source", "llm");
                return;
            }

            _pendingGesture = id;
            if (_speaking) ScheduleGesture();
        }

        // ------------------------------------------------------------------ phase (body)

        /// <summary>True when the ambient player's profile authors at least one Calm / Transition / Agitated set.</summary>
        private bool PhasesAuthored => ambientPlayer != null && ambientPlayer.profile != null && ambientPlayer.profile.HasPhases;

        /// <summary>
        /// The phase the latest reply reported: the body crossfades into that
        /// phase's idle loop and gesture pool right away (the reply text lands
        /// before the voice, so the loop is in place for the first word).
        /// </summary>
        public void SetPhase(ConversationPhase phase)
        {
            ApplyPhase(phase, "reply");
            if (_gestureAwaitsReply && _speaking) ScheduleGesture();
            _gestureAwaitsReply = false;
        }

        private void ApplyPhase(ConversationPhase phase, string reason)
        {
            ConversationPhase previous = _phase;
            bool hadPhase = ambientPlayer != null && ambientPlayer.UsesPhases;
            _phase = phase;
            string source = reason == "reply" ? $"LLM reply says \"{PhaseWord(phase)}\"" : $"{reason}: phase \"{PhaseWord(phase)}\"";

            if (ambientPlayer == null)
            {
                Flow("phase", $"{source} — no StudentAmbientPlayer on the character, the body animation does not follow it.",
                    "phase", PhaseWord(phase), "reason", reason, "loop", null);
                return;
            }

            bool changed = !hadPhase || previous != phase;
            ambientPlayer.SetPhase(phase);

            StudentBehaviourProfile.ModeSet set = ambientPlayer.CurrentSet;
            string loop = ambientPlayer.BaseLoopClip != null ? ambientPlayer.BaseLoopClip.name : "none";
            bool fallback = ambientPlayer.profile != null && set == ambientPlayer.profile.listening;
            List<string> pool = PoolNames(set);
            string poolText = pool.Count > 0 ? string.Join(", ", pool) : "empty";
            var loops = new List<string>();
            foreach (StudentBehaviourProfile.LoopEntry entry in ambientPlayer.CurrentLoops)
                loops.Add(entry.clip.name);
            bool timer = set != null && set.timerOneShots;
            string loopText = loops.Count > 1
                ? $"IDLE LOOPS [{string.Join(", ", loops)}] rotating at random, now '{loop}' (~{ambientPlayer.LoopTimeLeft:0} s)"
                : $"IDLE LOOP '{loop}' (loops until the phase changes)";
            string shotText = timer
                ? $"One-shots on the idle timer ({set.oneShotInterval.x:0.#}–{set.oneShotInterval.y:0.#} s): {poolText}."
                : $"One-shots only when the LLM picks one: {poolText}.";

            string human = changed
                ? $"{source} (was {(hadPhase ? PhaseWord(previous) : "not set")}) → {loopText}"
                    + $"{(fallback ? " [no " + PhaseWord(phase) + " set authored — using Listening]" : "")}"
                    + $"{(set != null ? $", blend {set.modeBlendSeconds:0.#} s" : "")}. {shotText}"
                : $"{source} (unchanged) → keeps {(loops.Count > 1 ? $"rotating idle loops, now '{loop}'" : $"IDLE LOOP '{loop}'")}.";
            bool blendByMood = ambientPlayer.MoodBlending;
            string mix = blendByMood ? ambientPlayer.LoopMixText() : null;
            if (blendByMood)
                human += $" Loops follow the eased mood ({ambientPlayer.EasedMood:0}): {mix}; '{loop}' carries on until the rotation moves on.";
            Flow("phase", human,
                "mood_eased", blendByMood ? (object)ambientPlayer.EasedMood : null,
                "mix", mix,
                "phase", PhaseWord(phase),
                "previous", hadPhase ? PhaseWord(previous) : null,
                "reason", reason,
                "changed", changed,
                "loop", loop,
                "loops", loops,
                "fallback", fallback,
                "blend_s", set != null ? set.modeBlendSeconds : 0f,
                "timer_oneshots", timer,
                "interval", set != null ? new[] { set.oneShotInterval.x.ToString("0.#"), set.oneShotInterval.y.ToString("0.#") } : null,
                "pool", pool);
        }

        /// <summary>
        /// Collects slow frames and logs them at most once a second with what
        /// the body was doing then — to tell whether hitches line up with
        /// one-shots / crossfades or with something else (speech, the LLM).
        /// </summary>
        private void TrackFrameHitches()
        {
            if (logFrameHitchesOverMs <= 0f || !logPhaseAndAnimations || Time.frameCount < 120) return;
            float ms = Time.unscaledDeltaTime * 1000f;
            if (ms >= logFrameHitchesOverMs)
            {
                _hitchCount++;
                _hitchWorstMs = Mathf.Max(_hitchWorstMs, ms);
            }
            if (_hitchCount == 0 || Time.realtimeSinceStartup < _hitchLogAt) return;

            string shot = ambientPlayer != null ? ambientPlayer.ActiveOneShotName : null;
            bool blending = ambientPlayer != null && ambientPlayer.IsLoopBlending;
            string loop = ambientPlayer != null && ambientPlayer.BaseLoopClip != null ? ambientPlayer.BaseLoopClip.name : null;
            string doing = shot != null ? $"one-shot '{shot}'" : blending ? $"idle crossfade into '{loop}'" : $"idle loop '{loop}'";
            Flow("frame_hitch",
                $"SLOW FRAME{(_hitchCount > 1 ? $"S ×{_hitchCount}" : "")}: worst {_hitchWorstMs:0} ms ({1000f / Mathf.Max(_hitchWorstMs, 1f):0} fps) during {doing}{(_speaking ? ", while speaking" : "")}{(Application.isEditor ? " [Editor]" : "")}.",
                "worst_ms", _hitchWorstMs, "count", _hitchCount, "oneshot", shot, "loop_blending", blending, "loop", loop,
                "speaking", _speaking, "editor", Application.isEditor);
            _hitchCount = 0;
            _hitchWorstMs = 0f;
            _hitchLogAt = Time.realtimeSinceStartup + 1f;
        }

        private void HandleLoopChanged(AnimationClip clip, string reason, float hold)
        {
            // Set switches are logged by ApplyPhase; this covers the rotation.
            if (reason != "rotation" && reason != "mood") return;
            string clipName = clip != null ? clip.name : "none";
            bool blend = ambientPlayer != null && ambientPlayer.MoodBlending;
            string mix = blend ? ambientPlayer.LoopMixText() : null;
            float eased = blend ? ambientPlayer.EasedMood : -1f;
            string why = reason == "mood"
                ? $"the eased mood ({eased:0}) moved on and the previous loop no longer fit"
                : blend ? $"random rotation, mood {eased:0} → mix {mix}" : $"random rotation within phase \"{CurrentPhaseWord ?? "ambient"}\"";
            Flow("loop", $"IDLE LOOP → '{clipName}' ({why}, next change in ~{hold:0} s).",
                "loop", clipName, "reason", reason, "hold_s", hold, "phase", CurrentPhaseWord,
                "mood_eased", eased >= 0f ? (object)eased : null, "mix", mix);
        }

        private static string PhaseWord(ConversationPhase phase) => phase.ToString().ToLowerInvariant();

        private static List<string> PoolNames(StudentBehaviourProfile.ModeSet set)
        {
            var names = new List<string>();
            if (set == null || set.oneShots == null) return names;
            foreach (StudentBehaviourProfile.OneShotEntry entry in set.oneShots)
            {
                if (entry == null || entry.clip == null || entry.weight <= 0f) continue;
                names.Add(StudentAmbientPlayer.EntryName(entry));
            }
            return names;
        }

        private string CurrentPhaseWord =>
            ambientPlayer != null && ambientPlayer.UsesPhases ? PhaseWord(ambientPlayer.Phase) : null;

        private void HandleOneShotStarted(StudentBehaviourProfile.OneShotEntry entry, string reason)
        {
            string phase = CurrentPhaseWord;
            string why = reason switch
            {
                "llm" => "the LLM picked it for this reply",
                "llm (queued)" => "the LLM picked it for this reply (after the previous one-shot faded)",
                "speech" => "random pick as she started speaking",
                "speech (queued)" => "random pick as she started speaking (waited for the previous one-shot to fade)",
                "timer" => "idle timer",
                "manual" => "triggered from the inspector",
                _ => reason,
            };
            string layer = entry != null && entry.mask != null ? $" on mask '{entry.mask.name}'" : "";
            float duration = ambientPlayer != null ? ambientPlayer.ActiveOneShotDuration : 0f;
            string loop = ambientPlayer != null && ambientPlayer.BaseLoopClip != null ? ambientPlayer.BaseLoopClip.name : "none";
            Flow("oneshot_start",
                $"ONE-SHOT ▶ '{StudentAmbientPlayer.EntryName(entry)}'{layer} plays once (~{duration:0.0} s) over idle loop '{loop}' — {why}, {(phase != null ? $"phase \"{phase}\"" : "ambient mode")}.",
                "label", entry != null ? entry.label : null,
                "clip", entry != null && entry.clip != null ? entry.clip.name : null,
                "name", StudentAmbientPlayer.EntryName(entry),
                "reason", reason,
                "why", why,
                "phase", phase,
                "mask", entry != null && entry.mask != null ? entry.mask.name : null,
                "duration_s", duration,
                "speed", ambientPlayer != null ? ambientPlayer.ActiveOneShotSpeed : 1f,
                "loop", loop,
                "speaking", _speaking);
        }

        private void HandleOneShotEnded(StudentBehaviourProfile.OneShotEntry entry, bool cut)
        {
            string loop = ambientPlayer != null && ambientPlayer.BaseLoopClip != null ? ambientPlayer.BaseLoopClip.name : "none";
            Flow("oneshot_end",
                $"ONE-SHOT ■ '{StudentAmbientPlayer.EntryName(entry)}' {(cut ? "cut short (phase change or a newer one-shot)" : "finished")} — back to idle loop '{loop}'.",
                "label", entry != null ? entry.label : null,
                "clip", entry != null && entry.clip != null ? entry.clip.name : null,
                "name", StudentAmbientPlayer.EntryName(entry),
                "cut", cut,
                "phase", CurrentPhaseWord,
                "loop", loop);
        }

        private void Flow(string ev, string human, params object[] fields)
        {
            if (logPhaseAndAnimations) TestFlowLog.Emit(ev, $"[{name}] {human}", this, fields);
        }

        /// <summary>Arms the speech gesture: fires a short random delay from now (see Update).</summary>
        private void ScheduleGesture()
        {
            if (ambientPlayer == null || !ambientPlayer.UsesPhases) return;
            if (_pendingGesture == null && !randomGestureOnSpeech) return;
            float lo = Mathf.Max(0f, Mathf.Min(gestureDelay.x, gestureDelay.y));
            float hi = Mathf.Max(0f, Mathf.Max(gestureDelay.x, gestureDelay.y));
            _gestureAt = Time.time + Random.Range(lo, hi);
        }

        private void Update()
        {
            TrackFrameHitches();
            if (_gestureAt < 0f || Time.time < _gestureAt) return;
            if (ambientPlayer == null || !_speaking) { _gestureAt = -1f; return; }
            // One transition at a time: let the idle loop finish crossfading
            // (typically this reply's phase change) before the one-shot starts.
            if (ambientPlayer.IsLoopBlending) return;
            _gestureAt = -1f;

            // The started gesture itself is logged by HandleOneShotStarted.
            if (_pendingGesture != null)
            {
                string id = _pendingGesture;
                _pendingGesture = null;
                StudentAmbientPlayer.GestureResult result = ambientPlayer.PlayGesture(id, "llm", out _);
                if (result == StudentAmbientPlayer.GestureResult.Started || result == StudentAmbientPlayer.GestureResult.Queued)
                {
                    _repliesSinceGesture = 0;
                    _lastGestureTime = Time.time;
                }
                if (result == StudentAmbientPlayer.GestureResult.Queued)
                    Flow("gesture_queued", $"LLM one-shot '{id}' waits for the previous one-shot to fade (phase \"{PhaseWord(_phase)}\").",
                        "gesture", id, "active", ambientPlayer.ActiveOneShotName, "phase", PhaseWord(_phase), "source", "llm");
                else if (result != StudentAmbientPlayer.GestureResult.Started)
                    Flow("gesture_skipped", $"LLM picked one-shot '{id}' but {(result == StudentAmbientPlayer.GestureResult.NotFound ? "no phase set has it (or it is excluded)" : "the player has no animation graph")}.",
                        "reason", result == StudentAmbientPlayer.GestureResult.NotFound ? "not found" : "unavailable", "gesture", id, "phase", PhaseWord(_phase), "source", "llm");
                return;
            }

            if (!randomGestureOnSpeech) return;
            if (!ambientPlayer.PlayGestureNow("speech"))
            {
                bool busy = ambientPlayer.IsOneShotActive;
                string why = busy ? $"'{ambientPlayer.ActiveOneShotName}' is still playing" : "the one-shot pool is empty";
                Flow("gesture_skipped", $"she started speaking, no random one-shot — {why} (phase \"{PhaseWord(_phase)}\").",
                    "reason", busy ? "busy" : "empty pool", "active", busy ? ambientPlayer.ActiveOneShotName : null, "phase", PhaseWord(_phase), "source", "random");
            }
            else if (ambientPlayer.GestureQueued)
            {
                Flow("gesture_queued", $"she started speaking — random one-shot queued until '{ambientPlayer.ActiveOneShotName}' fades (phase \"{PhaseWord(_phase)}\").",
                    "active", ambientPlayer.ActiveOneShotName, "phase", PhaseWord(_phase), "source", "random");
            }
        }

        private static int CountBands(IReadOnlyList<AgitationResponse> moods)
        {
            if (moods == null) return 0;
            int n = 0;
            foreach (AgitationResponse band in moods)
                if (band != null && band.HasLoops) n++;
            return n;
        }

        // ------------------------------------------------------------------ conversation input

        /// <summary>
        /// The user said something and a reply is on its way: its agitation
        /// is unknown until the stream ends, so should the voice beat the
        /// number, the loop waits for it. Call before the LLM request goes out.
        /// </summary>
        public void ReplyPending()
        {
            _replyPending = true;
            _pendingGesture = null;   // a pick belongs to one reply only
        }

        /// <summary>
        /// The agitation the latest reply reported (0..100; negative = the
        /// reply carried none, the previous level is kept). Call once the
        /// reply has fully arrived.
        /// </summary>
        public void SetAgitation(int agitation)
        {
            _replyPending = false;
            // A reply without a phase still releases a gesture that waited for it.
            if (_gestureAwaitsReply && _speaking) ScheduleGesture();
            _gestureAwaitsReply = false;
            if (agitation < 0)
            {
                // Malformed reply — keep the previous mood rather than jumping.
                Log("reply carried no agitation — keeping the previous level");
                return;
            }
            _agitation = Mathf.Clamp(agitation, 0, 100);
            if (ambientPlayer != null) ambientPlayer.SetMood(_agitation);
            Log($"agitation {_agitation}");
            if (_speaking && _activeLoop == null) StartLoop();
        }

        private void HandleSpeechStarted()
        {
            _speaking = true;
            Flow("speech_start", "voice starts.", "phase", CurrentPhaseWord, "reply_pending", _replyPending);
            if (!_replyPending)
            {
                StartLoop();
                ScheduleGesture();
            }
            else
            {
                Log("voice started before the agitation landed — loop and gesture wait for it");
                _gestureAwaitsReply = true;
            }
        }

        private void HandleSpeechFinished()
        {
            _speaking = false;
            Flow("speech_end", "voice ends.", "phase", CurrentPhaseWord);
            if (_gestureAt >= 0f && _pendingGesture != null)
                Flow("gesture_skipped", $"LLM one-shot '{_pendingGesture}' did not start — the voice ended first.",
                    "reason", "voice ended", "gesture", _pendingGesture, "phase", CurrentPhaseWord, "source", "llm");
            _gestureAt = -1f;
            _gestureAwaitsReply = false;
            _pendingGesture = null;
            StopLoop();
        }

        // ------------------------------------------------------------------ body

        private void StartLoop()
        {
            if (sequencePlayer == null || _moods == null || _agitation < 0) return;
            if (_activeLoop != null) return;

            AgitationResponse band = null;
            foreach (AgitationResponse candidate in _moods)
            {
                if (candidate == null || !candidate.Contains(_agitation)) continue;
                band = candidate;
                break; // first matching band wins, loops or not
            }
            if (band == null || !band.HasLoops)
            {
                Log($"no speaking loop for agitation {_agitation} — ambient sequence continues");
                return;
            }

            AnimationClip clip = PickLoop(band.speakingLoops);
            if (clip == null) return;
            float speed = Random.Range(Mathf.Min(band.speedRange.x, band.speedRange.y), Mathf.Max(band.speedRange.x, band.speedRange.y));
            if (speed <= 0f) speed = 1f;

            _activeBand = band;
            _activeLoop = clip;
            sequencePlayer.PlayOverride(clip, band.blendIn, speed);
            Log($"speaking loop '{clip.name}' (agitation {_agitation}, band {band.minAgitation}-{band.maxAgitation}, speed {speed:0.00})");
        }

        private static AnimationClip PickLoop(List<AnimationClip> loops)
        {
            int count = 0;
            foreach (AnimationClip clip in loops)
                if (clip != null) count++;
            if (count == 0) return null;
            int pick = Random.Range(0, count);
            foreach (AnimationClip clip in loops)
            {
                if (clip == null) continue;
                if (pick-- == 0) return clip;
            }
            return null;
        }

        private void StopLoop()
        {
            if (_activeLoop == null) return;
            float blendOut = _activeBand != null ? _activeBand.blendOut : 0.8f;
            if (sequencePlayer != null) sequencePlayer.StopOverride(blendOut);
            Log($"speaking loop '{_activeLoop.name}' ends");
            _activeLoop = null;
            _activeBand = null;
        }

        // ------------------------------------------------------------------ face

        private bool FaceActive => driveFace && _scenarioDrivesFace && idleMotion != null;

        private void LateUpdate()
        {
            // Order: ProceduralIdleMotion queues its facial step at -12, this
            // runs at 0, and its FaceWriter flushes the step at +5 — so the
            // offsets set here reach the mesh in the same frame. They are
            // re-added every frame; Clear + Add is the contract.
            // The expression fades in when the face switches on and out when it
            // switches off, instead of snapping (the mood keeps its last value
            // while fading out).
            bool active = FaceActive;
            float fadeStep = faceFadeSeconds > 0f ? Time.deltaTime / faceFadeSeconds : 1f;
            _facePresence = Mathf.MoveTowards(_facePresence, active ? 1f : 0f, fadeStep);
            if (idleMotion == null || (!active && (_facePresence <= 0f || !driveFace)))
            {
                _facePresence = active ? _facePresence : 0f;
                if (_faceHeld) ReleaseFace();
                return;
            }
            float presence = Mathf.SmoothStep(0f, 1f, _facePresence);

            int agitation = _agitation >= 0 ? _agitation : restingAgitation;
            // The phase can hold the face at a minimum (e.g. a visible frown in Transition).
            if (ambientPlayer != null && ambientPlayer.UsesPhases)
            {
                StudentBehaviourProfile.ModeSet set = ambientPlayer.CurrentSet;
                if (set != null && set.faceAgitationFloor >= 0) agitation = Mathf.Max(agitation, set.faceAgitationFloor);
            }
            float target = agitation / 100f;
            _faceLevel = Mathf.SmoothDamp(_faceLevel, target, ref _faceVelocity, faceSmoothSeconds);
            float level = Mathf.Clamp01(_faceLevel);

            float happy = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, happyFadesOutAt, level));
            float angry = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(angryStartsAt, Mathf.Max(angryFullAt, angryStartsAt + 0.05f), level));

            idleMotion.ClearExpressionOffsets();
            AddSet(happyShapes, happy * faceIntensity * presence);
            AddSet(angryShapes, angry * faceIntensity * presence);
            _faceHeld = true;
        }

        private void AddSet(FaceShapeWeight[] set, float scale)
        {
            if (set == null || scale <= 0f) return;
            foreach (FaceShapeWeight shape in set)
            {
                if (shape == null || string.IsNullOrEmpty(shape.blendShape)) continue;
                idleMotion.AddExpressionOffset(shape.blendShape, shape.weight * scale);
            }
        }

        private void ReleaseFace()
        {
            if (idleMotion != null) idleMotion.ClearExpressionOffsets();
            _faceHeld = false;
        }

        // ------------------------------------------------------------------ defaults

        /// <summary>A slight friendly smile — corners up a little, a touch of cheek, a hint of squint. Deliberately small.</summary>
        private static FaceShapeWeight[] DefaultHappyShapes() => new[]
        {
            new FaceShapeWeight("Mouth_Smile_L", 20f),
            new FaceShapeWeight("Mouth_Smile_R", 20f),
            new FaceShapeWeight("Cheek_Raise_L", 10f),
            new FaceShapeWeight("Cheek_Raise_R", 10f),
            new FaceShapeWeight("Eye_Squint_L", 5f),
            new FaceShapeWeight("Eye_Squint_R", 5f),
            new FaceShapeWeight("Brow_Raise_Inner_L", 4f),
            new FaceShapeWeight("Brow_Raise_Inner_R", 4f),
        };

        /// <summary>Annoyed — brows down and in, a squint, corners down, lips pressed, a little sneer and flared nostrils.</summary>
        private static FaceShapeWeight[] DefaultAngryShapes() => new[]
        {
            new FaceShapeWeight("Brow_Drop_L", 55f),
            new FaceShapeWeight("Brow_Drop_R", 55f),
            new FaceShapeWeight("Brow_Compress_L", 45f),
            new FaceShapeWeight("Brow_Compress_R", 45f),
            new FaceShapeWeight("Eye_Squint_L", 22f),
            new FaceShapeWeight("Eye_Squint_R", 22f),
            new FaceShapeWeight("Mouth_Frown_L", 28f),
            new FaceShapeWeight("Mouth_Frown_R", 28f),
            new FaceShapeWeight("Mouth_Press_L", 22f),
            new FaceShapeWeight("Mouth_Press_R", 22f),
            new FaceShapeWeight("Nose_Sneer_L", 16f),
            new FaceShapeWeight("Nose_Sneer_R", 16f),
            new FaceShapeWeight("Nose_Nostril_Dilate_L", 18f),
            new FaceShapeWeight("Nose_Nostril_Dilate_R", 18f),
        };

        private void Log(string message)
        {
            if (verboseLogging) Debug.Log($"AgitationResponder ({name}): {message}", this);
        }
    }
}
