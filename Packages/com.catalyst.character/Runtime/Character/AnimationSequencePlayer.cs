using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace catalyst.xr67
{
    /// <summary>
    /// Plays a list of clips as one looping cycle with crossfades: each step
    /// holds its clip, blends into the next, and the cycle closes by blending
    /// back into the starting pose — the first clip at its Start Offset — so
    /// it repeats seamlessly every <see cref="CycleDuration"/> seconds.
    /// Per-step Speed / Start Offset let several characters share the same
    /// clips without moving in sync. <see cref="totalDuration"/> overrides
    /// the cycle length: at that time minus <see cref="loopBlend"/> the
    /// sequence heads back to the starting pose no matter where it is, so
    /// clips recorded for one video atlas all get the exact same duration.
    /// Drives the Animator through a mixer PlayableGraph — no
    /// AnimatorController needed — and works the same in Play Mode and Edit
    /// Mode: tick <see cref="playInEditMode"/> to watch it run, or scrub
    /// <see cref="previewTime"/> to inspect a specific moment.
    /// <see cref="Sample"/> is the deterministic entry point for stepping it
    /// from a recorder. The first clip should be loop-enabled, since the
    /// cycle starts and ends inside it. Root motion is suppressed so the
    /// character stays anchored. A sibling <see cref="EditorAnimationPreview"/>
    /// yields to this while Play In Edit Mode is on; don't combine with
    /// RuntimeClipLoop.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class AnimationSequencePlayer : MonoBehaviour, IPoseSampler
    {
        [Serializable]
        public class Step
        {
            [Tooltip("Clip this step plays.")]
            public AnimationClip clip;

            [Tooltip("Seconds the clip plays at full weight, after its blend-in completes.")]
            public float hold = 2f;

            [Tooltip("Crossfade duration from the previous clip into this one. Ignored on the first step.")]
            public float blendIn = 0.4f;

            [Tooltip("Playback speed of the clip. Scales only which frame is sampled — hold and blend times stay in real seconds.")]
            public float speed = 1f;

            [Tooltip("Seconds into the clip where this step starts. On the first step this defines the cycle's starting pose.")]
            public float startOffset;
        }

        [Tooltip("Steps of the cycle, in order. The first clip should be loop-enabled — the cycle starts and ends inside it.")]
        public List<Step> steps = new List<Step>();

        [Tooltip("Crossfade duration from wherever the sequence is back into the starting pose, closing the loop.")]
        public float loopBlend = 0.5f;

        [Tooltip("If > 0, forces the exact cycle length in seconds. The steps repeat as a pass for as long as needed (last step blending back into the first via Loop Blend), and at Total Duration minus Loop Blend the sequence blends back to the starting pose no matter where it is. 0 = one pass. Use one value across characters so their recorded videos pack to identical durations.")]
        public float totalDuration;

        [Tooltip("Advance the cycle continuously in Edit Mode.")]
        public bool playInEditMode;

        [Tooltip("Time in seconds to freeze the cycle at in Edit Mode when not playing.")]
        public float previewTime;

        [Tooltip("Play Mode: start at a random point in the cycle so characters sharing a sequence don't run in sync.")]
        public bool randomizeStartOffset = true;

        [Tooltip("Pin feet to each clip's IK goals — fixes the foot slide humanoid retargeting introduces. Disable if ankles look wrong on a specific clip.")]
        public bool applyFootIK = true;

        [Tooltip("Sequence file to save to / load from (Save To Asset / Load From Asset buttons below), so one sequence can be reused across characters and the recorder.")]
        public AnimationSequenceAsset sequenceAsset;

        private PlayableGraph graph;
        private AnimationMixerPlayable mixer;
        private readonly List<AnimationClipPlayable> playables = new List<AnimationClipPlayable>();
        private Animator boundAnimator;
        private float[] stepStarts;
        private float playbackTime;
        private bool appliedFootIK = true;
        // Mirrors the clips the playables were built with, so the per-frame graph
        // validity check compares managed references instead of calling
        // GetAnimationClip() (native interop) for every step.
        private AnimationClip[] builtClips;
        // Mixer inputs written last frame — only these need zeroing next frame.
        private int lastInputA = -1, lastInputB = -1, lastInputLoop = -1;
#if UNITY_EDITOR
        private double lastEditorTime;
#endif

        /// <summary>Raised each time a pose was evaluated — hook for pose post-processors (e.g. BonePoseSmoother) in Edit Mode.</summary>
        public event Action PoseSampled;

        bool IPoseSampler.IsPreviewing => isActiveAndEnabled && playInEditMode;

        /// <summary>Current position within the cycle (seconds, wrapped), as of the last Sample. Lets layered effects envelope themselves around the loop seam.</summary>
        public float CycleTime { get; private set; }

        /// <summary>
        /// Time advanced by the last Sample (cycle-wrapped, ≥ 0). Pose layers
        /// step by this instead of wall-clock time, so a recorder stepping the
        /// sequence at a fixed 1/fps gets deterministic idle/smoothing too.
        /// </summary>
        public float LastStepDelta { get; private set; }

        /// <summary>
        /// While true, the player never samples on its own (edit tick,
        /// OnValidate, project change) — an external driver (the recorder)
        /// owns the pose via explicit Sample calls, so nothing can re-pose the
        /// character between a sampled frame and its capture.
        /// </summary>
        public bool ExternallyDriven { get; set; }

        private bool hasPreviousCycleTime;

        // Override layer — one extra mixer input (index n+1) holding a clip a
        // behaviour asked for on top of the schedule (AgitationResponder's
        // speaking loops). It crossfades in over the schedule and back out;
        // the schedule keeps advancing underneath, so the return lands
        // wherever the cycle is by then. The playable is (re)built lazily
        // inside Sample, so a graph rebuild (clips changed) just drops it and
        // the next sample recreates it.
        private AnimationClipPlayable overridePlayable;
        private AnimationClip overrideClip;     // clip the override input currently holds
        private AnimationClip overrideTarget;   // clip requested; null = fading out
        private float overrideWeight;           // current blend 0..1
        private float overrideFadeIn, overrideFadeOut;
        private float overrideSpeed = 1f;
        private float overrideTime;             // local clip time, wrapped
        private bool overrideWeightWritten;     // mixer input n+1 holds a non-zero weight

        /// <summary>Clip requested through <see cref="PlayOverride"/> and not yet stopped; null while the schedule owns the pose (a fade-out may still be running).</summary>
        public AnimationClip OverrideClip => overrideTarget;

        /// <summary>Current blend of the override layer, 0 (schedule only) to 1 (override only).</summary>
        public float OverrideWeight => overrideWeight;

        /// <summary>
        /// Crossfades the given clip in over the schedule and holds it, wrapped
        /// as a loop, until <see cref="StopOverride"/>. Requesting a different
        /// clip mid-override swaps it in place (the blend weight carries over).
        /// Play Mode only — the edit-mode preview never sees it. A null clip
        /// is a stop.
        /// </summary>
        public void PlayOverride(AnimationClip clip, float blendIn, float speed = 1f, float startOffset = 0f)
        {
            if (clip == null)
            {
                StopOverride(blendIn);
                return;
            }
            if (overrideTarget != clip) overrideTime = Mathf.Max(startOffset, 0f);
            overrideTarget = clip;
            overrideFadeIn = Mathf.Max(blendIn, 0f);
            overrideSpeed = speed;
        }

        /// <summary>Crossfades the override out; the schedule is fully back after blendOut seconds.</summary>
        public void StopOverride(float blendOut)
        {
            overrideTarget = null;
            overrideFadeOut = Mathf.Max(blendOut, 0f);
        }

        /// <summary>
        /// Advances the override fade by dt and keeps mixer input n+1 in step:
        /// (re)creates the playable when the requested clip changed, scrubs it,
        /// writes its weight. Returns the factor the schedule inputs must be
        /// scaled by (1 - override weight).
        /// </summary>
        private float StepOverride(float dt, int n)
        {
            if (!Application.isPlaying) return 1f;

            // Fade toward the request.
            if (overrideTarget != null)
                overrideWeight = overrideFadeIn > 0f ? Mathf.Min(1f, overrideWeight + dt / overrideFadeIn) : 1f;
            else
                overrideWeight = overrideFadeOut > 0f ? Mathf.Max(0f, overrideWeight - dt / overrideFadeOut) : 0f;

            int input = n + 1;
            if (overrideWeight <= 0f)
            {
                if (overrideWeightWritten)
                {
                    mixer.SetInputWeight(input, 0f);
                    overrideWeightWritten = false;
                }
                // Nothing to show: free the playable so a stale clip is never
                // held across scenarios.
                if (overrideTarget == null && overridePlayable.IsValid())
                {
                    graph.Disconnect(mixer, input);
                    overridePlayable.Destroy();
                    overrideClip = null;
                }
                return 1f;
            }

            // Fading out keeps showing the last clip; fading in shows the request.
            AnimationClip wanted = overrideTarget != null ? overrideTarget : overrideClip;
            if (wanted == null)
            {
                overrideWeight = 0f;
                return 1f;
            }
            if (overrideClip != wanted || !overridePlayable.IsValid())
            {
                if (overridePlayable.IsValid())
                {
                    graph.Disconnect(mixer, input);
                    overridePlayable.Destroy();
                }
                overridePlayable = AnimationClipPlayable.Create(graph, wanted);
                overridePlayable.SetApplyFootIK(applyFootIK);
                graph.Connect(overridePlayable, 0, mixer, input);
                overrideClip = wanted;
            }

            // Always wrap: these are speaking loops, and a non-loop-flagged
            // clip clamped at its last frame would freeze the character
            // mid-sentence. Loop-enabled clips just wrap more gracefully.
            overrideTime += dt * overrideSpeed;
            float length = Mathf.Max(wanted.length, 0.001f);
            overrideTime = Mathf.Repeat(overrideTime, length);
            overridePlayable.SetTime(overrideTime);

            // A smooth curve on the crossfade; the schedule side gets the complement.
            float w = Mathf.SmoothStep(0f, 1f, overrideWeight);
            mixer.SetInputWeight(input, w);
            overrideWeightWritten = true;
            return 1f - w;
        }

        /// <summary>Total length of one loop cycle in seconds — the override when set, else derived from the steps.</summary>
        public float CycleDuration
        {
            get
            {
                if (steps == null || steps.Count == 0) return 0f;
                if (totalDuration > 0f)
                    return Mathf.Max(totalDuration, Mathf.Max(loopBlend, 0f) + 0.001f);
                float t = Mathf.Max(steps[0].hold, 0f);
                for (int i = 1; i < steps.Count; i++)
                    t += Mathf.Max(steps[i].blendIn, 0f) + Mathf.Max(steps[i].hold, 0f);
                return t + Mathf.Max(loopBlend, 0f);
            }
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                playbackTime = randomizeStartOffset
                    ? UnityEngine.Random.Range(0f, Mathf.Max(CycleDuration, 0.001f))
                    : 0f;
                Sample(playbackTime);
            }
#if UNITY_EDITOR
            else
            {
                EditorApplication.update += EditorTick;
                EditorApplication.projectChanged += OnProjectChanged;
                lastEditorTime = EditorApplication.timeSinceStartup;
                Sample(playInEditMode ? playbackTime : previewTime);
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
            if (!Application.isPlaying && isActiveAndEnabled && !ExternallyDriven)
                Sample(playInEditMode ? playbackTime : previewTime);
        }
#endif

        /// <summary>
        /// While true the graph is kept alive but not evaluated (playback time
        /// does not advance) — set by IntroSequencePlayer while a Timeline
        /// drives this Animator. Cheaper than disabling: no graph
        /// destroy/rebuild and no Animator rebind on both ends of the intro.
        /// </summary>
        public bool Suspended { get; set; }

        private void Update()
        {
            if (!Application.isPlaying || Suspended) return;
            playbackTime += Time.deltaTime;
            Sample(playbackTime);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying && isActiveAndEnabled && !ExternallyDriven)
                Sample(playInEditMode ? playbackTime : previewTime);
        }

        [ContextMenu("Log Cycle Duration")]
        private void LogCycleDuration()
        {
            Debug.Log($"{name} sequence cycle: {CycleDuration:0.###} s", this);
        }

        private void EditorTick()
        {
            if (this == null || Application.isPlaying) return;

            double now = EditorApplication.timeSinceStartup;
            float delta = (float)(now - lastEditorTime);
            lastEditorTime = now;

            if (!playInEditMode || ExternallyDriven) return;
            playbackTime += delta;
            Sample(playbackTime);
            SceneView.RepaintAll();
        }
#endif

        /// <summary>
        /// Poses the character at the given time, wrapped into the cycle.
        /// Deterministic — a recorder can step this frame by frame.
        /// </summary>
        public void Sample(float time)
        {
            float duration = CycleDuration;
            if (duration <= 0f || !EnsureGraph()) return;

            float ct = Mathf.Repeat(time, duration);
            LastStepDelta = hasPreviousCycleTime ? Mathf.Clamp(Mathf.Repeat(ct - CycleTime, duration), 0f, 0.1f) : 0f;
            CycleTime = ct;
            hasPreviousCycleTime = true;
            int n = steps.Count;

            if (appliedFootIK != applyFootIK)
            {
                for (int i = 0; i < playables.Count; i++) playables[i].SetApplyFootIK(applyFootIK);
                appliedFootIK = applyFootIK;
            }

            // Audible start of each step = start of its blend-in window. The
            // steps form one PASS; when Total Duration exceeds a pass, the
            // pass repeats (last step blends back into step 0 via loopBlend as
            // a wrap-around blend-in) until the closing loop blend takes over.
            if (stepStarts == null || stepStarts.Length != n) stepStarts = new float[n];
            stepStarts[0] = 0f;
            float cursor = Mathf.Max(steps[0].hold, 0f);
            for (int i = 1; i < n; i++)
            {
                stepStarts[i] = cursor;
                cursor += Mathf.Max(steps[i].blendIn, 0f) + Mathf.Max(steps[i].hold, 0f);
            }
            float passLength = cursor + Mathf.Max(loopBlend, 0f);
            float pt = passLength > 0f ? Mathf.Repeat(ct, passLength) : 0f;

            // Closing blend window: the last loopBlend seconds of the cycle
            // fade whatever the schedule shows into the seam pose. Inside it
            // the pass never starts its own wrap blend — the closing blend IS
            // the wrap for the final pass — so the two can't fight over the
            // loop-back mixer input.
            float loopStart = duration - Mathf.Max(loopBlend, 0f);
            bool inClosingBlend = ct >= loopStart;

            // Underlying schedule state within the pass: active step a,
            // incoming step b, weight. Past the last step's hold, the pass
            // wraps: last step blends into step 0 (its next-pass instance).
            int a = 0, b = -1;
            float weight = 0f;
            bool wrapBlend = false;
            if (pt >= cursor && n > 0 && passLength > cursor && !inClosingBlend)
            {
                a = n - 1;
                b = 0;
                wrapBlend = true;
                weight = BlendWeight(pt - cursor, passLength - cursor);
            }
            else
            {
                // Inside the closing blend, past the last hold: the pass would
                // have wrapped, so the outgoing pose is the last step holding.
                if (pt >= cursor && n > 0) pt = Mathf.Min(pt, cursor);
                for (int i = n - 1; i >= 0; i--)
                {
                    if (pt < stepStarts[i]) continue;
                    float sinceStart = pt - stepStarts[i];
                    float blendIn = i > 0 ? Mathf.Max(steps[i].blendIn, 0f) : 0f;
                    if (sinceStart < blendIn)
                    {
                        a = i - 1;
                        b = i;
                        weight = BlendWeight(sinceStart, blendIn);
                    }
                    else
                    {
                        a = i;
                    }
                    break;
                }
            }

            // Closing blend layered on top: fades whatever the schedule shows
            // into the dedicated loop-back instance of clip 0, timed to reach
            // the starting pose (start offset) exactly at the cycle seam.
            float loopWeight = inClosingBlend ? BlendWeight(ct - loopStart, duration - loopStart) : 0f;

            // Override layer (speaking loops): whatever it takes, the schedule
            // inputs give up, so the mixer stays normalised.
            float schedule = StepOverride(LastStepDelta, n);

            // Mixer weights persist between evaluations, so only the inputs
            // written last frame need zeroing — not all n+1 of them.
            if (lastInputA >= 0 && lastInputA <= n) mixer.SetInputWeight(lastInputA, 0f);
            if (lastInputB >= 0 && lastInputB <= n) mixer.SetInputWeight(lastInputB, 0f);
            if (lastInputLoop >= 0 && lastInputLoop <= n) mixer.SetInputWeight(lastInputLoop, 0f);
            lastInputA = lastInputB = lastInputLoop = -1;

            mixer.SetInputWeight(a, (1f - weight) * (1f - loopWeight) * schedule);
            lastInputA = a;
            SetClipTime(a, steps[a], pt - stepStarts[a]);
            if (b >= 0)
            {
                // Wrap blend: the incoming step 0 belongs to the NEXT pass, so
                // its elapsed time is negative — it arrives at its start offset
                // exactly as the pass rolls over. Use the loop-back mixer input
                // (a second instance of clip 0) so step 0 can be both the
                // outgoing step of a one-step sequence and the incoming one.
                int input = wrapBlend ? n : b;
                float elapsed = wrapBlend
                    ? LoopBackElapsed(pt - cursor, passLength - cursor, steps[0])
                    : pt - stepStarts[b];
                mixer.SetInputWeight(input, weight * (1f - loopWeight) * schedule);
                lastInputB = input;
                SetClipTime(input, steps[b], elapsed);
            }
            if (loopWeight > 0f)
            {
                mixer.SetInputWeight(n, loopWeight * schedule);
                lastInputLoop = n;
                // The loop-back instance must show exactly what step 0 will
                // show right after the seam: its clip running forward from the
                // start offset, phase-aligned so both sides agree at ct == duration.
                // Counting DOWN toward the offset (negative elapsed) would clamp
                // to frame 0 on non-looping clips or wrap to the clip's end on
                // looping ones — a visible jump at the seam either way.
                SetClipTime(n, steps[0], LoopBackElapsed(ct - loopStart, duration - loopStart, steps[0]));
            }

            // Suppress root motion for this evaluation only, so the edit-mode
            // preview never permanently modifies the Animator.
            bool rootMotion = boundAnimator.applyRootMotion;
            boundAnimator.applyRootMotion = false;
            graph.Evaluate(0f);
            boundAnimator.applyRootMotion = rootMotion;

            PoseSampled?.Invoke();
        }

        private static float BlendWeight(float elapsed, float blendDuration)
        {
            if (blendDuration <= 0f) return 1f;
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / blendDuration));
        }

        /// <summary>
        /// Elapsed time (from the step's start offset) for the loop-back
        /// instance during the closing blend. Looping first clip: run the clip
        /// forward through the blend so it arrives exactly at the start offset
        /// at the seam — one whole clip cycle back, phase-continuous. Non-looping
        /// first clip: hold the anchor frame itself so the blend lands on it.
        /// </summary>
        private static float LoopBackElapsed(float sinceLoopStart, float loopBlendDuration, Step first)
        {
            AnimationClip clip = first.clip;
            if (clip == null || !clip.isLooping || first.speed <= 0f) return 0f;
            float clipCycleSeconds = clip.length / first.speed;
            // Start as many whole clip cycles back as fit the blend so local time
            // wraps naturally, ending on startOffset at ct == duration.
            int cycles = Mathf.Max(1, Mathf.CeilToInt(loopBlendDuration / clipCycleSeconds));
            return sinceLoopStart - loopBlendDuration + cycles * clipCycleSeconds;
        }

        private void SetClipTime(int index, Step step, float elapsed)
        {
            AnimationClip clip = builtClips[index];
            float local = step.startOffset + elapsed * step.speed;
            local = clip.isLooping
                ? Mathf.Repeat(local, clip.length)
                : Mathf.Clamp(local, 0f, clip.length);
            playables[index].SetTime(local);
        }

        /// <summary>Mixer inputs 0..n-1 mirror the steps; input n is a second
        /// instance of clip 0 used for the closing loop blend; input n+1 is the
        /// override layer (<see cref="PlayOverride"/>), connected on demand.</summary>
        private bool EnsureGraph()
        {
            int n = steps != null ? steps.Count : 0;
            if (n == 0) return false;
            for (int i = 0; i < n; i++)
                if (steps[i].clip == null) return false;

            bool valid = graph.IsValid() && playables.Count == n + 1
                && builtClips != null && builtClips.Length == n + 1;
            for (int i = 0; i <= n && valid; i++)
                if (builtClips[i] != steps[i == n ? 0 : i].clip) valid = false;
            if (valid) return true;

            DestroyGraph();

            boundAnimator = GetComponent<Animator>();
            if (boundAnimator == null) boundAnimator = GetComponentInChildren<Animator>(true);
            if (boundAnimator == null) return false;

            graph = PlayableGraph.Create("AnimationSequencePlayer");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            mixer = AnimationMixerPlayable.Create(graph, n + 2);
            builtClips = new AnimationClip[n + 1];
            lastInputA = lastInputB = lastInputLoop = -1;
            for (int i = 0; i <= n; i++)
            {
                AnimationClip clip = steps[i == n ? 0 : i].clip;
                AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(applyFootIK);
                graph.Connect(playable, 0, mixer, i);
                playables.Add(playable);
                builtClips[i] = clip;
            }
            AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "SequencePose", boundAnimator);
            output.SetSourcePlayable(mixer);
            return true;
        }

        private void DestroyGraph()
        {
            if (graph.IsValid()) graph.Destroy();
            playables.Clear();
            builtClips = null;
            // The override playable died with the graph; the request itself
            // survives, so the next Sample rebuilds it at the same weight.
            overridePlayable = default;
            overrideClip = null;
            overrideWeightWritten = false;
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(AnimationSequencePlayer))]
    public class AnimationSequencePlayerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var player = (AnimationSequencePlayer)target;
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save To Asset"))
                    SaveToAsset(player);
                using (new EditorGUI.DisabledScope(player.sequenceAsset == null))
                {
                    if (GUILayout.Button("Load From Asset"))
                        LoadFromAsset(player);
                }
            }
        }

        /// <summary>Overwrites the assigned asset, or prompts for a new file when none is assigned.</summary>
        private static void SaveToAsset(AnimationSequencePlayer player)
        {
            string path = player.sequenceAsset != null
                ? AssetDatabase.GetAssetPath(player.sequenceAsset)
                : EditorUtility.SaveFilePanelInProject("Save Animation Sequence",
                    player.name + "_Sequence", "asset", "Where to save the sequence asset.");
            if (string.IsNullOrEmpty(path)) return;

            AnimationSequenceAsset asset = AssetDatabase.LoadAssetAtPath<AnimationSequenceAsset>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<AnimationSequenceAsset>();
                AssetDatabase.CreateAsset(asset, path);
            }
            asset.CopyFrom(player);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);

            if (player.sequenceAsset != asset)
            {
                Undo.RecordObject(player, "Assign Sequence Asset");
                player.sequenceAsset = asset;
            }
        }

        private static void LoadFromAsset(AnimationSequencePlayer player)
        {
            Undo.RecordObject(player, "Load Animation Sequence");
            player.sequenceAsset.ApplyTo(player);
            player.Sample(player.previewTime);
        }
    }
#endif
}
