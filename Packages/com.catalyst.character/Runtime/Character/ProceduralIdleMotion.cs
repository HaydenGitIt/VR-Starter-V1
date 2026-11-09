using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace catalyst.xr67
{
    /// <summary>
    /// Procedural seated idle life layered over the animated pose: breathing,
    /// Perlin postural sway distributed up the spine, occasional weight
    /// shifts (pelvis roll with torso counter-balance — position stays
    /// planted), and slow neck drift. Designed to run over a FROZEN anchor
    /// frame (a base loop at speed 0) so one clean mocap pose plus this layer
    /// replaces a noisy mocap loop; it works equally over playing clips.
    /// Per-character random seeds keep a classroom out of phase.
    /// The head bone is deliberately untouched — StudentGazeController owns
    /// it; head life comes from the neck. Runs at order -12, before
    /// BonePoseSmoother (-11), gaze (-10) and BoneDriver (0). Settings come
    /// from the ambient player's profile per mode when present (Reading can
    /// slump differently from Listening), else from the local
    /// <see cref="settings"/> — so preview/recording characters without the
    /// student stack bake idle life into billboard videos too.
    /// <see cref="Step"/> is public for deterministic stepping by a recorder.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-12)]
    public class ProceduralIdleMotion : MonoBehaviour
    {
        [Tooltip("Idle tuning for this character. A sibling IIdleMotionSettingsSource (e.g. a student ambient player with a profile) overrides it per mode.")]
        public IdleMotionSettings settings = new IdleMotionSettings();

        [Tooltip("Run the layer in Edit Mode (follows whichever player is previewing; also animates a static pose).")]
        public bool previewInEditMode = true;

        [Tooltip("With an AnimationSequencePlayer on this character, fade the idle influence to zero around the cycle seam over this many seconds — the recorded video then loops perfectly (the seam pose is pure animation). 0 disables the envelope.")]
        public float sequenceLoopFade = 2f;

        [Tooltip("While the character speaks, how much of the held mood expression (AgitationResponder) stays on the mouth shapes — smile, press, dimple, frown. 1 = full mood, 0 = lip sync alone. Brows, eyes and nose always keep the full mood.")]
        [Range(0f, 1f)]
        public float moodMouthWhileSpeaking = 0.5f;

        /// <summary>
        /// 0..1, set every frame by VisemeDriver: how far speech has taken over
        /// the mouth. The idle mouth life (resting smile, lip-press drift and
        /// events, the captured animation base) fades out by it and the held
        /// mood on the mouth scales toward <see cref="moodMouthWhileSpeaking"/>.
        /// </summary>
        public float SpeechMouthGate
        {
            get => speechMouthGate;
            set => speechMouthGate = Mathf.Clamp01(value);
        }
        private float speechMouthGate;

        // Per-step scales for the mouth slots (see StepFace / Add).
        private float idleMouthScale = 1f, moodMouthScale = 1f;
        private bool addingMood;

        private static bool IsMouthSlot(int slot) => (slot >= 12 && slot <= 17) || slot == 24 || slot == 25;

        private float BaseFor(int slot) => faceShapes[slot].baseValue * (IsMouthSlot(slot) ? idleMouthScale : 1f);

        [Tooltip("Debug: log the smile shape's base / written / read-back value each step for a couple of seconds, to tell value jitter apart from downstream rewrites.")]
        public bool debugLogSmile;
        private int debugFramesLeft;

        private IPoseSampler[] samplers = System.Array.Empty<IPoseSampler>();
        private IIdleMotionSettingsSource settingsSource;
        private AnimationSequencePlayer sequence;

        private Transform root;
        private Transform hips, spine, chest, upperChest, neck, shoulderL, shoulderR;
        private Transform upperArmL, upperArmR, lowerArmL, lowerArmR;
        private Transform upperLegL, lowerLegL, footL, upperLegR, lowerLegR, footR;
        private bool bonesResolved;

        // Standing stance: where the animation put the feet this frame, so
        // the legs can be solved back after the pelvis moved.
        private Vector3 footPosL, footPosR;
        private Quaternion footRotL, footRotR;
        private bool feetCaptured;

        // Keeps the body offsets from stacking on themselves when nothing
        // re-poses the skeleton between two steps (see AdditiveBoneLayer).
        private readonly AdditiveBoneLayer boneLayer = new AdditiveBoneLayer();

        private float seedYaw, seedPitch, seedRoll, seedNeckYaw, seedNeckPitch;
        private float seedArmL, seedArmR;
        private float breathPhase, breathJitter;
        private float time;

        private float shiftTimer;
        private float shiftFrom, shiftTo, shiftCurrent;
        private float shiftBlend = 1f;
        private int shiftSide = 1;

        // Facial life — additive micro-expression on the CC body mesh.
        private SkinnedMeshRenderer faceMesh;
        private bool faceResolved;
        private FaceShape[] faceShapes;
        private float seedFace;
        private float faceEventTimer;
        private FaceEvent faceEvent = FaceEvent.None;
        private float faceEventPhase, faceEventDuration, faceEventSide;
        private bool faceDirty;

        private enum FaceEvent { None, BrowRaise, Squint, LipPress, Swallow, BrowFurrow, HalfSmile }
        // Enum.GetValues allocates an array (via reflection) on every call.
        private const int FaceEventCount = 7;

        // Blinking — the guaranteed source for any character with idle life.
        private int blinkL = -1, blinkR = -1;
        private float nextBlinkIn = -1f;
        private float blinkPhase = -1f;
        private bool blinkPendingDouble;
        private float blinkLast;
        private const float BlinkClose = 0.07f, BlinkHold = 0.03f, BlinkOpen = 0.12f;

        /// <summary>True while this layer is driving blinks — StudentGazeController yields its own blink to avoid doubling.</summary>
        public bool DrivesBlink { get; private set; }

        /// <summary>
        /// Blink now (if not already mid-blink) — for layers that want a blink
        /// on a cue, e.g. GazeDriver on a large gaze shift, without becoming a
        /// second writer of the eyelid shapes. No-op unless this layer drives
        /// the blink (<see cref="DrivesBlink"/>).
        /// </summary>
        public void BlinkNow()
        {
            if (!DrivesBlink || blinkPhase >= 0f) return;
            blinkPhase = 0f;
            blinkPendingDouble = false;
        }

        /// <summary>
        /// A driven blendshape: index on the body mesh, the animation's own
        /// value (the base we add onto — captured, never inferred), and the
        /// total we wrote on top last frame.
        /// The base is re-captured whenever the animation actually rewrote
        /// the shapes (a player Sample / the Animator in Play Mode); when
        /// nothing rewrote them (frozen editor pose), we keep the last known
        /// base instead of reading our own output back — so our contribution
        /// can never accumulate, and reverting settings restores the face.
        /// </summary>
        private struct FaceShape
        {
            public int index;
            public float baseValue;
            public float ourLast;
        }

        private bool faceBaseCaptured;

        /// <summary>
        /// Highest value we accept as an animation-authored base for the
        /// expression shapes we drive. Real idle/reading/lipsync clips keep
        /// these under ~40; anything above is our own leftover.
        /// </summary>
        private const float MaxPlausibleBase = 45f;

        // Order matters — indices below refer to this list.
        private static readonly string[] FaceShapeNames =
        {
            "Brow_Raise_Inner_L", "Brow_Raise_Inner_R",     // 0,1
            "Brow_Raise_Outer_L", "Brow_Raise_Outer_R",     // 2,3
            "Brow_Drop_L", "Brow_Drop_R",                   // 4,5
            "Brow_Compress_L", "Brow_Compress_R",           // 6,7
            "Eye_Squint_L", "Eye_Squint_R",                 // 8,9
            "Cheek_Raise_L", "Cheek_Raise_R",               // 10,11
            "Mouth_Smile_L", "Mouth_Smile_R",               // 12,13
            "Mouth_Press_L", "Mouth_Press_R",               // 14,15
            "Mouth_Dimple_L", "Mouth_Dimple_R",             // 16,17
            "Neck_Swallow_Up", "Neck_Swallow_Down",         // 18,19
            "Nose_Nostril_Dilate_L", "Nose_Nostril_Dilate_R", // 20,21
            "Eyelid_Outer_Down_L", "Eyelid_Outer_Down_R",   // 22,23
            // Held expressions only (AgitationResponder) — the idle life
            // never drives these itself.
            "Mouth_Frown_L", "Mouth_Frown_R",               // 24,25
            "Nose_Sneer_L", "Nose_Sneer_R",                 // 26,27
            "Eye_Wide_L", "Eye_Wide_R",                     // 28,29
        };

        // ---------- Held expression offsets (external layer) ----------
        // A behaviour (AgitationResponder) can hold an expression on the face:
        // per-shape weights in blendshape units that are written every frame
        // together with the facial life, on top of the captured animation
        // base and UNDER the resting smile / drift / micro-events, so the
        // two layers compose instead of overwriting each other. Not enveloped
        // by the sequencer seam fade — a held mood must not blink off at the
        // loop point. Play Mode only.
        // NonSerialized: null means "nothing held / nothing written". Unity's
        // domain-reload serialization keeps private arrays on this
        // [ExecuteAlways] component and brings a null one back as float[0],
        // which WriteExternalOffsets then took for a live 30-slot array.
        private static Dictionary<string, int> shapeSlotByName;
        [System.NonSerialized] private float[] externalOffsets;
        [System.NonSerialized] private float[] externalWritten;   // what reached the mesh last frame (to clear a shape that dropped to 0)
        [System.NonSerialized] private bool externalActive;

        private static int SlotOf(string blendShape)
        {
            if (shapeSlotByName == null)
            {
                shapeSlotByName = new Dictionary<string, int>(FaceShapeNames.Length);
                for (int i = 0; i < FaceShapeNames.Length; i++) shapeSlotByName[FaceShapeNames[i]] = i;
            }
            return blendShape != null && shapeSlotByName.TryGetValue(blendShape, out int slot) ? slot : -1;
        }

        /// <summary>Shapes the external layer can hold — the driven set above. Anything else is ignored by <see cref="AddExpressionOffset"/>.</summary>
        public static bool CanHoldExpression(string blendShape) => SlotOf(blendShape) >= 0;

        /// <summary>Forgets every held offset. Call once per frame before re-adding, or once to release the face.</summary>
        public void ClearExpressionOffsets()
        {
            if (externalOffsets == null) return;
            System.Array.Clear(externalOffsets, 0, externalOffsets.Length);
            externalActive = false;
        }

        /// <summary>
        /// Adds a held weight (blendshape units, 0..100) for one shape this
        /// frame. Shapes outside the driven set, or on a mesh that lacks them,
        /// are ignored. Offsets accumulate until <see cref="ClearExpressionOffsets"/>.
        /// </summary>
        public void AddExpressionOffset(string blendShape, float weight)
        {
            int slot = SlotOf(blendShape);
            if (slot < 0 || weight <= 0f) return;
            if (externalOffsets == null || externalOffsets.Length != FaceShapeNames.Length) externalOffsets = new float[FaceShapeNames.Length];
            externalOffsets[slot] += weight;
            externalActive = true;
        }

        /// <summary>
        /// Writes the held offsets — and puts back the base on any shape whose
        /// offset dropped to zero since last frame, so a released mood never
        /// leaves a residue. Runs inside StepFace, after ourLast was reset,
        /// so the resting smile and events then add on top through Add().
        /// </summary>
        private void WriteExternalOffsets()
        {
            bool anyWritten = externalWritten != null;
            if (!externalActive && !anyWritten) return;
            if (externalWritten == null || externalWritten.Length != faceShapes.Length) externalWritten = new float[faceShapes.Length];
            bool stillHeld = false;
            addingMood = true;
            for (int i = 0; i < faceShapes.Length; i++)
            {
                float v = externalActive && externalOffsets != null && i < externalOffsets.Length ? externalOffsets[i] : 0f;
                if (v > 0f)
                {
                    Add(i, v);
                    stillHeld = true;
                }
                else if (externalWritten[i] > 0f && faceShapes[i].index >= 0)
                {
                    faceMesh.SetBlendShapeWeight(faceShapes[i].index, Mathf.Clamp(BaseFor(i), 0f, 100f));
                }
                externalWritten[i] = v;
            }
            addingMood = false;
            if (!stillHeld) externalWritten = null;
        }

#if UNITY_EDITOR
        private double lastEditorTime;
        private double lastPoseTime;
#endif

        private void OnEnable()
        {
            samplers = GetComponents<IPoseSampler>();
            settingsSource = GetComponent<IIdleMotionSettingsSource>();
            sequence = GetComponent<AnimationSequencePlayer>();
            SubscribePlayers(true);

            // Hot-reload / re-enable safety: resolve the face and capture the
            // base fresh rather than trusting whatever survived the reload.
            faceResolved = false;
            faceShapes = null;
            faceBaseCaptured = false;
            bonesResolved = false;

            // Keep Perlin seeds SMALL. Mathf.PerlinNoise loses precision at
            // large coordinates — the lattice gets coarse and the output
            // steps instead of flowing, which reads as twitching. Seeds in
            // 0..64 stay smooth and are more than enough decorrelation.
            seedYaw = Random.value * 64f;
            seedPitch = Random.value * 64f;
            seedRoll = Random.value * 64f;
            seedNeckYaw = Random.value * 64f;
            seedNeckPitch = Random.value * 64f;
            seedArmL = Random.value * 64f;
            seedArmR = Random.value * 64f;
            seedFace = Random.value * 64f;
            breathPhase = Random.value * 100f;
            breathJitter = Random.Range(-1f, 1f);
            shiftTimer = Random.Range(3f, 15f);
            faceEventTimer = Random.Range(2f, 8f);

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorApplication.update += EditorTick;
                lastEditorTime = EditorApplication.timeSinceStartup;
                lastPoseTime = lastEditorTime;
            }
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorTick;
#endif
            SubscribePlayers(false);
            boneLayer.RestoreAll();   // leave the clean pose behind, not our last offsets
            boneLayer.Reset();
            facePending = false;
            ClearFace();
            faceBaseCaptured = false;
            if (DrivesBlink) { SetBlink(0f); DrivesBlink = false; }
        }

        private void OnDestroy()
        {
            // Don't destroy the companion here: when the whole GameObject is
            // going away Unity is already destroying every component, and a
            // second DestroyImmediate is a "Destroying object multiple times"
            // error. Just orphan it — it removes itself on its next LateUpdate
            // (see FaceWriter), and it is hidden + DontSave regardless.
            if (faceWriter != null) faceWriter.owner = null;
            faceWriter = null;
        }

        private void SubscribePlayers(bool on)
        {
            foreach (IPoseSampler sampler in samplers)
            {
                if (sampler == null) continue;
                sampler.PoseSampled -= OnPoseSampled;
                if (on) sampler.PoseSampled += OnPoseSampled;
            }
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            // Play Mode: -12, before BoneDriver (0). The face write itself is
            // deferred to FaceWriter (+5); see Step.
            CaptureFaceBaseNow();
            Step(Time.deltaTime);
        }

        /// <summary>
        /// Captures the animation's values of our expression shapes — ONCE, on
        /// the first clean frame, then holds. Why not every frame: the idle /
        /// reading / talking clips carry no curves for these particular shapes
        /// (lipsync drives visemes, not brow/cheek/press), so nothing rewrites
        /// them between our writes; reading the mesh again would just read our
        /// own previous write back as "the base" and compound. Re-capture on
        /// demand via <see cref="RecaptureFaceBase"/> (e.g. after swapping to a
        /// clip that does author these shapes).
        /// </summary>
        private void CaptureFaceBaseNow()
        {
            if (faceBaseCaptured || !ResolveFace()) return;
            for (int i = 0; i < faceShapes.Length; i++)
            {
                if (faceShapes[i].index < 0) continue;
                float v = faceMesh.GetBlendShapeWeight(faceShapes[i].index);
                // Poison guard: a large value here is a leftover of our own
                // earlier writes (old build / stuck editor state), not animation.
                faceShapes[i].baseValue = v > MaxPlausibleBase ? 0f : v;
            }
            faceBaseCaptured = true;
        }

        /// <summary>Forget the captured facial base and take it again from the current mesh (clears our contribution first).</summary>
        [ContextMenu("Recapture Face Base")]
        public void RecaptureFaceBase()
        {
            ClearFace();
            faceBaseCaptured = false;
            CaptureFaceBaseNow();
        }

        private void OnPoseSampled()
        {
#if UNITY_EDITOR
            if (Application.isPlaying) return;
            // First clean moment after a player wrote the pose: capture the
            // facial base (once — see CaptureFaceBaseNow).
            CaptureFaceBaseNow();
            if (!previewInEditMode) return;
            double now = EditorApplication.timeSinceStartup;
            float dt = SmoothEditorDelta((float)(now - lastPoseTime));
            lastPoseTime = now;
            // A sequencer publishes the exact time it advanced — deterministic
            // under a recorder stepping at 1/fps, unlike wall-clock deltas.
            if (sequence != null && sequence.isActiveAndEnabled) dt = sequence.LastStepDelta;
            Step(dt);
#endif
        }

#if UNITY_EDITOR
        private float smoothedEditorDt = 1f / 60f;

        /// <summary>
        /// Editor ticks are bursty (repaint storms give 5 ms ticks, a hitch
        /// gives 100 ms). Raw deltas make the noise sampling stutter — on a
        /// face that reads as twitching. Low-pass the delta so motion flows
        /// at a steady rate; total elapsed time still tracks wall clock.
        /// </summary>
        private float SmoothEditorDelta(float raw)
        {
            raw = Mathf.Clamp(raw, 0f, 0.1f);
            smoothedEditorDt = Mathf.Lerp(smoothedEditorDt, raw, 0.15f);
            return smoothedEditorDt;
        }
#endif

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (this == null || Application.isPlaying) return;

            double now = EditorApplication.timeSinceStartup;
            float delta = SmoothEditorDelta((float)(now - lastEditorTime));
            lastEditorTime = now;

            if (!previewInEditMode) return;
            // With a player previewing, PoseSampled drives us right after the
            // fresh pose; this tick covers a character with a static pose.
            foreach (IPoseSampler sampler in samplers)
                if (sampler != null && sampler.IsPreviewing) return;
            Step(delta);
            SceneView.RepaintAll();
        }
