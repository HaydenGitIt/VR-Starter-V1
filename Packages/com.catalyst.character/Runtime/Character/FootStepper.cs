using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Keeps the feet from sliding while two body poses crossfade. A
    /// crossfade blends joint rotations, so a foot that stands in a different
    /// spot in the two loops glides across the floor between them. While
    /// <see cref="StudentAmbientPlayer.PoseBlendActivity"/> is up, both feet
    /// are planted where they stood; when the blended pose has pulled a foot
    /// far enough away (or turned it), that foot takes a short arced step to
    /// where the pose wants it — one foot at a time, the one that is further
    /// off first. After the blend, a foot still off by more than a couple of
    /// centimetres takes a final small step, then the IK lets go smoothly.
    ///
    /// Runs in LateUpdate at -11: after ProceduralIdleMotion (-12, which puts
    /// the feet back where the clip has them after its pelvis sway), before
    /// gaze (-10), the hand guard (-9) and the Reallusion BoneDriver (0).
    /// Added at runtime by StudentAmbientPlayer (Step Feet During Blends).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-11)]
    public class FootStepper : MonoBehaviour
    {
        [Tooltip("Overall strength. 0 = feet slide as the crossfade blends them.")]
        [Range(0f, 1f)]
        public float weight = 1f;

        [Header("When to step")]
        [Tooltip("During a blend, a planted foot steps once the pose wants it this far away (metres, along the floor).")]
        public float stepDistance = 0.06f;

        [Tooltip("…or turned this much (degrees, about the vertical).")]
        public float stepTurnDegrees = 15f;

        [Tooltip("After the blend, a foot still this far off (metres) takes one last small step; closer than that it just eases in.")]
        public float settleDistance = 0.025f;

        [Header("The step")]
        [Tooltip("Seconds one step takes (lift, move, put down).")]
        public float stepSeconds = 0.42f;

        [Tooltip("How high the foot lifts, metres (less for short steps).")]
        public float stepHeight = 0.05f;

        [Tooltip("Pause between one foot landing and the other lifting, seconds.")]
        public float stepGap = 0.08f;

        [Tooltip("Seconds for the IK to hand the feet back to the animation once nothing is left to step.")]
        public float releaseSeconds = 0.3f;

        [Header("Debug")]
        [Tooltip("Write a [TestFlow] line after every blend: how many steps each foot took and how far.")]
        public bool logSteps = true;

        private class Foot
        {
            public Transform upper, lower, foot;
            public bool planted;          // IK holds it (planted or stepping)
            public Vector3 pos;           // planted / current position, character-root space
            public Quaternion rot;        // planted / current rotation, character-root space
            public bool stepping;
            public Vector3 fromPos;
            public Quaternion fromRot;
            public float t;               // 0..1 through the step
            public float stepLength;
            public int steps;             // this episode, for the log
            public float travelled;
        }

        private readonly Foot left = new Foot(), right = new Foot();
        private Animator animator;
        private StudentAmbientPlayer player;
        private Transform root;
        private bool ready;
        private float ikWeight;           // 0..1, eased in/out
        private float gapLeft;
        private bool episode;
        private string episodeFrom, episodeTo;

        private void OnEnable()
        {
            ready = Resolve();
            left.planted = right.planted = false;
            ikWeight = 0f;
        }

        private bool Resolve()
        {
            if (player == null) player = GetComponent<StudentAmbientPlayer>();
            if (player == null) player = GetComponentInParent<StudentAmbientPlayer>();
            if (animator == null) animator = GetComponent<Animator>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman || animator.avatar == null || player == null) return false;
            root = animator.transform;
            left.upper = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            left.lower = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            left.foot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            right.upper = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            right.lower = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            right.foot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            return left.foot != null && right.foot != null && left.upper != null && right.upper != null
                && left.lower != null && right.lower != null;
        }

        private void LateUpdate()
        {
            if (!ready && !(ready = Resolve())) return;
            float dt = Time.deltaTime;
            bool blending = weight > 0f && player.isActiveAndEnabled && !player.Suspended && player.PoseBlendActivity > 0f;

            // The pose's own feet this frame (character-root space).
            Vector3 targetL = root.InverseTransformPoint(left.foot.position), targetR = root.InverseTransformPoint(right.foot.position);
            Quaternion targetRotL = Quaternion.Inverse(root.rotation) * left.foot.rotation;
            Quaternion targetRotR = Quaternion.Inverse(root.rotation) * right.foot.rotation;

            if (blending && !left.planted)
            {
                // A blend starts: plant both feet exactly where they stand now (no pop).
                Plant(left, targetL, targetRotL);
                Plant(right, targetR, targetRotR);
                StartEpisode();
            }
            if (!left.planted)
            {
                ikWeight = 0f;
                return;
            }

            gapLeft -= dt;
            float trigger = blending ? stepDistance : settleDistance;
            float turnTrigger = blending ? stepTurnDegrees : stepTurnDegrees * 0.5f;
            if (!left.stepping && !right.stepping && gapLeft <= 0f)
            {
                float offL = Off(left, targetL, targetRotL, trigger, turnTrigger);
                float offR = Off(right, targetR, targetRotR, trigger, turnTrigger);
                if (offL > 0f || offR > 0f) BeginStep(offL >= offR ? left : right, offL >= offR ? targetL : targetR);
            }
            Advance(left, targetL, targetRotL, dt);
            Advance(right, targetR, targetRotR, dt);

            // Done: blend over and nothing left to step — ease back to the animation.
            bool idle = !blending && !left.stepping && !right.stepping && gapLeft <= 0f
                && Off(left, targetL, targetRotL, settleDistance, stepTurnDegrees * 0.5f) <= 0f
                && Off(right, targetR, targetRotR, settleDistance, stepTurnDegrees * 0.5f) <= 0f;
            float rate = idle ? -dt / Mathf.Max(releaseSeconds, 0.01f) : dt / 0.1f;
            ikWeight = Mathf.Clamp01(ikWeight + rate);
            if (idle && ikWeight <= 0f)
            {
                left.planted = right.planted = false;
                EndEpisode();
                return;
            }

            float w = Mathf.SmoothStep(0f, 1f, ikWeight) * Mathf.Clamp01(weight);
            Apply(left, targetL, targetRotL, w);
            Apply(right, targetR, targetRotR, w);
        }

        private static void Plant(Foot f, Vector3 pos, Quaternion rot)
        {
            f.planted = true;
            f.stepping = false;
            f.pos = pos;
            f.rot = rot;
        }

        /// <summary>How far past the trigger this foot is (along the floor; turn counts as distance), 0 when within it.</summary>
        private float Off(Foot f, Vector3 target, Quaternion targetRot, float trigger, float turnTrigger)
        {
            if (f.stepping) return 0f;
            Vector3 d = target - f.pos;
            d.y = 0f;
            float dist = d.magnitude;
            float turn = Mathf.Abs(YawDelta(targetRot, f.rot));
            float over = Mathf.Max(dist - trigger, 0f);
            if (turn > turnTrigger) over = Mathf.Max(over, 0.001f + (turn - turnTrigger) * 0.002f);
            return over;
        }

        /// <summary>Signed turn about the vertical (character-root space) that takes rotation <paramref name="from"/> to <paramref name="to"/>, degrees.</summary>
        private static float YawDelta(Quaternion to, Quaternion from)
        {
            Vector3 v = (to * Quaternion.Inverse(from)) * Vector3.forward;
            v.y = 0f;
            return v.sqrMagnitude < 1e-6f ? 0f : Vector3.SignedAngle(Vector3.forward, v, Vector3.up);
        }

        /// <summary>The pose's foot rotation with its turn (relative to <paramref name="held"/>) taken out by <paramref name="amount"/> — tilt and roll still follow the animation.</summary>
        private static Quaternion HoldHeading(Quaternion target, Quaternion held, float amount) =>
            Quaternion.AngleAxis(-YawDelta(target, held) * amount, Vector3.up) * target;

        private void BeginStep(Foot f, Vector3 target)
        {
            f.stepping = true;
            f.t = 0f;
            f.fromPos = f.pos;
            f.fromRot = f.rot;
            Vector3 d = target - f.pos;
            d.y = 0f;
            f.stepLength = d.magnitude;
        }

        private void Advance(Foot f, Vector3 target, Quaternion targetRot, float dt)
        {
            if (!f.stepping) return;
            f.t = Mathf.Min(f.t + dt / Mathf.Max(stepSeconds, 0.05f), 1f);
            if (f.t < 1f) return;
            f.stepping = false;
            f.pos = target;
            f.rot = targetRot;
            f.steps++;
            f.travelled += f.stepLength;
            gapLeft = stepGap;
        }

        /// <summary>
        /// Where the IK puts the foot: planted = its floor spot and heading
        /// held (height, tilt and roll still from the animation, so a tapping
        /// foot still lands); stepping = an arc from the old spot to the
        /// pose's spot, the landing following the pose while in the air.
        /// </summary>
        private void Apply(Foot f, Vector3 target, Quaternion targetRot, float w)
        {
            if (w <= 0f) return;
            Vector3 held;
            Quaternion heldRot;
            if (f.stepping)
            {
                float e = Mathf.SmoothStep(0f, 1f, f.t);
                held = Vector3.Lerp(f.fromPos, target, e);
                float lift = Mathf.Min(stepHeight, 0.015f + f.stepLength * 0.4f);
                held.y = target.y + Mathf.Sin(f.t * Mathf.PI) * lift;
                heldRot = HoldHeading(targetRot, f.fromRot, 1f - e);
            }
            else
            {
                held = new Vector3(f.pos.x, target.y, f.pos.z);
                heldRot = HoldHeading(targetRot, f.rot, 1f);
            }
            Vector3 pos = Vector3.Lerp(target, held, w);
            Quaternion rot = Quaternion.Slerp(targetRot, heldRot, w);
            SolveLeg(f.upper, f.lower, f.foot, root.TransformPoint(pos), root.rotation * rot);
        }

        /// <summary>Analytic two-bone IK; the knee bends in its current plane (forward when straight), then the foot takes the rotation.</summary>
        private void SolveLeg(Transform upper, Transform lower, Transform foot, Vector3 targetPos, Quaternion targetRot)
        {
            Vector3 a = upper.position, b = lower.position, c = foot.position;
            float lenAB = (b - a).magnitude, lenBC = (c - b).magnitude;
            if (lenAB < 1e-5f || lenBC < 1e-5f) return;

            Vector3 toTarget = targetPos - a;
            float reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(lenAB - lenBC) + 1e-4f, lenAB + lenBC - 1e-4f);
            float wanted = Mathf.Acos(Mathf.Clamp((lenAB * lenAB + lenBC * lenBC - reach * reach) / (2f * lenAB * lenBC), -1f, 1f)) * Mathf.Rad2Deg;
            float current = Vector3.Angle(a - b, c - b);
            Vector3 axis = Vector3.Cross(b - a, c - b);
            if (axis.sqrMagnitude < 1e-8f) axis = -root.right;
            axis.Normalize();
            if (Mathf.Abs(current - wanted) > 0.01f)
            {
                Quaternion before = lower.rotation;
                lower.rotation = Quaternion.AngleAxis(current - wanted, axis) * before;
                if (Mathf.Abs((foot.position - a).magnitude - reach) > 1e-3f)
                    lower.rotation = Quaternion.AngleAxis(wanted - current, axis) * before;
            }
            Vector3 now = foot.position - a;
            if (now.sqrMagnitude > 1e-10f && toTarget.sqrMagnitude > 1e-10f)
                upper.rotation = Quaternion.FromToRotation(now, toTarget) * upper.rotation;
            foot.rotation = targetRot;
        }

        // ------------------------------------------------------------------ log

        private void StartEpisode()
        {
            episode = true;
            left.steps = right.steps = 0;
            left.travelled = right.travelled = 0f;
            AnimationClip from = player.BlendingFromClip;
            AnimationClip to = player.BaseLoopClip;
            string shot = player.ActiveOneShotName;
            episodeFrom = from != null ? from.name : shot != null ? $"one-shot '{shot}'" : to != null ? to.name : "?";
            episodeTo = to != null ? to.name : "?";
        }

        private void EndEpisode()
        {
            if (!episode) return;
            episode = false;
            if (!logSteps || !Application.isPlaying || left.steps + right.steps == 0) return;
            TestFlowLog.Emit("foot_steps",
                $"[{name}] FEET during blend '{episodeFrom}' → '{episodeTo}': left {left.steps} step(s) ({left.travelled * 100f:0} cm), right {right.steps} step(s) ({right.travelled * 100f:0} cm) instead of sliding.",
                this, "from", episodeFrom, "to", episodeTo,
                "left_steps", left.steps, "left_cm", left.travelled * 100f,
                "right_steps", right.steps, "right_cm", right.travelled * 100f);
        }
    }
}
