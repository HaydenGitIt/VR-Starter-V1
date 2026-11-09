using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace catalyst.xr67
{
    /// <summary>
    /// Procedural conversational gaze for a Character Creator rig: eye
    /// contact with a target (the player camera by default) interleaved with
    /// gaze aversions "into thought", micro-saccades, occasional rapid
    /// thinking darts, blinking, and partial head following.
    ///
    /// Grounded in conversation research: speakers hold eye contact roughly
    /// 40% of the time in bouts of a few seconds while listeners hold ~75%
    /// (Argyle); gaze aversion marks cognitive effort such as planning what
    /// to say — typically at the start of an utterance, directed down or to
    /// the side, and children from ~8 years use it like adults
    /// (Doherty-Sneddon); saccades are near-instant while the head joins
    /// gaze shifts slower and only partially, eyes leading (Lee et al.,
    /// "Eyes Alive").
    ///
    /// Eyes are driven through the CC Eye_*_Look_* blendshapes on the body
    /// mesh — the Reallusion BoneDriver turns those into eye-bone rotation
    /// plus eyelid deformation and syncs the other renderers, exactly like
    /// the viseme pipeline. Blinks use Eye_Blink_L/R. Only the head moves at
    /// transform level, as an additive offset on top of the Animator pose.
    ///
    /// Ordering — everything runs in LateUpdate at execution order -10:
    /// after the Animator has posed the skeleton (so an idle animation's
    /// head sway is part of the measurement), before BoneDriver (order 0)
    /// consumes the look shapes. Each frame the head offset is applied
    /// first, then the eye angles are measured against the FINAL head pose —
    /// the eyes compensate for any head motion, animated or procedural, and
    /// stay locked on the target within the same frame.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public class GazeDriver : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Character to animate. Found on this GameObject or its parents when left empty.")]
        public CharacterReferenceHolder character;

        [Tooltip("Optional: used to switch between speaking and idle gaze rhythm. Found via the character's voiceSource when left empty.")]
        public LipSyncAnalyzer analyzer;

        [Tooltip("What to make eye contact with. Main camera when left empty.")]
        public Transform target;

        [Tooltip("Overrides the character's body mesh when assigned.")]
        public SkinnedMeshRenderer bodyMeshOverride;

        [Header("Contact rhythm — speaking (Argyle: ~40% contact)")]
        [Tooltip("Min/max seconds of eye contact per bout while speaking.")]
        public Vector2 speakingContactDuration = new Vector2(1.2f, 3f);

        [Tooltip("Min/max seconds looking away per bout while speaking.")]
        public Vector2 speakingAversionDuration = new Vector2(1.5f, 3.5f);

        [Header("Contact rhythm — idle/listening (~75-85% contact)")]
        [Tooltip("Min/max seconds of eye contact per bout when not speaking.")]
        public Vector2 idleContactDuration = new Vector2(3f, 6f);

        [Tooltip("Min/max seconds looking away per bout when not speaking.")]
        public Vector2 idleAversionDuration = new Vector2(0.5f, 1.2f);

        [Header("Aversion (thinking look)")]
        [Tooltip("Chance an aversion goes downward — the classic thinking direction. The rest splits between sideways and upward (recalling).")]
        [Range(0f, 1f)] public float downwardBias = 0.55f;

        [Tooltip("Max horizontal angle of an aversion, degrees.")]
        public float aversionYawMax = 18f;

        [Tooltip("Max downward angle of an aversion, degrees.")]
        public float aversionPitchDownMax = 14f;

        [Tooltip("Max upward angle of an aversion, degrees.")]
        public float aversionPitchUpMax = 9f;

        [Tooltip("Chance that an aversion while speaking contains a burst of 2-3 rapid darts between nearby points (visible 'thinking').")]
        [Range(0f, 1f)] public float thinkingDartChance = 0.35f;

        [Header("Motion")]
        [Tooltip("Response rate of eye saccades. High — real saccades take tens of milliseconds.")]
        public float saccadeRate = 25f;

        [Tooltip("Fraction of the total gaze angle the head follows (eyes do the rest).")]
        [Range(0f, 1f)] public float headFollow = 0.35f;

        [Tooltip("Response rate of head movement — far slower than the eyes.")]
        public float headRate = 3.5f;

        public float maxHeadYaw = 12f;
        public float maxHeadPitch = 8f;

        [Tooltip("Drive the head bone. Disable if something else owns head aim — the eyes alone still track the target accurately.")]
        public bool driveHead = true;

        [Header("Scripted cue targets (intro GazeCue at another character)")]
        [Tooltip("A neighbour sits far off to the side — beyond the conversational head/eye limits above, which are tuned for the user in front. While an intro cue points at a non-default target, these limits apply instead so the head actually turns toward them.")]
        [Range(0f, 1f)] public float cueHeadFollow = 0.8f;
        public float cueMaxHeadYaw = 60f;
        public float cueMaxHeadPitch = 25f;

        [Tooltip("Clamp on the measured gaze angle while a cue target is active (45° normally). The eye shapes still cap at their own range; the head does the bulk of a wide turn.")]
        public float cueEyeYawMax = 70f;

        [Tooltip("Response rate of head movement while a cue target is active, replacing headRate. Slower than the conversational rate on purpose: a cue turn covers up to 60° rather than 12°, and at the same rate that much travel reads as a snap. Exponential approach — the head is ~90% of the way there after 2.3/rate seconds, so 2 gives a ~1.15 s turn against ~0.65 s at 3.5.")]
        public float cueHeadRate = 2f;

        [Tooltip("Seconds the aim point takes to glide from the previous target to a new one when the target changes (gaze cues, the end of a cue). 0 = snap.")]
        public float targetBlendSeconds = 1f;

        /// <summary>Set by IntroSequencePlayer while a GazeCue points at a non-default target — switches to the cue limits.</summary>
        public bool CueTargetActive { get; set; }

        [Header("Eye range (degrees at look-shape weight 100)")]
        [Tooltip("Read the true per-eye ranges from this character's BoneDriver. The rotation each look shape delivers is baked per character and is neither left/right symmetric nor equal between the eyes, so a single assumed figure aims the gaze off to one side. Disable only to force the manual values below.")]
        public bool useRigEyeRanges = true;

        [Tooltip("Used when the rig ranges are unavailable, or when Use Rig Eye Ranges is off.")]
        public float eyeYawRange = 30f;
        public float eyePitchUpRange = 20f;
        public float eyePitchDownRange = 25f;

        [Header("Blinking")]
        public bool blinkingEnabled = true;

        [Tooltip("Min/max seconds between spontaneous blinks (~15-20 per minute).")]
        public Vector2 blinkInterval = new Vector2(2.5f, 5.5f);

        [Tooltip("Chance to also blink on a large gaze shift, as humans do.")]
        [Range(0f, 1f)] public float gazeShiftBlinkChance = 0.5f;

        private const float BlinkCloseTime = 0.07f;
        private const float BlinkHoldTime = 0.03f;
        private const float BlinkOpenTime = 0.12f;
        private const float MicroSaccadeMinInterval = 0.3f;
        private const float MicroSaccadeMaxInterval = 0.9f;
        private const float SpeakLoudnessThreshold = 0.02f;
        private const float SpeakingHold = 0.5f;

        private enum GazeState { Contact, Averted }

        private SkinnedMeshRenderer _bodyMesh;
        private Transform _headBone;
        private Transform _leftEyeBone;
        private Transform _rightEyeBone;

        // Degrees each look shape really delivers at weight 100, per eye.
        private EyeLookRanges _eyeRanges = EyeLookRanges.Nominal;

        // Rotation from the head bone's frame to the "eyes forward" frame,
        // captured at Awake where the neutral pose looks along root.forward.
        // Applying it to the head's current rotation yields where the eyes
        // point at zero look-shape weight, independent of the CC bone axes.
        private Quaternion _headToGaze = Quaternion.identity;

        private int _lookLLeft = -1, _lookLRight = -1, _lookLUp = -1, _lookLDown = -1;
        private int _lookRLeft = -1, _lookRRight = -1, _lookRUp = -1, _lookRDown = -1;
        private int _blinkL = -1, _blinkR = -1;
        // The character's facial-life layer, when it has one. It blinks too, so
        // only one of us may write the eyelids: while it drives the blink this
        // driver stops its own timer and hands gaze-shift blinks over to it.
        private ProceduralIdleMotion _idleMotion;
        private bool _idleMotionResolved;

        private GazeState _state = GazeState.Contact;
        private float _stateTimer;
        private Vector2 _aversionOffset;   // yaw right-positive, pitch down-positive (deg)
        private Vector2 _microOffset;
        private float _microTimer;
        private Vector2 _gazeOffset;       // smoothed offset relative to the target direction
        private Vector2 _headOffset;       // smoothed head angles actually applied
        private int _dartsLeft;
        private float _dartTimer;
        private float _speakTimer;
        private bool _wasSpeaking;

        private float _nextBlinkIn;
        private float _blinkPhase = -1f;   // <0 idle, otherwise seconds into the blink
        private float _lastBlinkTime;

        // The head turn is additive on the animated head; this keeps it from
        // stacking on itself when the Animator did not re-pose the head
        // (culled off-screen, a clip without head curves).
        private readonly AdditiveBoneLayer _headLayer = new AdditiveBoneLayer();

        private void Awake()
        {
            if (character == null) character = GetComponentInParent<CharacterReferenceHolder>();
            if (analyzer == null && character != null && character.voiceSource != null)
                analyzer = character.voiceSource.GetComponent<LipSyncAnalyzer>();

            _bodyMesh = bodyMeshOverride;
            if (_bodyMesh == null && character != null)
            {
                if (character.bodyMesh == null) character.ResolveReferences();
                _bodyMesh = character.bodyMesh;
            }
            if (character != null)
            {
                if (character.headBone == null || character.leftEyeBone == null || character.rightEyeBone == null)
                    character.ResolveReferences();
                _headBone = character.headBone;
                _leftEyeBone = character.leftEyeBone;
                _rightEyeBone = character.rightEyeBone;
            }

            if (_bodyMesh == null)
            {
                Debug.LogError("GazeDriver: no body mesh — disabling.", this);
                enabled = false;
                return;
            }

            if (_headBone != null)
                _headToGaze = ComputeHeadToGaze();

            _eyeRanges = EyeLookRanges.Nominal;
            _eyeRanges.leftYawLeft = _eyeRanges.leftYawRight = _eyeRanges.rightYawLeft = _eyeRanges.rightYawRight = eyeYawRange;
            _eyeRanges.leftPitchUp = _eyeRanges.rightPitchUp = eyePitchUpRange;
            _eyeRanges.leftPitchDown = _eyeRanges.rightPitchDown = eyePitchDownRange;
            if (useRigEyeRanges)
            {
                EyeLookRanges rig = EyeLookRanges.Resolve(Root, this);
                if (rig.resolved) _eyeRanges = rig;
            }

            CacheShapeIndices();
            _stateTimer = RandomRange(idleContactDuration);
            _nextBlinkIn = RandomRange(blinkInterval);
        }

        private Transform Root => character != null ? character.Root.transform : transform;

        /// <summary>
        /// Head-frame → eyes-forward-frame calibration. The scene's serialized
        /// pose is arbitrary (editor previews save mid-animation poses into the
        /// scene), so calibrating from the live Awake pose skews the eye frame
        /// by whatever the head was doing when the scene was last saved — the
        /// character then looks a constant wrong direction at every target.
        /// Prefer the humanoid avatar's REST pose, where the character looks
        /// along the model root's forward by construction; fall back to the
        /// live pose only when no humanoid avatar is available.
        /// </summary>
        private Quaternion ComputeHeadToGaze()
        {
            Animator animator = character != null ? character.animator : null;
            if (animator == null && character != null && character.Root != null)
                animator = character.Root.GetComponentInChildren<Animator>(true);
            if (animator == null)
                animator = GetComponentInParent<Animator>();

            Avatar avatar = animator != null ? animator.avatar : null;
            if (avatar != null && avatar.isHuman)
            {
                SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
                if (skeleton != null && skeleton.Length > 0)
                {
                    var restByName = new Dictionary<string, Quaternion>(skeleton.Length);
                    foreach (SkeletonBone bone in skeleton)
                        restByName[bone.name] = bone.rotation;

                    // Head's rest rotation in model-root space = product of the
                    // rest local rotations down the live parent chain.
                    var chain = new List<Quaternion>();
                    bool complete = false;
                    for (Transform t = _headBone; t != null; t = t.parent)
                    {
                        if (t == animator.transform) { complete = true; break; }
                        if (!restByName.TryGetValue(t.name, out Quaternion rest)) break;
                        chain.Add(rest);
                    }
                    if (complete)
                    {
                        Quaternion restHead = Quaternion.identity;
                        for (int i = chain.Count - 1; i >= 0; i--)
                            restHead *= chain[i];
                        // gazeFrame = head.rotation * headToGaze; at rest this
                        // must equal the model root's rotation (eyes forward).
                        return Quaternion.Inverse(restHead);
                    }
                }
            }

            // Legacy behavior — only correct if the saved pose is neutral.
            return Quaternion.Inverse(_headBone.rotation) * Root.rotation;
        }

        private void CacheShapeIndices()
        {
            Mesh mesh = _bodyMesh.sharedMesh;
            var missing = new List<string>();
            int Find(string name)
            {
                int i = mesh.GetBlendShapeIndex(name);
                if (i < 0) missing.Add(name);
                return i;
            }

            _lookLLeft = Find("Eye_L_Look_L");
            _lookLRight = Find("Eye_L_Look_R");
            _lookLUp = Find("Eye_L_Look_Up");
            _lookLDown = Find("Eye_L_Look_Down");
            _lookRLeft = Find("Eye_R_Look_L");
            _lookRRight = Find("Eye_R_Look_R");
            _lookRUp = Find("Eye_R_Look_Up");
            _lookRDown = Find("Eye_R_Look_Down");
            _blinkL = Find("Eye_Blink_L");
            _blinkR = Find("Eye_Blink_R");

            if (missing.Count > 0)
                Debug.LogWarning($"GazeDriver: blendshapes not found on '{_bodyMesh.name}': {string.Join(", ", missing)}.", this);
        }

        private Transform _cachedCameraTarget; // Camera.main fallback, resolved once
        // The point actually aimed at: glides from the previous target to the current one on a change.
        private Transform _aimTarget;
        private Vector3 _aimFrom;
        private float _aimBlend = 1f;

        private Transform Target
        {
            get
            {
                if (target != null) return target;
                if (_cachedCameraTarget == null)
                {
                    Camera main = Camera.main;
                    _cachedCameraTarget = main != null ? main.transform : null;
                }
                return _cachedCameraTarget;
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;

            bool speaking = UpdateSpeakingState(dt);
            UpdateStateMachine(dt, speaking);
            UpdateMicroSaccades(dt);
            UpdateDarts(dt);

            // Where the gaze wants to be, relative to straight-at-target.
            Vector2 wanted = _state == GazeState.Averted ? _aversionOffset + _microOffset : _microOffset;

            Vector2 previous = _gazeOffset;
            _gazeOffset += (wanted - _gazeOffset) * SmoothingStep(saccadeRate, dt);

            // Humans often blink while the eyes jump a long way.
            if (blinkingEnabled && (wanted - previous).magnitude > 10f &&
                Time.time - _lastBlinkTime > 1f && Random.value < gazeShiftBlinkChance)
            {
                if (IdleMotionDrivesBlink)
                {
                    _idleMotion.BlinkNow();
                    _lastBlinkTime = Time.time;
                }
                else
                    StartBlink();
            }

            // Head first (on top of the Animator pose evaluated this frame),
            // then eyes measured against the final head orientation — this is
            // what keeps the gaze locked on target while the idle animation
            // sways the head.
            UpdateAimPoint(dt);
            ApplyHeadOffset(dt);
            WriteEyeShapes();
            UpdateBlink(dt);
        }

        private void ApplyHeadOffset(float dt)
        {
            // Measured in the ANIMATED head's own frame (before this frame's
            // offset), like the eyes are — not the character root, whose
            // forward need not match the facing (prefab root orientation,
            // a Timeline clip's root rotation). With the small conversational
            // limits a root mismatch hid inside the eyes' correction; the
            // wide cue turns made it visible.
            if (_headBone != null) _headLayer.Begin(_headBone);   // restores last frame's turn if it is still there
            Quaternion animatedFrame = _headBone != null ? _headBone.rotation * _headToGaze : Root.rotation;
            Vector2 rootAngles = RootAnglesToTarget(animatedFrame);
            float follow = CueTargetActive ? cueHeadFollow : headFollow;
            float yawMax = CueTargetActive ? cueMaxHeadYaw : maxHeadYaw;
            float pitchMax = CueTargetActive ? cueMaxHeadPitch : maxHeadPitch;
            var headWanted = new Vector2(
                Mathf.Clamp((rootAngles.x + _gazeOffset.x) * follow, -yawMax, yawMax),
                Mathf.Clamp((rootAngles.y + _gazeOffset.y) * follow, -pitchMax, pitchMax));

            _headOffset += (headWanted - _headOffset) * SmoothingStep(CueTargetActive ? cueHeadRate : headRate, dt);

            if (driveHead && _headBone != null)
            {
                // Yaw about world up, pitch about the animated head frame's right —
                // on top of the animated pose, independent of the CC bone axes
                // and of the character root's orientation.
                _headBone.rotation =
                    Quaternion.AngleAxis(_headOffset.x, Vector3.up) *
                    Quaternion.AngleAxis(_headOffset.y, animatedFrame * Vector3.right) *
                    _headBone.rotation;
            }
            _headLayer.Commit();
        }

        private bool UpdateSpeakingState(float dt)
        {
            if (analyzer != null && analyzer.Loudness > SpeakLoudnessThreshold)
                _speakTimer = SpeakingHold;
            else
                _speakTimer -= dt;

            bool speaking = _speakTimer > 0f;

            // Utterance planning: look away right as speech begins.
            if (speaking && !_wasSpeaking)
                EnterAversion(speaking: true, planned: true);

            _wasSpeaking = speaking;
            return speaking;
        }

        private void UpdateStateMachine(float dt, bool speaking)
        {
            _stateTimer -= dt;
            if (_stateTimer > 0f) return;

            if (_state == GazeState.Averted)
            {
                _state = GazeState.Contact;
                _stateTimer = RandomRange(speaking ? speakingContactDuration : idleContactDuration);
            }
            else
            {
                EnterAversion(speaking, planned: false);
            }
        }

        private void EnterAversion(bool speaking, bool planned)
        {
            _state = GazeState.Averted;
            _aversionOffset = PickAversionOffset();
            float scale = planned ? 0.8f : 1f; // planning glances run a touch shorter
            Vector2 range = speaking ? speakingAversionDuration : idleAversionDuration;
            _stateTimer = RandomRange(range) * scale;

            _dartsLeft = 0;
            if (speaking && Random.value < thinkingDartChance)
            {
                _dartsLeft = Random.Range(2, 4);
                _dartTimer = Random.Range(0.12f, 0.25f);
            }
        }

        private Vector2 PickAversionOffset()
        {
            float yaw = Random.Range(6f, aversionYawMax) * (Random.value < 0.5f ? -1f : 1f);
            float roll = Random.value;
            float pitch;
            if (roll < downwardBias)
                pitch = Random.Range(4f, aversionPitchDownMax);            // down — thinking
            else if (roll < downwardBias + 0.25f)
                pitch = Random.Range(-2f, 2f);                             // level sideways
            else
                pitch = -Random.Range(3f, aversionPitchUpMax);             // up — recalling
            return new Vector2(yaw, pitch);
        }

        private void UpdateMicroSaccades(float dt)
        {
            _microTimer -= dt;
            if (_microTimer > 0f) return;
            _microTimer = Random.Range(MicroSaccadeMinInterval, MicroSaccadeMaxInterval);
            _microOffset = new Vector2(Random.Range(-1.5f, 1.5f), Random.Range(-1f, 1f));
        }

        private void UpdateDarts(float dt)
        {
            if (_state != GazeState.Averted || _dartsLeft <= 0) return;
            _dartTimer -= dt;
            if (_dartTimer > 0f) return;

            _dartsLeft--;
            _dartTimer = Random.Range(0.12f, 0.25f);
            _aversionOffset += new Vector2(Random.Range(-6f, 6f), Random.Range(-4f, 4f));
            _aversionOffset.x = Mathf.Clamp(_aversionOffset.x, -aversionYawMax, aversionYawMax);
            _aversionOffset.y = Mathf.Clamp(_aversionOffset.y, -aversionPitchUpMax, aversionPitchDownMax);
        }

        /// <summary>Yaw/pitch (right/down positive, deg) from the character's facing to the target. Used to decide head follow.</summary>
        private Vector2 RootAnglesToTarget(Quaternion frame)
        {
            if (Target == null) return Vector2.zero;

            Transform root = Root;
            Vector3 from = _headBone != null ? _headBone.position : root.position + root.up * 1.5f;
            Vector3 local = Quaternion.Inverse(frame) * (AimPoint() - from).normalized;
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            float yawMax = CueTargetActive ? cueEyeYawMax : 45f;
            return new Vector2(Mathf.Clamp(yaw, -yawMax, yawMax), Mathf.Clamp(pitch, -30f, 30f));
        }

        /// <summary>
        /// Yaw/pitch from the eyes' CURRENT neutral direction (head pose as
        /// finally animated this frame, including our offset) to the target.
        /// Writing these angles as look shapes lands the gaze exactly on the
        /// target regardless of what the idle animation did to the head.
        /// </summary>
        private Vector2 EyeAnglesToTarget(Vector3 from)
        {
            if (Target == null) return Vector2.zero;

            Quaternion gazeFrame = _headBone != null ? _headBone.rotation * _headToGaze : Root.rotation;
            Vector3 local = Quaternion.Inverse(gazeFrame) * (AimPoint() - from).normalized;
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            float yawMax = CueTargetActive ? cueEyeYawMax : 45f;
            return new Vector2(Mathf.Clamp(yaw, -yawMax, yawMax), Mathf.Clamp(pitch, -30f, 30f));
        }

        /// <summary>
        /// Notices a target change and restarts the glide from where the
        /// previous target was (or the camera fallback), so the switch reads
        /// as the character turning rather than teleporting their gaze.
        /// </summary>
        private void UpdateAimPoint(float dt)
        {
            Transform current = Target;
            if (current != _aimTarget)
            {
                // Start from the point we are looking at right now (mid-glide included).
                _aimFrom = _aimTarget != null ? AimPoint() : (current != null ? current.position : Vector3.zero);
                _aimTarget = current;
                _aimBlend = targetBlendSeconds > 0f && current != null ? 0f : 1f;
            }
            if (_aimBlend < 1f)
                _aimBlend = Mathf.Min(1f, _aimBlend + dt / Mathf.Max(0.0001f, targetBlendSeconds));
        }

        /// <summary>World point the gaze is aimed at this frame — the target itself once a glide has finished.</summary>
        private Vector3 AimPoint()
        {
            if (_aimTarget == null) return _aimFrom;
            if (_aimBlend >= 1f) return _aimTarget.position;
            return Vector3.Lerp(_aimFrom, _aimTarget.position, Mathf.SmoothStep(0f, 1f, _aimBlend));
        }

        /// <summary>That eye's own position, falling back to the head when the eye bones are missing.</summary>
        private Vector3 EyeOrigin(Transform eyeBone)
        {
            if (eyeBone != null) return eyeBone.position;
            return _headBone != null ? _headBone.position : Root.position + Root.up * 1.5f;
        }

        private void WriteEyeShapes()
        {
            // Measured per eye from that eye's own position, so the pair
            // converges ON the target rather than running parallel past it,
            // and inverted through the rotation each eye's shapes really
            // deliver rather than one assumed figure for all of them.
            Vector2 leftEye = EyeAnglesToTarget(EyeOrigin(_leftEyeBone)) + _gazeOffset;
            Vector2 rightEye = EyeAnglesToTarget(EyeOrigin(_rightEyeBone)) + _gazeOffset;

            EyeLookRanges.ShapeWeights(leftEye, 1f,
                _eyeRanges.leftYawLeft, _eyeRanges.leftYawRight, _eyeRanges.leftPitchUp, _eyeRanges.leftPitchDown,
                out float lL, out float lR, out float lU, out float lD);
            EyeLookRanges.ShapeWeights(rightEye, 1f,
                _eyeRanges.rightYawLeft, _eyeRanges.rightYawRight, _eyeRanges.rightPitchUp, _eyeRanges.rightPitchDown,
                out float rL, out float rR, out float rU, out float rD);

            SetShape(_lookLRight, lR); SetShape(_lookRRight, rR);
            SetShape(_lookLLeft, lL); SetShape(_lookRLeft, rL);
            SetShape(_lookLDown, lD); SetShape(_lookRDown, rD);
            SetShape(_lookLUp, lU); SetShape(_lookRUp, rU);
        }

        /// <summary>
        /// True while a ProceduralIdleMotion on this character writes the
        /// eyelids — two independent blink timers on one face read as a
        /// twitchy double blink, most visibly during the intro timelines where
        /// gaze cues add shift-blinks on top.
        /// </summary>
        private bool IdleMotionDrivesBlink
        {
            get
            {
                if (!_idleMotionResolved)
                {
                    _idleMotionResolved = true;
                    _idleMotion = Root.GetComponentInChildren<ProceduralIdleMotion>(true);
                    if (_idleMotion == null) _idleMotion = GetComponentInParent<ProceduralIdleMotion>();
                }
                return _idleMotion != null && _idleMotion.isActiveAndEnabled && _idleMotion.DrivesBlink;
            }
        }

        private void UpdateBlink(float dt)
        {
            if (!blinkingEnabled) return;
            // Yield to the facial-life layer; a blink already in progress
            // finishes so the lids never freeze half-closed.
            if (_blinkPhase < 0f && IdleMotionDrivesBlink) return;

            if (_blinkPhase < 0f)
            {
                _nextBlinkIn -= dt;
                if (_nextBlinkIn <= 0f) StartBlink();
            }

            float w = 0f;
            if (_blinkPhase >= 0f)
            {
                _blinkPhase += dt;
                if (_blinkPhase < BlinkCloseTime)
                    w = _blinkPhase / BlinkCloseTime;
                else if (_blinkPhase < BlinkCloseTime + BlinkHoldTime)
                    w = 1f;
                else if (_blinkPhase < BlinkCloseTime + BlinkHoldTime + BlinkOpenTime)
                    w = 1f - (_blinkPhase - BlinkCloseTime - BlinkHoldTime) / BlinkOpenTime;
                else
                {
                    _blinkPhase = -1f;
                    _nextBlinkIn = RandomRange(blinkInterval);
                }
            }

            // Written every frame, open included: while this driver blinks
            // the lids it owns them. Writing only mid-blink let a clip with
            // eyelid curves (the mocap intros) show through between blinks —
            // lids half-closed, snapping open for the last frame of each blink.
            SetShape(_blinkL, w * 100f);
            SetShape(_blinkR, w * 100f);
        }

        private void StartBlink()
        {
            _blinkPhase = 0f;
            _lastBlinkTime = Time.time;
        }

        private void SetShape(int index, float weight)
        {
            if (index >= 0) _bodyMesh.SetBlendShapeWeight(index, weight);
        }

        private static float SmoothingStep(float rate, float dt)
        {
            return 1f - Mathf.Exp(-rate * dt);
        }

        private static float RandomRange(Vector2 range)
        {
            return Random.Range(range.x, range.y);
        }

        private void OnDisable()
        {
            _headLayer.RestoreAll();
            _headLayer.Reset();
            if (_bodyMesh == null) return;
            SetShape(_lookLLeft, 0f); SetShape(_lookLRight, 0f); SetShape(_lookLUp, 0f); SetShape(_lookLDown, 0f);
            SetShape(_lookRLeft, 0f); SetShape(_lookRRight, 0f); SetShape(_lookRUp, 0f); SetShape(_lookRDown, 0f);
            SetShape(_blinkL, 0f); SetShape(_blinkR, 0f);
        }
    }
}