#endif

        /// <summary>Settings in effect: a sibling source's (e.g. a profile's per-mode set) when it offers one, else the local ones.</summary>
        private IdleMotionSettings ActiveSettings
        {
            get
            {
                IdleMotionSettings fromSource = settingsSource != null ? settingsSource.ActiveIdleSettings : null;
                return fromSource ?? settings;
            }
        }

        /// <summary>
        /// Advances the idle motion and applies it on top of the current
        /// pose. Deterministic — a recorder can step this frame by frame.
        /// </summary>
        public void Step(float dt)
        {
            IdleMotionSettings s = ActiveSettings;
            if (s == null || !s.enabled || !ResolveBones()) return;

            // Where nothing re-posed a bone since the last step (a static pose
            // in Edit Mode, an Animator culled off-screen, a clip with no
            // curve for it) put the clean pose back first, so the offsets
            // below are applied to the animation and never to themselves.
            boneLayer.Begin(hips); boneLayer.Begin(spine); boneLayer.Begin(chest); boneLayer.Begin(upperChest);
            boneLayer.Begin(neck); boneLayer.Begin(shoulderL); boneLayer.Begin(shoulderR);
            boneLayer.Begin(upperArmL); boneLayer.Begin(upperArmR); boneLayer.Begin(lowerArmL); boneLayer.Begin(lowerArmR);
            if (s.stance == IdleStance.Standing)
            {
                boneLayer.Begin(upperLegL); boneLayer.Begin(lowerLegL); boneLayer.Begin(footL);
                boneLayer.Begin(upperLegR); boneLayer.Begin(lowerLegR); boneLayer.Begin(footR);
            }

            time += dt;
            float k = s.intensity;

            // Sequenced characters: ease the whole layer out approaching the
            // loop seam and back in after it, so frame N == frame 0 exactly
            // and the baked video loops seamlessly with idle life enabled.
            // Only while the sequencer is actually advancing (previewing or
            // being recorded). A parked sequencer sits at CycleTime 0 — the
            // seam — and the envelope would clamp the WHOLE layer to zero:
            // no sway, no expression, only the (un-enveloped) blink. That is
            // precisely the "it only blinks" symptom.
            bool sequencerRunning = sequence != null && sequence.isActiveAndEnabled
                && (sequence.playInEditMode || sequence.ExternallyDriven || Application.isPlaying);
            if (sequencerRunning && sequenceLoopFade > 0f)
            {
                float duration = sequence.CycleDuration;
                if (duration > 0f)
                {
                    float fade = Mathf.Min(sequenceLoopFade, duration / 3f);
                    float ct = sequence.CycleTime;
                    k *= Mathf.Min(
                        Mathf.SmoothStep(0f, 1f, ct / fade),
                        Mathf.SmoothStep(0f, 1f, (duration - ct) / fade));
                }
            }

            // Breathing — sine at a per-character rate.
            float breathHz = Mathf.Max(s.breathsPerMinute + breathJitter * s.breathVariation, 1f) / 60f;
            float breath = Mathf.Sin((time * breathHz + breathPhase) * 2f * Mathf.PI);
            float chestPitch = breath * s.chestPitchDegrees * k;
            float shoulderRise = breath * s.shoulderRiseDegrees * k;

            // Postural sway — centered Perlin, distributed up the spine.
            float swayYaw = Noise(seedYaw, time * s.swayFrequency) * s.swayDegrees.x * k;
            float swayPitch = Noise(seedPitch, time * s.swayFrequency) * s.swayDegrees.y * k;
            float swayRoll = Noise(seedRoll, time * s.swayFrequency) * s.swayDegrees.z * k;

            // Weight shifts — alternate sides, settle smoothly, hold.
            shiftTimer -= dt;
            if (shiftTimer <= 0f)
            {
                shiftSide = -shiftSide;
                shiftFrom = shiftCurrent;
                shiftTo = shiftSide * Random.Range(0.6f, 1.2f);   // unitless; amplitudes apply below
                shiftBlend = 0f;
                shiftTimer = Random.Range(s.shiftInterval.x, s.shiftInterval.y);
            }
            if (shiftBlend < 1f)
            {
                shiftBlend = Mathf.Min(1f, shiftBlend + dt / Mathf.Max(s.shiftBlendSeconds, 0.01f));
                shiftCurrent = Mathf.Lerp(shiftFrom, shiftTo, Mathf.SmoothStep(0f, 1f, shiftBlend));
            }
            float shift = shiftCurrent * s.shiftRollDegrees * k;
            bool standing = s.stance == IdleStance.Standing;
            if (standing)
            {
                // Standing, a pelvis roll alone swings the legs and slides
                // the feet. The shift is mostly a sideways pelvis travel with
                // a smaller roll on top; the feet are remembered first and
                // the legs solved back once the pelvis has moved.
                CaptureFeet();
                hips.position += root.right * (shiftCurrent * k * s.shiftLateralCentimetres * 0.01f);
                shift *= s.standingRollScale;
            }

            // Pelvis leans (seated: rotation only, so the seat contact stays
            // planted), torso counter-balances so the head stays roughly over
            // the base of support.
            Apply(hips, 0f, 0f, swayRoll * 0.3f + shift);
            if (standing) RestoreFeet();
            Apply(spine, swayYaw * 0.5f, swayPitch * 0.5f - chestPitch * 0.3f, swayRoll * 0.4f - shift * 0.4f);
            Apply(chest, swayYaw * 0.3f, swayPitch * 0.3f + chestPitch, swayRoll * 0.3f - shift * 0.3f);
            Apply(upperChest, swayYaw * -0.2f, chestPitch * 0.5f, swayRoll * -0.2f - shift * 0.2f);
            Apply(shoulderL, 0f, 0f, shoulderRise);
            Apply(shoulderR, 0f, 0f, -shoulderRise);

            // Arm micro-motion — independent per limb, faster than torso sway.
            if (s.armSwayDegrees > 0f)
            {
                float armFreq = s.swayFrequency * 1.6f;
                float arm = s.armSwayDegrees * k;
                Apply(upperArmL, Noise(seedArmL, time * armFreq) * arm, Noise(seedArmL + 37f, time * armFreq) * arm, 0f);
                Apply(lowerArmL, 0f, Noise(seedArmL + 73f, time * armFreq) * arm * 0.7f, 0f);
                Apply(upperArmR, Noise(seedArmR, time * armFreq) * arm, Noise(seedArmR + 37f, time * armFreq) * arm, 0f);
                Apply(lowerArmR, 0f, Noise(seedArmR + 73f, time * armFreq) * arm * 0.7f, 0f);
            }

            // Neck drift — slower, independent wander; the gaze layer owns the head bone.
            Apply(neck,
                Noise(seedNeckYaw, time * s.neckDriftFrequency) * s.neckDriftDegrees * k,
                Noise(seedNeckPitch, time * s.neckDriftFrequency) * s.neckDriftDegrees * 0.6f * k,
                0f);
            boneLayer.Commit();

            // Face + blink are written LATE (see FaceWriter below), after the
            // Reallusion BoneDriver's LateUpdate at order 0. BoneDriver rewrites
            // body blendshapes in place (constraint targets are reset to 100
            // then scaled) and only when the character is on-screen — so if we
            // wrote the face before it, our mouth/brow values were overwritten
            // on some ticks and not others: a flicker proportional to what we
            // wrote. Writing after it makes our values the last word every frame.
            pendingFaceDt += dt;
            pendingFaceSettings = s;
            pendingFaceEnvelope = k / Mathf.Max(s.intensity, 0.0001f);
            facePending = true;
            EnsureFaceWriter();
            // The FaceWriter companion ([ExecuteAlways], order +5) flushes in
            // ITS LateUpdate — which the editor runs in its player loop after
            // BoneDriver's ExecuteInEditMode LateUpdate (order 0), in both Edit
            // and Play Mode. (An EditorApplication.delayCall would NOT work:
            // it fires in the next update dispatch, i.e. before that tick's
            // LateUpdates, so BoneDriver would still overwrite us.)
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        private float pendingFaceDt;
        private IdleMotionSettings pendingFaceSettings;
        private float pendingFaceEnvelope;
        private bool facePending;
        private FaceWriter faceWriter;

        /// <summary>Applies the queued facial step. Called from FaceWriter (Play Mode, order +5) or the editor flush.</summary>
        internal void FlushFace()
        {
            if (!facePending || pendingFaceSettings == null) return;
            facePending = false;
            float dt = pendingFaceDt;
            pendingFaceDt = 0f;
            StepFace(dt, pendingFaceSettings, pendingFaceEnvelope);
            StepBlink(dt, pendingFaceSettings);
        }

        private void EnsureFaceWriter()
        {
            if (faceWriter != null) return;
            faceWriter = GetComponent<FaceWriter>();
            if (faceWriter == null)
            {
                faceWriter = gameObject.AddComponent<FaceWriter>();
                faceWriter.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            }
            faceWriter.owner = this;
        }

        /// <summary>
        /// Companion that runs the facial write in Play Mode LateUpdate at
        /// order +5 — after Reallusion's BoneDriver (0), so BoneDriver's
        /// in-place blendshape rewrites can never land on top of ours.
        /// Auto-added, hidden, not saved.
        /// </summary>
        [DefaultExecutionOrder(5)]
        [ExecuteAlways]
        internal class FaceWriter : MonoBehaviour
        {
            internal ProceduralIdleMotion owner;

            private void LateUpdate()
            {
                if (owner == null)
                {
                    // Orphaned: the owner component was removed but this
                    // GameObject lives on. Re-attach if an owner reappeared
                    // (hot reload), else remove ourselves. Only ever from a
                    // normal LateUpdate on a live object — never during a
                    // teardown — so it cannot double-destroy.
                    owner = GetComponent<ProceduralIdleMotion>();
                    if (owner != null) { owner.faceWriter = this; return; }
                    if (!gameObject.scene.isLoaded) return;
                    if (Application.isPlaying) Destroy(this); else DestroyImmediate(this);
                    return;
                }
                owner.FlushFace();
            }
        }

        // ---------- Blinking ----------
        // Not enveloped by the sequencer seam fade: a blink mid-seam is fine
        // (the seam frame itself is almost never inside a 0.2 s blink), and
        // fading a blink looks worse than a rare seam blink.
        private void StepBlink(float dt, IdleMotionSettings s)
        {
            if (!s.blinkEnabled || !ResolveFace() || blinkL < 0)
            {
                if (DrivesBlink) { SetBlink(0f); DrivesBlink = false; }
                return;
            }
            DrivesBlink = true;

            if (nextBlinkIn < 0f) nextBlinkIn = Random.Range(s.blinkInterval.x, s.blinkInterval.y);

            if (blinkPhase < 0f)
            {
                nextBlinkIn -= dt;
                if (nextBlinkIn <= 0f)
                {
                    blinkPhase = 0f;
                    blinkPendingDouble = Random.value < s.doubleBlinkChance;
                }
                else
                {
                    // Written every frame, not just once after a blink: a clip
                    // with Eye_Blink curves (the mocap intros) otherwise shows
                    // through between blinks — lids half-closed, snapping open
                    // for one frame at the end of each blink.
                    SetBlink(0f);
                    return;
                }
            }

            blinkPhase += dt;
            float w;
            if (blinkPhase < BlinkClose) w = blinkPhase / BlinkClose;
            else if (blinkPhase < BlinkClose + BlinkHold) w = 1f;
            else if (blinkPhase < BlinkClose + BlinkHold + BlinkOpen) w = 1f - (blinkPhase - BlinkClose - BlinkHold) / BlinkOpen;
            else
            {
                w = 0f;
                blinkPhase = -1f;
                if (blinkPendingDouble)
                {
                    blinkPendingDouble = false;
                    nextBlinkIn = Random.Range(0.15f, 0.3f);
                }
                else
                {
                    nextBlinkIn = Random.Range(s.blinkInterval.x, s.blinkInterval.y);
                }
            }
            SetBlink(w * 100f);
        }

        private void SetBlink(float weight)
        {
            if (faceMesh == null) return;
            if (blinkL >= 0) faceMesh.SetBlendShapeWeight(blinkL, weight);
            if (blinkR >= 0) faceMesh.SetBlendShapeWeight(blinkR, weight);
            blinkLast = weight;
        }

        // ---------- Facial life ----------
        // Additive micro-expression on the CC ExPlus blendshapes: a resting
        // smile bias, slow Perlin drift across brows/lids/mouth (never repeats,
        // per-character seed), and occasional discrete micro-events (a brow
        // raise, a squint, a lip press, a swallow...). Everything is added ON
        // TOP of what the animation / lipsync already wrote this frame — we
        // read the current value first — so it composes with talking kids and
        // authored expressions. Blink and eye-look shapes are never touched;
        // those belong to the gaze layer. Runs before BoneDriver (order 0),
        // which turns lid/brow shapes into the matching bone deformation.

        /// <param name="envelope">Sequencer seam fade (0..1) already applied to the body; keeps face and body dying out together at the loop point.</param>
        private void StepFace(float dt, IdleMotionSettings s, float envelope)
        {
            // A held expression keeps the face layer writing even with the
            // facial life switched off — the mood must still show.
            bool holding = externalActive || externalWritten != null;
            if ((!s.faceEnabled && !holding) || !ResolveFace())
            {
                if (faceDirty) ClearFace();
                return;
            }

            float k = s.faceIntensity * envelope;
            float ft = time * s.driftFrequency;

            // The animation base was captured once (CaptureFaceBaseNow); we
            // never re-read the mesh here — it holds our own previous write.
            if (!faceBaseCaptured) CaptureFaceBaseNow();
            for (int i = 0; i < faceShapes.Length; i++) faceShapes[i].ourLast = 0f;

            // Speech owns the mouth: the idle side of the mouth slots fades
            // out, the held mood on them scales down. Mouth slots are written
            // absolutely while gated, so a resting smile from before the
            // speech can never be left behind on a shape nothing else rewrites.
            float gate = Mathf.SmoothStep(0f, 1f, speechMouthGate);
            idleMouthScale = 1f - gate;
            moodMouthScale = Mathf.Lerp(1f, moodMouthWhileSpeaking, gate);
            if (gate > 0f)
            {
                for (int i = 0; i < faceShapes.Length; i++)
                {
                    if (!IsMouthSlot(i) || faceShapes[i].index < 0) continue;
                    faceMesh.SetBlendShapeWeight(faceShapes[i].index, Mathf.Clamp(BaseFor(i), 0f, 100f));
                }
            }

            // Held mood first, so the life below rides on top of it.
            WriteExternalOffsets();
            if (!s.faceEnabled)
            {
                faceDirty = true;
                return;
            }

            // Resting expression — a faint smile + cheek lift. The asymmetry is
            // a FIXED per-character trait (seeded once), not a moving value:
            // redistributing the smile between the corners over time reads as
            // lip twitching, the single most noticeable artifact on a face.
            float smile = s.restingSmile * 30f * k;
            float asym = 0.85f + 0.3f * Mathf.Repeat(seedFace * 0.37f, 1f);
            Add(12, smile * asym); Add(13, smile * (1.7f - asym));
            Add(10, smile * 0.35f); Add(11, smile * 0.35f);

            if (debugLogSmile)
            {
                if (debugFramesLeft <= 0) debugFramesLeft = 120;
                debugFramesLeft--;
                if (debugFramesLeft == 0) debugLogSmile = false;
                int idx = faceShapes[12].index;
                Debug.Log($"[smile] dt={dt:F4} k={k:F3} env={envelope:F3} base={faceShapes[12].baseValue:F2} ours={faceShapes[12].ourLast:F2} mesh={(idx >= 0 ? faceMesh.GetBlendShapeWeight(idx) : -1f):F2}", this);
            }

            // Slow drift — brows breathe up/down, lids relax/tighten, mouth
            // corners wander. Small amplitudes; the point is that nothing is
            // ever perfectly still.
            float d = s.driftAmount * k;
            // Brows: raise (+) vs drop (-) from one signed noise, but split
            // with a soft shoulder rather than max(0,x) — a hard kink at the
            // zero crossing is a visible flick. Asymmetry is slow and small.
            if (s.animateBrows)
            {
                float browDrift = Noise(seedFace, ft);                       // -1..1
                float browAsym = Noise(seedFace + 11f, ft * 0.6f) * 0.25f;
                float raiseL = Soft(browDrift + browAsym), raiseR = Soft(browDrift - browAsym);
                float dropL = Soft(-browDrift + browAsym), dropR = Soft(-browDrift - browAsym);
                Add(0, raiseL * d); Add(1, raiseR * d);
                Add(2, Soft(browDrift) * d * 0.6f); Add(3, Soft(browDrift) * d * 0.6f);
                Add(4, dropL * d * 0.7f); Add(5, dropR * d * 0.7f);
            }
            float lidDrift = Noise01(seedFace + 23f, ft * 0.8f);       // 0..1
            Add(22, lidDrift * d * 0.8f); Add(23, lidDrift * d * 0.8f);
            // Mouth: ONE slow, one-sided quantity — a gentle lip press that
            // eases in and out. No sign flipping between press and dimple: a
            // zero-crossing there switches the lip shape and reads as a
            // twitch. Half the drift rate of the brows; a third the amplitude.
            float mouthDrift = Noise01(seedFace + 37f, ft * 0.35f);
            Add(14, mouthDrift * d * 0.35f);
            Add(15, mouthDrift * d * 0.35f);
            // Nostrils flare faintly with the breath cycle.
            float breathHz = Mathf.Max(s.breathsPerMinute + breathJitter * s.breathVariation, 1f) / 60f;
            float inhale = Mathf.Max(0f, Mathf.Sin((time * breathHz + breathPhase) * 2f * Mathf.PI));
            Add(20, inhale * d * 0.5f); Add(21, inhale * d * 0.5f);

            // Discrete micro-events.
            faceEventTimer -= dt;
            if (faceEvent == FaceEvent.None && faceEventTimer <= 0f)
            {
                faceEvent = (FaceEvent)Random.Range(1, FaceEventCount);
                // Brow events are part of the brow toggle: re-roll to a non-brow event.
                int guard = 0;
                while (!s.animateBrows && (faceEvent == FaceEvent.BrowRaise || faceEvent == FaceEvent.BrowFurrow) && guard++ < 8)
                    faceEvent = (FaceEvent)Random.Range(1, FaceEventCount);
                if (!s.animateBrows && (faceEvent == FaceEvent.BrowRaise || faceEvent == FaceEvent.BrowFurrow))
                    faceEvent = FaceEvent.LipPress;
                // While speaking the mouth belongs to lip sync: no mouth events.
                if (gate > 0.5f && (faceEvent == FaceEvent.LipPress || faceEvent == FaceEvent.HalfSmile))
                    faceEvent = Random.value < 0.5f ? FaceEvent.Squint : FaceEvent.Swallow;
                faceEventPhase = 0f;
                faceEventSide = Random.value < 0.5f ? -1f : 1f;
                faceEventDuration = faceEvent switch
                {
                    FaceEvent.Swallow => Random.Range(0.9f, 1.3f),
                    FaceEvent.BrowRaise => Random.Range(0.8f, 1.6f),
                    FaceEvent.Squint => Random.Range(0.7f, 1.4f),
                    FaceEvent.LipPress => Random.Range(0.6f, 1.2f),
                    FaceEvent.BrowFurrow => Random.Range(1.0f, 2.2f),
                    _ => Random.Range(1.2f, 2.5f),
                };
            }
            if (faceEvent != FaceEvent.None)
            {
                faceEventPhase += dt / faceEventDuration;
                if (faceEventPhase >= 1f)
                {
                    faceEvent = FaceEvent.None;
                    faceEventTimer = Random.Range(s.eventInterval.x, s.eventInterval.y);
                }
                else
                {
                    // Bell envelope: quick in, hold, ease out.
                    float e = Mathf.Sin(faceEventPhase * Mathf.PI);
                    e = Mathf.Pow(e, 0.7f) * s.eventStrength * k;
                    float sideL = faceEventSide < 0f ? 1f : 0.55f;
                    float sideR = faceEventSide > 0f ? 1f : 0.55f;
                    switch (faceEvent)
                    {
                        case FaceEvent.BrowRaise:
                            Add(0, e * sideL); Add(1, e * sideR); Add(2, e * 0.7f * sideL); Add(3, e * 0.7f * sideR);
                            break;
                        case FaceEvent.Squint:
                            Add(8, e * 0.8f); Add(9, e * 0.8f); Add(10, e * 0.4f); Add(11, e * 0.4f);
                            break;
                        case FaceEvent.LipPress:
                            Add(14, e); Add(15, e); Add(16, e * 0.3f); Add(17, e * 0.3f);
                            break;
                        case FaceEvent.Swallow:
                            // Up then down through the event.
                            Add(18, Mathf.Sin(Mathf.Clamp01(faceEventPhase * 2f) * Mathf.PI) * s.eventStrength * 1.5f * k);
                            Add(19, Mathf.Sin(Mathf.Clamp01(faceEventPhase * 2f - 1f) * Mathf.PI) * s.eventStrength * 1.5f * k);
                            break;
                        case FaceEvent.BrowFurrow:
                            Add(4, e * 0.7f); Add(5, e * 0.7f); Add(6, e * 0.9f); Add(7, e * 0.9f);
                            break;
                        case FaceEvent.HalfSmile:
                            Add(faceEventSide < 0f ? 12 : 13, e * 1.2f);
                            Add(faceEventSide < 0f ? 16 : 17, e * 0.6f);
                            Add(faceEventSide < 0f ? 10 : 11, e * 0.5f);
                            break;
                    }
                }
            }

            faceDirty = true;
        }

        private void Add(int shape, float amount)
        {
            if (shape >= faceShapes.Length) return;
            if (IsMouthSlot(shape)) amount *= addingMood ? moodMouthScale : idleMouthScale;
            if (amount <= 0f) return;
            FaceShape fs = faceShapes[shape];
            if (fs.index < 0) return;
            // Absolute write of base + our total for this frame — never a
            // read-modify-write of the mesh, so nothing can compound.
            float ours = fs.ourLast + amount;
            faceMesh.SetBlendShapeWeight(fs.index, Mathf.Clamp(BaseFor(shape) + ours, 0f, 100f));
            faceShapes[shape].ourLast = ours;
        }

        private static float Noise01(float seed, float t)
        {
            return Mathf.Clamp01(Mathf.PerlinNoise(seed, Mathf.Repeat(t, 512f)));
        }

        /// <summary>Soft positive part: 0 below -0.2, x above 0.2, smooth in between — no kink at zero.</summary>
        private static float Soft(float x)
        {
            return x * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.2f, 0.2f, x));
        }

        private bool ResolveFace()
        {
            // Key on the actual data, not a flag: after a script hot-reload
            // Unity may restore the mesh reference but not our private struct
            // array, and a stale "resolved" flag would then hand out nulls.
            if (faceMesh != null && faceShapes != null && faceShapes.Length == FaceShapeNames.Length) return true;
            if (faceResolved && faceMesh == null) return false;
            faceResolved = true;

            CharacterReferenceHolder holder = GetComponentInParent<CharacterReferenceHolder>();
            if (holder != null)
            {
                if (holder.bodyMesh == null) holder.ResolveReferences();
                faceMesh = holder.bodyMesh;
            }
            if (faceMesh == null)
            {
                foreach (SkinnedMeshRenderer smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr.name == "CC_Base_Body") { faceMesh = smr; break; }
            }
            if (faceMesh == null || faceMesh.sharedMesh == null) { faceMesh = null; return false; }

            Mesh mesh = faceMesh.sharedMesh;
            blinkL = mesh.GetBlendShapeIndex("Eye_Blink_L");
            blinkR = mesh.GetBlendShapeIndex("Eye_Blink_R");
            faceShapes = new FaceShape[FaceShapeNames.Length];
            int found = 0;
            for (int i = 0; i < FaceShapeNames.Length; i++)
            {
                faceShapes[i].index = mesh.GetBlendShapeIndex(FaceShapeNames[i]);
                if (faceShapes[i].index >= 0) found++;
            }
            if (found == 0)
            {
                Debug.LogWarning($"ProceduralIdleMotion on '{name}': no CC facial blendshapes found on '{faceMesh.name}' — facial life disabled.", this);
                faceMesh = null;
                return false;
            }
            return true;
        }

        /// <summary>Removes our additive contribution: every driven shape goes back to the captured animation base.</summary>
        private void ClearFace()
        {
            externalWritten = null;
            if (faceMesh == null || faceShapes == null) { faceDirty = false; return; }
            for (int i = 0; i < faceShapes.Length; i++)
            {
                if (faceShapes[i].index < 0) continue;
                faceMesh.SetBlendShapeWeight(faceShapes[i].index, faceBaseCaptured ? faceShapes[i].baseValue : 0f);
                faceShapes[i].ourLast = 0f;
            }
            faceDirty = false;
        }


        /// <summary>
        /// Zeroes every expression shape this layer drives (plus blink) on the
        /// mesh and forgets the captured base — the "un-grimace" button for a
        /// face left in a bad state by an old build or a stuck editor session.
        /// </summary>
        [ContextMenu("Reset Face Shapes")]
        public void ResetFaceShapes()
        {
            faceResolved = false;
            faceShapes = null;
            if (!ResolveFace()) return;
            for (int i = 0; i < faceShapes.Length; i++)
            {
                if (faceShapes[i].index < 0) continue;
                faceMesh.SetBlendShapeWeight(faceShapes[i].index, 0f);
                faceShapes[i].baseValue = 0f;
                faceShapes[i].ourLast = 0f;
            }
            SetBlink(0f);
            faceBaseCaptured = true;
            faceDirty = false;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(faceMesh);
#endif
        }

        /// <summary>
        /// Restores every facial-life setting (drift, events, resting smile,
        /// blinking) to defaults — on the local settings, or on the active
        /// mode of the ambient player's profile when that's what drives this
        /// character. Body settings are left alone.
        /// </summary>
        public void ResetFacialSettings()
        {
            IdleMotionSettings target = ActiveSettings;
            if (target == null) return;
            var d = new IdleMotionSettings();
            target.faceEnabled = d.faceEnabled;
            target.faceIntensity = d.faceIntensity;
            target.restingSmile = d.restingSmile;
            target.animateBrows = d.animateBrows;
            target.driftAmount = d.driftAmount;
            target.driftFrequency = d.driftFrequency;
            target.eventInterval = d.eventInterval;
            target.eventStrength = d.eventStrength;
            target.blinkEnabled = d.blinkEnabled;
            target.blinkInterval = d.blinkInterval;
            target.doubleBlinkChance = d.doubleBlinkChance;
#if UNITY_EDITOR
            Object asset = settingsSource != null && target != settings ? settingsSource.IdleSettingsAsset : null;
            UnityEditor.EditorUtility.SetDirty(asset != null ? asset : this);
#endif
        }

        // Note: deliberately NO OnValidate-driven base re-capture. Editing our
        // own settings does not rewrite the pose; re-capturing then would read
        // base + our last write off the mesh and bake our contribution into
        // the base — the exact "never goes back to normal" bug. Only a real
        // pose rewrite (PoseSampled / Play Mode frame) refreshes the base.

        /// <summary>World-axis additive offset, same convention as the gaze system — independent of CC bone axes.</summary>
        private void Apply(Transform bone, float yaw, float pitch, float roll)
        {
            if (bone == null) return;
            bone.rotation =
                Quaternion.AngleAxis(yaw, root.up) *
                Quaternion.AngleAxis(pitch, root.right) *
                Quaternion.AngleAxis(roll, root.forward) *
                bone.rotation;
        }

        // ------------------------------------------------------------------ standing: planted feet

        private void CaptureFeet()
        {
            feetCaptured = footL != null && footR != null;
            if (!feetCaptured) return;
            footPosL = footL.position; footRotL = footL.rotation;
            footPosR = footR.position; footRotR = footR.rotation;
        }

        /// <summary>
        /// Puts both feet back where the animation had them after the pelvis
        /// moved: analytic two-bone IK per leg (the knee bends in its current
        /// plane, forward when the leg is straight), then the foot's own
        /// rotation is restored so the sole stays flat.
        /// </summary>
        private void RestoreFeet()
        {
            if (!feetCaptured) return;
            SolveLeg(upperLegL, lowerLegL, footL, footPosL, footRotL);
            SolveLeg(upperLegR, lowerLegR, footR, footPosR, footRotR);
        }

        private void SolveLeg(Transform upper, Transform lower, Transform foot, Vector3 targetPos, Quaternion targetRot)
        {
            if (upper == null || lower == null || foot == null) return;
            Vector3 a = upper.position, b = lower.position, c = foot.position;
            float lenAB = (b - a).magnitude, lenBC = (c - b).magnitude;
            if (lenAB < 1e-5f || lenBC < 1e-5f) return;

            Vector3 toTarget = targetPos - a;
            float reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(lenAB - lenBC) + 1e-4f, lenAB + lenBC - 1e-4f);

            // Knee angle the law of cosines asks for vs. the one the pose has.
            float wanted = Mathf.Acos(Mathf.Clamp((lenAB * lenAB + lenBC * lenBC - reach * reach) / (2f * lenAB * lenBC), -1f, 1f)) * Mathf.Rad2Deg;
            float current = Vector3.Angle(a - b, c - b);
            Vector3 axis = Vector3.Cross(b - a, c - b);
            if (axis.sqrMagnitude < 1e-8f) axis = -root.right;   // straight leg: the knee bends forward
            axis.Normalize();
            if (Mathf.Abs(current - wanted) > 0.01f)
            {
                // Rotate the shin about the knee; if that moved the foot the
                // wrong way the axis pointed the other way, so flip it.
                Quaternion before = lower.rotation;
                lower.rotation = Quaternion.AngleAxis(current - wanted, axis) * before;
                if (Mathf.Abs((foot.position - a).magnitude - reach) > 1e-3f)
                    lower.rotation = Quaternion.AngleAxis(wanted - current, axis) * before;
            }

            // Swing the whole leg so the foot lands on the target, then level the foot.
            Vector3 now = foot.position - a;
            if (now.sqrMagnitude > 1e-10f && toTarget.sqrMagnitude > 1e-10f)
                upper.rotation = Quaternion.FromToRotation(now, toTarget) * upper.rotation;
            foot.rotation = targetRot;
        }

        private static float Noise(float seed, float t)
        {
            // Wrap the time axis too: Perlin precision decays as t grows (a
            // long-running scene), so fold t into a large-but-safe period.
            // Wrapping causes one tiny discontinuity per period; at 512 s
            // and these amplitudes it's invisible.
            return (Mathf.PerlinNoise(seed, Mathf.Repeat(t, 512f)) - 0.5f) * 2f;
        }

        private bool ResolveBones()
        {
            if (bonesResolved) return true;

            Animator animator = GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman) return false;

            root = animator.transform;
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            upperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            shoulderL = animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
            shoulderR = animator.GetBoneTransform(HumanBodyBones.RightShoulder);
            upperArmL = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            upperArmR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            lowerArmL = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            lowerArmR = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            upperLegL = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            lowerLegL = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            footL = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            upperLegR = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            lowerLegR = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            footR = animator.GetBoneTransform(HumanBodyBones.RightFoot);

            bonesResolved = hips != null && spine != null;
            return bonesResolved;
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(ProceduralIdleMotion))]
    public class ProceduralIdleMotionEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var idle = (ProceduralIdleMotion)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Face", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Reset Face Shapes",
                        "Zero every expression shape this layer drives on the mesh and forget the captured base — fixes a face left in a bad state.")))
                {
                    Undo.RecordObject(idle, "Reset Face Shapes");
                    idle.ResetFaceShapes();
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button(new GUIContent("Reset Facial Settings",
                        "Restore the facial-life settings (drift, events, resting smile, blinking) to defaults — on the profile's active mode when an ambient player drives this character, else on the local settings.")))
                {
                    var source = idle.GetComponent<IIdleMotionSettingsSource>();
                    Object asset = source != null ? source.IdleSettingsAsset : null;
                    Object recordTarget = asset != null ? asset : idle;
                    Undo.RecordObject(recordTarget, "Reset Facial Settings");
                    idle.ResetFacialSettings();
                }
            }
        }
    }
#endif
}
