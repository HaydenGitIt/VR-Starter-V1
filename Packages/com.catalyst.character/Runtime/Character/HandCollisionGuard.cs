using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace catalyst.xr67
{
    /// <summary>
    /// Keeps the hands from cutting through the torso while two body poses
    /// crossfade (e.g. hands-in-front → hands-on-hips). A crossfade blends
    /// joint rotations, and the straight path between two arm poses often
    /// runs through the belly or chest; the authored poses themselves are
    /// fine. So this only acts while <see cref="StudentAmbientPlayer.PoseBlendActivity"/>
    /// is up (mid-blend) and leaves authored poses untouched.
    ///
    /// The torso is a stack of elliptical cross-sections (pelvis, hips,
    /// waist, chest, upper chest) fitted to the body mesh at start — only
    /// vertices skinned to the spine chain count, so arms, legs and head are
    /// ignored. Each frame the wrist, the palm and the middle of the forearm
    /// are tested against it; a point that comes within <see cref="softZone"/>
    /// of the surface is eased outwards (deep points land just outside the
    /// skin), and a two-bone IK on upper arm + forearm moves the hand there
    /// while keeping the elbow's bend plane and the hand's orientation. The
    /// hand ends up sweeping around the body instead of through it.
    ///
    /// Runs in LateUpdate at -9: after the animation pose, ProceduralIdleMotion
    /// (-12) and gaze (-10), before the Reallusion BoneDriver (0). The pose is
    /// rebuilt from the clips every frame, so nothing accumulates. Added at
    /// runtime by StudentAmbientPlayer (Guard Hands During Blends); add it by
    /// hand to tune it and see the fitted torso as gizmos.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-9)]
    public class HandCollisionGuard : MonoBehaviour
    {
        [Serializable]
        public class Section
        {
            [Tooltip("Half the torso's width here, metres.")]
            public float halfWidth = 0.15f;

            [Tooltip("Half the torso's depth (front to back) here, metres.")]
            public float halfDepth = 0.11f;

            [Tooltip("How far the section's centre sits in front of the spine bone, metres (spine bones run near the back).")]
            public float forwardOffset = 0.02f;
        }

        [Header("When")]
        [Tooltip("Correct only while body poses crossfade (recommended): authored poses — even ones where the hands touch the body — are left exactly as animated. Off = guard all the time.")]
        public bool onlyDuringBlends = true;

        [Tooltip("Overall strength of the correction.")]
        [Range(0f, 1f)]
        public float weight = 1f;

        [Header("Torso shape")]
        [Tooltip("Fit the sections to the body mesh on enable (vertices skinned to the spine chain). Off = use the sections below as authored.")]
        public bool fitFromMesh = true;

        [Tooltip("Scales the fitted sections. 1 = the skin as fitted. Authored poses are never corrected (only blends are), so resting hands on the hips is not a reason to go below 1.")]
        [Range(0.6f, 1.3f)]
        public float shapeScale = 1f;

        [Tooltip("Body mesh to fit to. Empty = the skinned mesh under the character with the most vertices (CC_Base_Body).")]
        public SkinnedMeshRenderer bodyMesh;

        [Tooltip("Sections bottom to top: pelvis, hips, waist, chest, upper chest. Filled by the fit; editable with Fit From Mesh off.")]
        public Section[] sections = new Section[SectionCount];

        [Header("Limb thickness")]
        [Tooltip("Radius of the forearm around its bone, metres — the skin, not the bone, must stay out of the torso.")]
        public float forearmRadius = 0.04f;

        [Tooltip("Radius of the wrist around its bone, metres.")]
        public float wristRadius = 0.03f;

        [Tooltip("Half the thickness of the palm, metres.")]
        public float palmRadius = 0.025f;

        [Tooltip("Also test a CapsuleCollider found under each hand bone (e.g. L_hand_collider / R_hand_collider): points along its axis, with its radius — covers the fingers, which the wrist and palm points don't. Only its shape is read; the collider can be disabled or a trigger.")]
        public bool useHandColliders = true;

        [Header("Clearance")]
        [Tooltip("Extra gap kept between limb skin and torso skin, metres.")]
        public float clearance = 0.01f;

        [Tooltip("The push starts this far outside the surface and eases in, so the hand curves around the body rather than bouncing off it, metres.")]
        public float softZone = 0.06f;

        [Tooltip("A pose's own body contact (hands resting on the hips, crossed arms on the chest) is allowed only near where that pose holds the wrist: in full within the first distance, fading to nothing at the second (metres). Smaller = guards more of the path between two poses.")]
        public Vector2 contactReach = new Vector2(0.06f, 0.14f);

        [Tooltip("How fast the correction follows when it grows (per second). High = hands never sink in, even while they move fast.")]
        public float rateIn = 45f;

        [Tooltip("How fast the correction lets go (per second).")]
        public float rateOut = 6f;

        [Header("Debug")]
        [Tooltip("Draw the torso sections and the tested points when selected.")]
        public bool drawGizmos = true;

        [Tooltip("Write a [TestFlow] line after every blend: how deep each hand / forearm would have gone into the torso and how far it was moved out. Also logs the fitted torso at start.")]
        public bool logBlends = true;

        /// <summary>Current correction of the left / right hand, metres (inspector feedback).</summary>
        public float LeftCorrection => arms[0].offset.magnitude;
        public float RightCorrection => arms[1].offset.magnitude;

        private const int SectionCount = 5;

        private class Arm
        {
            public Transform upper, lower, hand, knuckle;
            public CapsuleCollider shape;   // optional hand capsule under the hand bone
            public float side;             // -1 left, +1 right
            public Vector3 offset;          // smoothed world-space correction of the hand
            public Vector3 lastDir;         // fallback push direction for points on the axis
            public float frameDepth;        // deepest skin overlap this frame, metres (before correction)
            public float frameAllow;        // natural contact of the poses being blended, metres
            public float maxDepth, maxFix, maxAllow;  // per blend, for the log
        }

        private readonly Arm[] arms = { new Arm { side = -1f }, new Arm { side = 1f } };
        private Animator animator;
        private Transform hips, spine, chest, upperChest, neck, legL, legR, armL, armR;
        private StudentAmbientPlayer player;
        private bool ready, fitted;

        // Per-frame torso frame: node centres on the spine and their axes.
        private readonly Vector3[] nodePos = new Vector3[SectionCount];
        private readonly Vector3[] nodeRight = new Vector3[SectionCount];
        private readonly Vector3[] nodeFwd = new Vector3[SectionCount];
        private readonly Vector3[] nodeUp = new Vector3[SectionCount];

#if UNITY_EDITOR
        private double lastEditorTime;
#endif

        // ------------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            ready = Resolve();
            if (ready && fitFromMesh) fitted = FitFromMesh();
            if (ready) LogFit(fitted ? $"body mesh '{fittedMeshName}'" : fitFromMesh ? $"skeleton proportions (mesh fit failed: {fitNotes})" : "authored sections");
            else if (Application.isPlaying)
                Debug.LogWarning($"HandCollisionGuard on '{name}': no humanoid Animator with hips / spine / neck found — the guard is off.", this);
            SubscribePlayer(true);
#if UNITY_EDITOR
            lastEditorTime = EditorApplication.timeSinceStartup;
#endif
        }

        private void OnDisable()
        {
            SubscribePlayer(false);
            arms[0].offset = arms[1].offset = Vector3.zero;
        }

        private void OnValidate()
        {
            if (sections == null || sections.Length != SectionCount)
            {
                var resized = new Section[SectionCount];
                for (int i = 0; i < SectionCount; i++)
                    resized[i] = sections != null && i < sections.Length && sections[i] != null ? sections[i] : new Section();
                sections = resized;
            }
        }

        private void SubscribePlayer(bool on)
        {
            if (player == null) return;
            player.PoseSampled -= OnPoseSampled;
            if (on) player.PoseSampled += OnPoseSampled;
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            Step(Time.deltaTime);
        }

        /// <summary>Edit Mode preview: follow the ambient player's evaluations (Play Mode uses LateUpdate).</summary>
        private void OnPoseSampled()
        {
#if UNITY_EDITOR
            if (Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - lastEditorTime), 0f, 0.1f);
            lastEditorTime = now;
            Step(dt);
#endif
        }

        [ContextMenu("Refit Torso From Mesh")]
        private void RefitNow()
        {
            ready = Resolve();
            if (ready) fitted = FitFromMesh();
            Debug.Log(fitted
                ? $"HandCollisionGuard on '{name}': torso fitted — {Describe()}"
                : $"HandCollisionGuard on '{name}': could not fit from the mesh, using the authored sections.", this);
        }

        // ------------------------------------------------------------------ per frame

        private void Step(float dt)
        {
            if (!ready && !(ready = Resolve())) return;

            float activity = onlyDuringBlends ? (player != null ? player.PoseBlendActivity : 0f) : 1f;
            float gain = Mathf.Clamp01(weight) * activity;

            // Which clips make up the pose: an authored pose on its own teaches
            // us its natural contact (crossed arms resting on the chest, hands
            // on the hips); during a blend that contact is allowed and only the
            // overlap beyond it — what the blend itself causes — is corrected.
            AnimationClip mixPrev = null, mixBase = null, mixShot = null;
            float loopW = 1f, shotW = 0f;
            if (player != null) player.GetPoseMix(out mixPrev, out mixBase, out loopW, out mixShot, out shotW);
            AnimationClip settledClip = null;
            if (onlyDuringBlends && activity <= 0f)
                settledClip = mixShot != null && shotW >= 0.999f ? mixShot : mixPrev == null && mixShot == null ? mixBase : null;
            bool measure = gain > 0f || settledClip != null;

            BuildFrame();
            for (int i = 0; i < arms.Length; i++)
            {
                Arm arm = arms[i];
                if (arm.upper == null || arm.lower == null || arm.hand == null) continue;

                Vector3 desired = Vector3.zero;
                if (measure)
                {
                    Vector3 shoulder = arm.upper.position, elbow = arm.lower.position, wrist = arm.hand.position;
                    float upperLen = Vector3.Distance(shoulder, elbow), foreLen = Vector3.Distance(elbow, wrist);
                    Vector3 palm = arm.knuckle != null
                        ? Vector3.Lerp(wrist, arm.knuckle.position, 0.6f)
                        : wrist + (wrist - elbow).normalized * 0.06f;

                    arm.frameDepth = 0f;
                    arm.frameAllow = 0f;
                    desired = Push(wrist, arm, wristRadius);
                    desired = Largest(desired, Push(palm, arm, palmRadius));
                    // Hand capsule (palm + fingers): rigid with the hand, which the IK
                    // keeps oriented, so moving the wrist by v moves it by v.
                    if (useHandColliders && arm.shape != null)
                    {
                        CapsuleSegment(arm.shape, out Vector3 c0, out Vector3 c1, out float cr);
                        for (int k = 0; k <= 3; k++)
                            desired = Largest(desired, Push(Vector3.Lerp(c0, c1, k / 3f), arm, cr));
                    }
                    // Forearm samples: moving the wrist by v moves a point a fraction f
                    // down the forearm by roughly lever(f)·v (the arm turns about the
                    // shoulder), so the wrist must move push / lever(f).
                    for (int k = 1; k <= 3; k++)
                    {
                        float f = k * 0.25f;
                        float lever = Mathf.Max((upperLen + f * foreLen) / Mathf.Max(upperLen + foreLen, 1e-4f), 0.4f);
                        desired = Largest(desired, Push(Vector3.Lerp(elbow, wrist, f), arm, forearmRadius) / lever);
                    }

                    float need = desired.magnitude;
                    Vector3 local = Quaternion.Inverse(hips.rotation) * (wrist - hips.position);
                    if (settledClip != null)
                    {
                        Learn(settledClip, i, need, local);
                        desired = Vector3.zero;
                    }
                    else if (onlyDuringBlends && need > 1e-5f)
                    {
                        // Each pose's own contact counts only near where that pose
                        // holds the hand: leaving the hips, the hips pose's contact
                        // fades out, so the path across the belly is fully guarded.
                        float allow = Mathf.Max(Contact(mixPrev, i, local), Contact(mixBase, i, local));
                        if (mixShot != null && shotW > 0f) allow = Mathf.Max(allow, Contact(mixShot, i, local));
                        arm.frameAllow = allow;
                        desired *= Mathf.Max(need - allow, 0f) / need;
                    }
                    desired *= gain;
                }

                float rate = desired.sqrMagnitude > arm.offset.sqrMagnitude ? rateIn : rateOut;
                arm.offset = Vector3.Lerp(arm.offset, desired, 1f - Mathf.Exp(-Mathf.Max(rate, 0.01f) * dt));
                TrackEpisode(arm, gain);
                if (arm.offset.sqrMagnitude < 1e-6f) continue;

                SolveTwoBone(arm.upper, arm.lower, arm.hand, arm.hand.position + arm.offset);
            }
            ReportEpisode(gain);
        }

        private static Vector3 Largest(Vector3 a, Vector3 b) => a.sqrMagnitude >= b.sqrMagnitude ? a : b;

        /// <summary>World-space core segment and radius of a capsule collider (scale included).</summary>
        private static void CapsuleSegment(CapsuleCollider c, out Vector3 a, out Vector3 b, out float radius)
        {
            Transform t = c.transform;
            Vector3 s = t.lossyScale;
            s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            Vector3 axis;
            float axisScale, radScale;
            switch (c.direction)
            {
                case 0: axis = Vector3.right; axisScale = s.x; radScale = Mathf.Max(s.y, s.z); break;
                case 2: axis = Vector3.forward; axisScale = s.z; radScale = Mathf.Max(s.x, s.y); break;
                default: axis = Vector3.up; axisScale = s.y; radScale = Mathf.Max(s.x, s.z); break;
            }
            radius = c.radius * radScale;
            float half = Mathf.Max(c.height * axisScale * 0.5f - radius, 0f);
            Vector3 centre = t.TransformPoint(c.center);
            Vector3 dir = t.TransformDirection(axis).normalized;
            a = centre - dir * half;
            b = centre + dir * half;
        }

        private static CapsuleCollider FindHandCapsule(Transform hand)
        {
            if (hand == null) return null;
            CapsuleCollider own = hand.GetComponent<CapsuleCollider>();
            if (own != null) return own;
            foreach (Transform child in hand)   // direct children first: the finger chains may carry their own
            {
                CapsuleCollider c = child.GetComponent<CapsuleCollider>();
                if (c != null) return c;
            }
            return hand.GetComponentInChildren<CapsuleCollider>(true);
        }

        // Natural contact of each authored pose, per arm: how much push the
        // pose "needs" on its own (metres) and where it holds the wrist
        // (hips space, running mean) — learned while the clip plays unblended.
        private class ClipContact
        {
            public readonly float[] need = new float[2];
            public readonly Vector3[] rest = new Vector3[2];
            public readonly int[] samples = new int[2];
        }

        private readonly Dictionary<AnimationClip, ClipContact> contact = new Dictionary<AnimationClip, ClipContact>();

        private void Learn(AnimationClip clip, int arm, float need, Vector3 local)
        {
            if (!contact.TryGetValue(clip, out ClipContact c)) contact[clip] = c = new ClipContact();
            c.need[arm] = Mathf.Max(c.need[arm], need);
            int n = c.samples[arm] = Mathf.Min(c.samples[arm] + 1, 240);
            c.rest[arm] = n == 1 ? local : c.rest[arm] + (local - c.rest[arm]) / n;
        }

        /// <summary>
        /// The pose's own contact for this arm at this wrist position: full
        /// near where the pose holds the wrist, fading to 0 across
        /// <see cref="contactReach"/>. 0 for a clip not seen on its own yet (fully guarded).
        /// </summary>
        private float Contact(AnimationClip clip, int arm, Vector3 local)
        {
            if (clip == null || !contact.TryGetValue(clip, out ClipContact c) || c.samples[arm] == 0) return 0f;
            float near = Mathf.Max(Mathf.Min(contactReach.x, contactReach.y), 0f);
            float far = Mathf.Max(Mathf.Max(contactReach.x, contactReach.y), near + 0.001f);
            float d = Vector3.Distance(local, c.rest[arm]);
            return c.need[arm] * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(near, far, d)));
        }

        // ------------------------------------------------------------------ diagnostics

        private bool episodeOpen;
        private float episodeStart, episodeQuiet;
        private string episodeFrom;

        private void TrackEpisode(Arm arm, float gain)
        {
            if (gain <= 0f && !episodeOpen) return;
            arm.maxDepth = Mathf.Max(arm.maxDepth, arm.frameDepth);
            arm.maxFix = Mathf.Max(arm.maxFix, arm.offset.magnitude);
            arm.maxAllow = Mathf.Max(arm.maxAllow, arm.frameAllow);
        }

        /// <summary>One [TestFlow] line per blend: how deep each arm would have gone in, and how far it was moved out.</summary>
        private void ReportEpisode(float gain)
        {
            if (!logBlends || !Application.isPlaying) return;
            string loop = player != null && player.BaseLoopClip != null ? player.BaseLoopClip.name : "?";
            if (gain > 0f)
            {
                if (!episodeOpen)
                {
                    episodeOpen = true;
                    episodeStart = Time.time;
                    AnimationClip from = player != null ? player.BlendingFromClip : null;
                    string shotName = player != null ? player.ActiveOneShotName : null;
                    episodeFrom = from != null ? from.name : shotName != null ? $"{loop} ⇄ one-shot '{shotName}'" : loop;
                }
                episodeQuiet = 0f;
                return;
            }
            if (!episodeOpen) return;
            episodeQuiet += Time.deltaTime;
            if (episodeQuiet < 0.3f) return;   // let the correction release first
            episodeOpen = false;

            Arm l = arms[0], r = arms[1];
            float lDepth = l.maxDepth, lFix = l.maxFix, rDepth = r.maxDepth, rFix = r.maxFix, lAllow = l.maxAllow, rAllow = r.maxAllow;
            foreach (Arm a in arms) { a.maxDepth = 0f; a.maxFix = 0f; a.maxAllow = 0f; }
            if (lDepth < 0.005f && rDepth < 0.005f) return;   // nothing came near the body
            string cm(float m) => $"{m * 100f:0.0}";
            string shot = player != null ? player.ActiveOneShotName : null;
            TestFlowLog.Emit("hand_guard",
                $"[{name}] HANDS during blend '{episodeFrom}' → '{loop}'{(shot != null ? $" / one-shot '{shot}'" : "")}: "
                + $"left arm would sink {cm(lDepth)} cm (pose's own contact {cm(lAllow)}), moved out {cm(lFix)} cm; right arm would sink {cm(rDepth)} cm (own contact {cm(rAllow)}), moved out {cm(rFix)} cm.",
                this,
                "from", episodeFrom, "to", loop,
                "left_depth_cm", lDepth * 100f, "left_fix_cm", lFix * 100f,
                "right_depth_cm", rDepth * 100f, "right_fix_cm", rFix * 100f,
                "left_allow_cm", lAllow * 100f, "right_allow_cm", rAllow * 100f,
                "duration_s", Time.time - episodeStart);
        }

        private void LogFit(string source)
        {
            if (!logBlends || !Application.isPlaying) return;
            var widths = new List<float>();
            var depths = new List<float>();
            foreach (Section s in sections)
            {
                widths.Add(s != null ? s.halfWidth * 200f : 0f);
                depths.Add(s != null ? s.halfDepth * 200f : 0f);
            }
            string Hand(Arm a) => useHandColliders && a.shape != null ? $"capsule '{a.shape.name}'" : "wrist + palm";
            string hands = $"left {Hand(arms[0])}, right {Hand(arms[1])}";
            TestFlowLog.Emit("hand_guard_ready",
                $"[{name}] hand guard on — torso from {source}: {Describe()}. Hands: {hands}.",
                this, "source", source, "width_cm", widths, "depth_cm", depths, "hands", hands,
                "forearm_radius_cm", forearmRadius * 100f, "only_during_blends", onlyDuringBlends);
        }

        /// <summary>World-space push that moves a limb point of the given skin radius out of the torso (zero when it is clear).</summary>
        private Vector3 Push(Vector3 p, Arm arm, float radius)
        {
            Vector3 best = Vector3.zero;
            for (int s = 0; s < SectionCount - 1; s++)
            {
                Vector3 a = nodePos[s], b = nodePos[s + 1];
                Vector3 ab = b - a;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-6f) continue;
                float t = Vector3.Dot(p - a, ab) / len2;
                // Open ends: nothing below the pelvis or above the upper chest.
                if ((s == 0 && t < 0f) || (s == SectionCount - 2 && t > 1f)) continue;
                t = Mathf.Clamp01(t);

                Section sa = sections[s], sb = sections[s + 1];
                float scale = shapeScale;
                float hw = Mathf.Lerp(sa.halfWidth, sb.halfWidth, t) * scale;
                float hd = Mathf.Lerp(sa.halfDepth, sb.halfDepth, t) * scale;
                if (hw < 0.01f || hd < 0.01f) continue;
                Vector3 right = Vector3.Slerp(nodeRight[s], nodeRight[s + 1], t);
                Vector3 fwd = Vector3.Slerp(nodeFwd[s], nodeFwd[s + 1], t);
                Vector3 centre = a + ab * t + fwd * Mathf.Lerp(sa.forwardOffset, sb.forwardOffset, t);

                Vector3 d = p - centre;
                float x = Vector3.Dot(d, right), z = Vector3.Dot(d, fwd);
                float qx = x / hw, qz = z / hd;
                float r = Mathf.Sqrt(qx * qx + qz * qz);

                // Normalised distances: the torso skin is r = 1; the limb's skin
                // reaches radius/unit further in than its bone.
                float unit = 0.5f * (hw + hd);
                float skin = Mathf.Max(radius, 0f) / unit;
                arm.frameDepth = Mathf.Max(arm.frameDepth, (1f + skin - r) * unit);   // diagnostics: real overlap, metres
                float c = (clearance + Mathf.Max(radius, 0f)) / unit, m = Mathf.Max(softZone / unit, 0.001f);
                float depth = (1f + c + m) - r;   // how far into the soft zone
                if (depth <= 0f) continue;
                // C1 ease: quadratic in the soft zone, then linear — deep points land at 1 + c.
                float push = depth < 2f * m ? depth * depth / (4f * m) : depth - m;

                Vector2 dir;
                if (r > 1e-3f) dir = new Vector2(qx / r, qz / r);
                else if (arm.lastDir.sqrMagnitude > 0f) dir = new Vector2(arm.lastDir.x, arm.lastDir.z);
                else dir = new Vector2(arm.side * 0.6f, 0.8f);      // on the axis: out to the arm's side and front
                arm.lastDir = new Vector3(dir.x, 0f, dir.y);

                float nx = (dir.x * (r + push) - qx) * hw;
                float nz = (dir.y * (r + push) - qz) * hd;
                Vector3 world = right * nx + fwd * nz;
                if (world.sqrMagnitude > best.sqrMagnitude) best = world;
            }
            return best;
        }

        /// <summary>Node centres and axes of the spine chain for this frame's pose.</summary>
        private void BuildFrame()
        {
            Vector3 hipMid = legL != null && legR != null ? (legL.position + legR.position) * 0.5f : hips.position;
            Vector3 up0 = (spine.position - hips.position);
            Vector3 lowerRight = legL != null && legR != null ? (legR.position - legL.position) : animator.transform.right;
            Vector3 upperRight = armL != null && armR != null ? (armR.position - armL.position) : lowerRight;

            // Pelvis bottom: below the hip joints by a third of the hips→spine distance.
            nodePos[0] = hipMid - up0.normalized * (up0.magnitude * 0.35f);
            nodePos[1] = hips.position;
            nodePos[2] = spine.position;
            nodePos[3] = chest != null ? chest.position : Vector3.Lerp(spine.position, neck.position, 0.5f);
            nodePos[4] = upperChest != null && upperChest != chest
                ? Vector3.Lerp(upperChest.position, neck.position, 0.35f)
                : Vector3.Lerp(nodePos[3], neck.position, 0.6f);

            for (int i = 0; i < SectionCount; i++)
            {
                Vector3 up = i < SectionCount - 1 ? nodePos[i + 1] - nodePos[i] : neck.position - nodePos[i];
                if (up.sqrMagnitude < 1e-8f) up = animator.transform.up;
                up.Normalize();
                Vector3 right = i <= 1 ? lowerRight : i >= 3 ? upperRight : Vector3.Lerp(lowerRight.normalized, upperRight.normalized, 0.5f);
                right = Vector3.ProjectOnPlane(right, up);
                if (right.sqrMagnitude < 1e-8f) right = Vector3.ProjectOnPlane(animator.transform.right, up);
                right.Normalize();
                nodeUp[i] = up;
                nodeRight[i] = right;
                nodeFwd[i] = Vector3.Cross(right, up);   // Unity: right × up = forward
            }
        }

        // ------------------------------------------------------------------ IK

        /// <summary>
        /// Analytic two-bone IK: bends the elbow to the target distance, then
        /// swings the arm onto the target. Keeps the elbow's current bend plane
        /// and the hand's world rotation.
        /// </summary>
        private static void SolveTwoBone(Transform upper, Transform lower, Transform end, Vector3 target)
        {
            Quaternion handRotation = end.rotation;
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float lab = Vector3.Distance(a, b), lcb = Vector3.Distance(b, c);
            if (lab < 1e-4f || lcb < 1e-4f) return;
            float lat = Mathf.Clamp(Vector3.Distance(a, target), 0.01f, (lab + lcb) * 0.999f);

            Vector3 ac = c - a, ab = b - a;
            float acab0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac.normalized, ab.normalized), -1f, 1f));
            float babc0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((a - b).normalized, (c - b).normalized), -1f, 1f));
            float acab1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float babc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

            Vector3 axis = Vector3.Cross(ac, ab);
            if (axis.sqrMagnitude < 1e-10f) axis = Vector3.Cross(ac, upper.forward);   // straight arm: any plane
            if (axis.sqrMagnitude < 1e-10f) return;
            axis.Normalize();

            upper.rotation = Quaternion.AngleAxis((acab1 - acab0) * Mathf.Rad2Deg, axis) * upper.rotation;
            lower.rotation = Quaternion.AngleAxis((babc1 - babc0) * Mathf.Rad2Deg, axis) * lower.rotation;

            Vector3 reached = end.position - a;
            Vector3 wanted = target - a;
            if (reached.sqrMagnitude > 1e-8f && wanted.sqrMagnitude > 1e-8f)
                upper.rotation = Quaternion.FromToRotation(reached, wanted) * upper.rotation;

            end.rotation = handRotation;
        }

        // ------------------------------------------------------------------ setup

        private bool Resolve()
        {
            if (player == null) player = GetComponent<StudentAmbientPlayer>();
            if (player == null) player = GetComponentInParent<StudentAmbientPlayer>();
            if (animator == null) animator = GetComponent<Animator>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman || animator.avatar == null) return false;

            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            upperChest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            if (neck == null) neck = animator.GetBoneTransform(HumanBodyBones.Head);
            legL = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            legR = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            armL = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            armR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (hips == null || spine == null || neck == null) return false;

            arms[0].upper = armL;
            arms[0].lower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            arms[0].hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            arms[0].knuckle = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            arms[1].upper = armR;
            arms[1].lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            arms[1].hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            arms[1].knuckle = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            foreach (Arm a in arms) a.shape = FindHandCapsule(a.hand);

            OnValidate();
            // Placeholder until the mesh fit runs; with Fit From Mesh off the authored sections stay.
            if (fitFromMesh && !fitted) DefaultSections();

            if (player != null)
            {
                player.PoseSampled -= OnPoseSampled;
                if (isActiveAndEnabled) player.PoseSampled += OnPoseSampled;
            }
            return true;
        }

        /// <summary>Rough proportions from the skeleton, used until (or when) the mesh fit is not available.</summary>
        private void DefaultSections()
        {
            float shoulders = armL != null && armR != null ? Vector3.Distance(armL.position, armR.position) : 0.34f;
            float legs = legL != null && legR != null ? Vector3.Distance(legL.position, legR.position) : 0.18f;
            Set(0, legs * 0.95f, legs * 0.65f, 0.00f);
            Set(1, legs * 1.00f, legs * 0.70f, 0.01f);
            Set(2, shoulders * 0.40f, shoulders * 0.30f, 0.02f);
            Set(3, shoulders * 0.45f, shoulders * 0.34f, 0.03f);
            Set(4, shoulders * 0.45f, shoulders * 0.32f, 0.02f);
        }

        private void Set(int i, float hw, float hd, float fwd)
        {
            if (sections[i] == null) sections[i] = new Section();
            sections[i].halfWidth = hw;
            sections[i].halfDepth = hd;
            sections[i].forwardOffset = fwd;
        }

        /// <summary>
        /// Measures each section from the body mesh as it is posed now: the
        /// vertices skinned mostly to the spine chain (not arms, legs, head)
        /// within a slab around the section's height.
        /// </summary>
        private string fitNotes;   // why the mesh fit failed, for the log

        private bool FitFromMesh()
        {
            var candidates = new List<SkinnedMeshRenderer>();
            if (bodyMesh != null) candidates.Add(bodyMesh);
            else
            {
                var seen = new HashSet<SkinnedMeshRenderer>();
                foreach (SkinnedMeshRenderer c in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (seen.Add(c)) candidates.Add(c);
                foreach (SkinnedMeshRenderer c in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (seen.Add(c)) candidates.Add(c);
                candidates.Sort((a, b) =>
                    (b.sharedMesh != null ? b.sharedMesh.vertexCount : 0).CompareTo(a.sharedMesh != null ? a.sharedMesh.vertexCount : 0));
            }

            var notes = new List<string>();
            foreach (SkinnedMeshRenderer smr in candidates)
            {
                if (TryFit(smr, notes)) { fitNotes = null; return true; }
                if (notes.Count >= 4) break;   // the biggest meshes are the ones that matter
            }
            fitNotes = candidates.Count == 0 ? "no SkinnedMeshRenderer under the character" : string.Join("; ", notes);
            DefaultSections();
            return false;
        }

        private bool TryFit(SkinnedMeshRenderer smr, List<string> notes)
        {
            Mesh shared = smr != null ? smr.sharedMesh : null;
            if (shared == null) return false;
            string label = $"'{smr.name}' ({shared.vertexCount} verts)";
            if (!shared.isReadable) { notes.Add($"{label}: mesh not readable (Read/Write off in the model's import settings)"); return false; }

            Transform[] bones = smr.bones;
            BoneWeight[] weights = shared.boneWeights;
            if (bones == null || bones.Length == 0 || weights == null || weights.Length != shared.vertexCount)
            {
                notes.Add($"{label}: no usable bone weights ({(bones != null ? bones.Length : 0)} bones, {(weights != null ? weights.Length : 0)} weights)");
                return false;
            }

            // Which skin bones belong to the torso: under Hips, not under a leg, arm or the neck.
            var excluded = new List<Transform>();
            foreach (HumanBodyBones hb in new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
                         HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, HumanBodyBones.LeftUpperArm,
                         HumanBodyBones.RightUpperArm, HumanBodyBones.Neck, HumanBodyBones.Head })
            {
                Transform t = animator.GetBoneTransform(hb);
                if (t != null) excluded.Add(t);
            }
            var torsoBone = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                Transform t = bones[i];
                if (t == null || !(t == hips || t.IsChildOf(hips))) continue;
                bool limb = false;
                foreach (Transform ex in excluded)
                    if (t == ex || t.IsChildOf(ex)) { limb = true; break; }
                torsoBone[i] = !limb;
            }

            int torsoBones = 0;
            foreach (bool b in torsoBone) if (b) torsoBones++;
            var torsoVerts = new List<int>();
            for (int v = 0; v < weights.Length; v++)
            {
                BoneWeight w = weights[v];
                int dominant = w.boneIndex0;
                float best = w.weight0;
                if (w.weight1 > best) { best = w.weight1; dominant = w.boneIndex1; }
                if (w.weight2 > best) { best = w.weight2; dominant = w.boneIndex2; }
                if (w.weight3 > best) { dominant = w.boneIndex3; }
                if (dominant >= 0 && dominant < torsoBone.Length && torsoBone[dominant]) torsoVerts.Add(v);
            }
            if (torsoVerts.Count < 100)
            {
                notes.Add($"{label}: only {torsoVerts.Count} vertices skinned to the spine chain ({torsoBones} of {bones.Length} bones count as torso)");
                return false;
            }

            BuildFrame();
            float slab = Mathf.Max(Vector3.Distance(nodePos[1], nodePos[2]) * 0.3f, 0.025f);

            // BakeMesh's handling of the renderer's scale differs between
            // setups — try both and keep the one that lands on the skeleton.
            List<float>[] bestX = null, bestZ = null;
            int bestHits = -1;
            string tried = "";
            for (int mode = 0; mode < 2; mode++)
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, mode == 1);
                Vector3[] verts = baked.vertices;
                DestroyImmediate(baked);
                Matrix4x4 toWorld = mode == 0
                    ? smr.transform.localToWorldMatrix
                    : Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);

                var xs = new List<float>[SectionCount];
                var zs = new List<float>[SectionCount];
                for (int i = 0; i < SectionCount; i++) { xs[i] = new List<float>(); zs[i] = new List<float>(); }
                foreach (int v in torsoVerts)
                {
                    if (v >= verts.Length) continue;
                    Vector3 p = toWorld.MultiplyPoint3x4(verts[v]);
                    for (int n = 0; n < SectionCount; n++)
                    {
                        Vector3 d = p - nodePos[n];
                        if (Mathf.Abs(Vector3.Dot(d, nodeUp[n])) > slab) continue;
                        float x = Vector3.Dot(d, nodeRight[n]), z = Vector3.Dot(d, nodeFwd[n]);
                        if (x * x + z * z > 0.5f * 0.5f) continue;   // > 50 cm from the spine: not this slab's torso
                        xs[n].Add(x);
                        zs[n].Add(z);
                    }
                }
                int hits = 0;
                var counts = new List<int>();
                for (int n = 0; n < SectionCount; n++) { counts.Add(xs[n].Count); if (xs[n].Count >= 24) hits++; }
                tried += $"{(mode == 0 ? "unscaled" : "scaled")} bake → slab vertices [{string.Join(", ", counts)}] ";
                if (hits > bestHits) { bestHits = hits; bestX = xs; bestZ = zs; }
            }

            if (bestHits < 3)
            {
                notes.Add($"{label}: {torsoVerts.Count} torso vertices but they don't line up with the spine — {tried.Trim()}");
                return false;
            }

            DefaultSections();
            for (int n = 0; n < SectionCount; n++)
            {
                if (bestX[n].Count < 24) continue;   // too few vertices: keep the skeleton estimate
                bestX[n].Sort();
                bestZ[n].Sort();
                float x0 = Percentile(bestX[n], 0.02f), x1 = Percentile(bestX[n], 0.98f);
                float z0 = Percentile(bestZ[n], 0.02f), z1 = Percentile(bestZ[n], 0.98f);
                Set(n, Mathf.Max(Mathf.Abs(x0), Mathf.Abs(x1)), (z1 - z0) * 0.5f, (z0 + z1) * 0.5f);
            }
            fittedMeshName = smr.name;
            return true;
        }

        private string fittedMeshName;

        private static float Percentile(List<float> sorted, float q)
        {
            int i = Mathf.Clamp(Mathf.RoundToInt(q * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }

        private string Describe()
        {
            var parts = new List<string>();
            string[] names = { "pelvis", "hips", "waist", "chest", "upper chest" };
            for (int i = 0; i < SectionCount; i++)
                parts.Add($"{names[i]} {sections[i].halfWidth * 200f:0}×{sections[i].halfDepth * 200f:0} cm (+{sections[i].forwardOffset * 100f:0.#} fwd)");
            return string.Join(", ", parts);
        }

        // ------------------------------------------------------------------ gizmos

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;
            if (!ready && !(ready = Resolve())) return;
            BuildFrame();
            for (int n = 0; n < SectionCount; n++)
            {
                Section s = sections[n];
                if (s == null) continue;
                Vector3 centre = nodePos[n] + nodeFwd[n] * s.forwardOffset;
                Gizmos.color = new Color(0.45f, 0.3f, 0.85f, 0.9f);
                DrawEllipse(centre, nodeRight[n], nodeFwd[n], s.halfWidth * shapeScale, s.halfDepth * shapeScale);
                Gizmos.color = new Color(0.45f, 0.3f, 0.85f, 0.3f);
                DrawEllipse(centre, nodeRight[n], nodeFwd[n], s.halfWidth * shapeScale + softZone, s.halfDepth * shapeScale + softZone);
            }
            foreach (Arm arm in arms)
            {
                if (arm.hand == null) continue;
                Gizmos.color = arm.offset.sqrMagnitude > 1e-6f ? Color.red : Color.green;
                Gizmos.DrawWireSphere(arm.hand.position, 0.02f);
                if (arm.offset.sqrMagnitude > 1e-6f) Gizmos.DrawLine(arm.hand.position - arm.offset, arm.hand.position);
                if (useHandColliders && arm.shape != null)
                {
                    CapsuleSegment(arm.shape, out Vector3 c0, out Vector3 c1, out float cr);
                    for (int k = 0; k <= 3; k++) Gizmos.DrawWireSphere(Vector3.Lerp(c0, c1, k / 3f), cr);
                }
            }
        }

        private static void DrawEllipse(Vector3 c, Vector3 right, Vector3 fwd, float a, float b)
        {
            const int steps = 32;
            Vector3 prev = c + right * a;
            for (int i = 1; i <= steps; i++)
            {
                float ang = i * Mathf.PI * 2f / steps;
                Vector3 p = c + right * (Mathf.Cos(ang) * a) + fwd * (Mathf.Sin(ang) * b);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
#endif
    }
}
