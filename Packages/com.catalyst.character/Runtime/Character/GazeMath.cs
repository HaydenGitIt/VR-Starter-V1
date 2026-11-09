using System.Collections.Generic;
using Reallusion.Runtime;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Stateless gaze helpers for the student gaze system — the same math
    /// <see cref="GazeDriver"/> uses on the hero (kept private there so the
    /// tuned hero path stays untouched): exponential smoothing, yaw/pitch
    /// measurement in an arbitrary frame, the world-axis additive head
    /// offset, and the head-to-gaze frame derivation.
    /// </summary>
    public static class GazeMath
    {
        /// <summary>Frame-rate independent smoothing factor: 1 - exp(-rate*dt).</summary>
        public static float SmoothingStep(float rate, float dt)
        {
            return 1f - Mathf.Exp(-rate * dt);
        }

        /// <summary>
        /// Yaw/pitch (right/down positive, degrees, clamped to ±maxYaw/±maxPitch,
        /// the conversational ±45/±30 by default) from a gaze frame at
        /// <paramref name="from"/> to the target position. A scripted cue at a
        /// neighbour sits well past 45°, so those callers widen the clamp.
        /// </summary>
        public static Vector2 AnglesToTarget(Quaternion frame, Vector3 from, Vector3 targetPosition,
            float maxYaw = 45f, float maxPitch = 30f)
        {
            Vector3 local = Quaternion.Inverse(frame) * (targetPosition - from).normalized;
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            return new Vector2(Mathf.Clamp(yaw, -maxYaw, maxYaw), Mathf.Clamp(pitch, -maxPitch, maxPitch));
        }

        /// <summary>
        /// Applies yaw/pitch (right/down positive, degrees) as a world-axis
        /// offset on top of the bone's current (animated) rotation —
        /// independent of the CC bone's internal axes.
        /// </summary>
        public static void ApplyWorldAxisOffset(Transform bone, Transform root, Vector2 yawPitch)
        {
            bone.rotation =
                Quaternion.AngleAxis(yawPitch.x, root.up) *
                Quaternion.AngleAxis(yawPitch.y, root.right) *
                bone.rotation;
        }

        /// <summary>
        /// Same offset about explicit axes — world up for yaw and the
        /// animated head frame's right for pitch — so the turn is correct
        /// whatever the character root's own orientation is.
        /// </summary>
        public static void ApplyFrameOffset(Transform bone, Vector3 up, Vector3 right, Vector2 yawPitch)
        {
            bone.rotation =
                Quaternion.AngleAxis(yawPitch.x, up) *
                Quaternion.AngleAxis(yawPitch.y, right) *
                bone.rotation;
        }

        /// <summary>
        /// Rotation from the head bone's frame to the "eyes forward" frame.
        /// Derived from the body mesh's bind pose, where CC characters face
        /// mesh forward — pose-independent, unlike a live capture, which is
        /// skewed when the scene was saved with a previewed pose applied.
        /// Falls back to a live capture against the root when the head bone
        /// is not part of the mesh's skeleton.
        /// </summary>
        public static Quaternion HeadToGazeFrame(SkinnedMeshRenderer bodyMesh, Transform headBone, Transform root)
        {
            if (bodyMesh != null && bodyMesh.sharedMesh != null && headBone != null)
            {
                Transform[] bones = bodyMesh.bones;
                Matrix4x4[] binds = bodyMesh.sharedMesh.bindposes;
                for (int i = 0; i < bones.Length && i < binds.Length; i++)
                {
                    if (bones[i] != headBone) continue;
                    // bindposes[i] is the inverse of the bone's bind matrix in
                    // mesh space, so its inverse rotation is the head's bind
                    // rotation relative to the forward-facing mesh root.
                    return Quaternion.Inverse(binds[i].inverse.rotation);
                }
            }
            return headBone != null && root != null
                ? Quaternion.Inverse(headBone.rotation) * root.rotation
                : Quaternion.identity;
        }
    }

    /// <summary>
    /// How far each CC eye look shape ACTUALLY rotates its eye bone at weight
    /// 100, in degrees, read from the character's own BoneDriver.
    ///
    /// This has to come from the rig rather than a constant: BoneDriver applies
    /// a per-shape rotation baked into each character at import
    /// (localRotation = ref * Lerp(identity, shapeRotation, weight/100)), and
    /// those bakes are neither the same across characters (measured here:
    /// 30-40° of yaw, 14-35° of pitch), nor left/right symmetric, nor equal
    /// between the two eyes. Assuming a single shared figure made the gaze
    /// overshoot in one direction and undershoot in the other, so the eyes
    /// landed beside the target and disagreed with each other by a few degrees.
    /// </summary>
    public struct EyeLookRanges
    {
        public float leftYawLeft, leftYawRight, leftPitchUp, leftPitchDown;
        public float rightYawLeft, rightYawRight, rightPitchUp, rightPitchDown;

        /// <summary>True when all eight shapes were found on a BoneDriver.</summary>
        public bool resolved;

        /// <summary>
        /// Fallback for when no BoneDriver data is available. These are the
        /// figures this code assumed before the ranges were read from the rig —
        /// close for some characters, well off for others.
        /// </summary>
        public static EyeLookRanges Nominal => new EyeLookRanges
        {
            leftYawLeft = 30f, leftYawRight = 30f, leftPitchUp = 20f, leftPitchDown = 25f,
            rightYawLeft = 30f, rightYawRight = 30f, rightPitchUp = 20f, rightPitchDown = 25f,
            resolved = false,
        };

        /// <summary>
        /// Reads the true ranges off the character's BoneDriver, falling back
        /// to <see cref="Nominal"/> for any shape it cannot find so a character
        /// without a fully built BoneDriver still behaves sanely.
        /// </summary>
        public static EyeLookRanges Resolve(Transform root, Object context = null)
        {
            EyeLookRanges r = Nominal;
            if (root == null) return r;

            BoneDriver driver = root.GetComponentInChildren<BoneDriver>(true);
            if (driver == null || driver.glossary == null || driver.glossary.ExpressionsByBone == null)
                return r;

            int found = 0;
            foreach (BoneDriver.ExpressionByBone bone in driver.glossary.ExpressionsByBone)
            {
                if (bone == null || bone.Expressions == null) continue;
                foreach (BoneDriver.Expression e in bone.Expressions)
                {
                    if (e == null || e.isConstraint) continue;
                    float deg = ShapeDegrees(e);
                    if (deg <= 0.01f) continue;

                    switch (e.ExpressionName)
                    {
                        case "Eye_L_Look_L": r.leftYawLeft = deg; found++; break;
                        case "Eye_L_Look_R": r.leftYawRight = deg; found++; break;
                        case "Eye_L_Look_Up": r.leftPitchUp = deg; found++; break;
                        case "Eye_L_Look_Down": r.leftPitchDown = deg; found++; break;
                        case "Eye_R_Look_L": r.rightYawLeft = deg; found++; break;
                        case "Eye_R_Look_R": r.rightYawRight = deg; found++; break;
                        case "Eye_R_Look_Up": r.rightPitchUp = deg; found++; break;
                        case "Eye_R_Look_Down": r.rightPitchDown = deg; found++; break;
                    }
                }
            }

            r.resolved = found == 8;
            if (!r.resolved && context != null)
                Debug.LogWarning($"EyeLookRanges: found {found}/8 eye look shapes on the BoneDriver — " +
                                 "the rest fall back to nominal ranges and will aim slightly off.", context);
            return r;
        }

        /// <summary>
        /// Converts one eye's desired yaw/pitch (right/down positive, degrees)
        /// into its four look-shape weights, inverting the rotation that eye's
        /// shapes actually deliver. Shared by the student and hero gaze paths.
        /// </summary>
        public static void ShapeWeights(Vector2 anglesDeg, float weight,
            float yawLeftRange, float yawRightRange, float pitchUpRange, float pitchDownRange,
            out float wLeft, out float wRight, out float wUp, out float wDown)
        {
            // A zero range would divide by zero; nominal is wrong but finite.
            if (yawLeftRange <= 0f) yawLeftRange = 30f;
            if (yawRightRange <= 0f) yawRightRange = 30f;
            if (pitchUpRange <= 0f) pitchUpRange = 20f;
            if (pitchDownRange <= 0f) pitchDownRange = 25f;

            // Clamped against the range of the direction actually asked for —
            // an eye cannot go past what its shape delivers at weight 100.
            float right = Mathf.Clamp(anglesDeg.x, -yawLeftRange, yawRightRange);
            float down = Mathf.Clamp(anglesDeg.y, -pitchUpRange, pitchDownRange);

            wRight = right > 0f ? right / yawRightRange * 100f * weight : 0f;
            wLeft = right < 0f ? -right / yawLeftRange * 100f * weight : 0f;
            wDown = down > 0f ? down / pitchDownRange * 100f * weight : 0f;
            wUp = down < 0f ? -down / pitchUpRange * 100f * weight : 0f;
        }

        /// <summary>
        /// The shape's full-weight rotation magnitude. BoneDriver scales it
        /// with Quaternion.Lerp, which across these angles tracks a linear
        /// ramp to well under a tenth of a degree, so a plain ratio inverts it.
        /// </summary>
        private static float ShapeDegrees(BoneDriver.Expression e)
        {
            if (e.RotationArr == null || e.RotationArr.Length < 4) return 0f;
            Quaternion q = e.GetRotaion();
            return 2f * Mathf.Acos(Mathf.Clamp01(Mathf.Abs(q.w))) * Mathf.Rad2Deg;
        }
    }

    /// <summary>
    /// Drives the CC Eye_L/R_Look_* and Eye_Blink_L/R blendshapes on a body
    /// mesh — the Reallusion BoneDriver converts them into eye-bone rotation
    /// plus lid follow and syncs the other renderers. The look math matches
    /// <see cref="GazeDriver"/>, with a weight parameter added so a gaze
    /// lock can fade the eyes in and out.
    ///
    /// Each eye is written separately against its own measured ranges (see
    /// <see cref="EyeLookRanges"/>), so both land on the target instead of
    /// sharing one weight that means a different angle on each of them.
    /// </summary>
    public class CcEyeShapeWriter
    {
        private const float BlinkCloseTime = 0.07f;
        private const float BlinkHoldTime = 0.03f;
        private const float BlinkOpenTime = 0.12f;

        /// <summary>Degrees each look shape delivers at weight 100.</summary>
        public EyeLookRanges ranges = EyeLookRanges.Nominal;

        private readonly SkinnedMeshRenderer mesh;
        private int lookLLeft = -1, lookLRight = -1, lookLUp = -1, lookLDown = -1;
        private int lookRLeft = -1, lookRRight = -1, lookRUp = -1, lookRDown = -1;
        private int blinkL = -1, blinkR = -1;

        private float nextBlinkIn = -1f;
        private float blinkPhase = -1f;

        public CcEyeShapeWriter(SkinnedMeshRenderer bodyMesh, Object context = null)
        {
            mesh = bodyMesh;
            if (mesh == null || mesh.sharedMesh == null) return;

            Mesh shared = mesh.sharedMesh;
            var missing = new List<string>();
            int Find(string name)
            {
                int i = shared.GetBlendShapeIndex(name);
                if (i < 0) missing.Add(name);
                return i;
            }

            lookLLeft = Find("Eye_L_Look_L");
            lookLRight = Find("Eye_L_Look_R");
            lookLUp = Find("Eye_L_Look_Up");
            lookLDown = Find("Eye_L_Look_Down");
            lookRLeft = Find("Eye_R_Look_L");
            lookRRight = Find("Eye_R_Look_R");
            lookRUp = Find("Eye_R_Look_Up");
            lookRDown = Find("Eye_R_Look_Down");
            blinkL = Find("Eye_Blink_L");
            blinkR = Find("Eye_Blink_R");

            if (missing.Count > 0)
                Debug.LogWarning($"CcEyeShapeWriter: blendshapes not found on '{mesh.name}': {string.Join(", ", missing)}.", context);
        }

        /// <summary>
        /// Writes the look shapes for per-eye yaw/pitch (right/down positive,
        /// degrees), scaled by weight (0 clears them). Giving each eye its own
        /// angles — measured from that eye's own position — is what produces
        /// vergence: at conversational range the eyes have to toe in a couple
        /// of degrees to meet at the target instead of staying parallel.
        /// </summary>
        public void WriteLook(Vector2 leftAnglesDeg, Vector2 rightAnglesDeg, float weight)
        {
            if (mesh == null) return;

            WriteEye(leftAnglesDeg, weight,
                ranges.leftYawLeft, ranges.leftYawRight, ranges.leftPitchUp, ranges.leftPitchDown,
                lookLLeft, lookLRight, lookLUp, lookLDown);

            WriteEye(rightAnglesDeg, weight,
                ranges.rightYawLeft, ranges.rightYawRight, ranges.rightPitchUp, ranges.rightPitchDown,
                lookRLeft, lookRRight, lookRUp, lookRDown);
        }

        /// <summary>Same angles for both eyes — no vergence. For clearing, or when the eye bones are unavailable.</summary>
        public void WriteLook(Vector2 anglesDeg, float weight)
        {
            WriteLook(anglesDeg, anglesDeg, weight);
        }

        private void WriteEye(Vector2 angles, float weight,
            float yawLeftRange, float yawRightRange, float pitchUpRange, float pitchDownRange,
            int idxLeft, int idxRight, int idxUp, int idxDown)
        {
            EyeLookRanges.ShapeWeights(angles, weight,
                yawLeftRange, yawRightRange, pitchUpRange, pitchDownRange,
                out float wLeft, out float wRight, out float wUp, out float wDown);

            Set(idxRight, wRight);
            Set(idxLeft, wLeft);
            Set(idxDown, wDown);
            Set(idxUp, wUp);
        }

        /// <summary>Advances the spontaneous blink envelope; call every frame.</summary>
        public void UpdateBlink(float dt, Vector2 interval)
        {
            if (mesh == null) return;

            if (nextBlinkIn < 0f) nextBlinkIn = Random.Range(interval.x, interval.y);

            if (blinkPhase < 0f)
            {
                nextBlinkIn -= dt;
                if (nextBlinkIn <= 0f) blinkPhase = 0f;
            }

            float w = 0f;
            if (blinkPhase >= 0f)
            {
                blinkPhase += dt;
                if (blinkPhase < BlinkCloseTime)
                    w = blinkPhase / BlinkCloseTime;
                else if (blinkPhase < BlinkCloseTime + BlinkHoldTime)
                    w = 1f;
                else if (blinkPhase < BlinkCloseTime + BlinkHoldTime + BlinkOpenTime)
                    w = 1f - (blinkPhase - BlinkCloseTime - BlinkHoldTime) / BlinkOpenTime;
                else
                {
                    blinkPhase = -1f;
                    nextBlinkIn = Random.Range(interval.x, interval.y);
                }
            }

            // Written every frame, open included, so the caller owns the lids
            // while it blinks them — a clip with eyelid curves (the mocap
            // intros) otherwise shows through between blinks.
            Set(blinkL, w * 100f);
            Set(blinkR, w * 100f);
        }

        /// <summary>Zeroes every driven shape — call on disable so the editor state stays clean.</summary>
        public void ResetAll()
        {
            if (mesh == null) return;
            Set(lookLLeft, 0f); Set(lookLRight, 0f); Set(lookLUp, 0f); Set(lookLDown, 0f);
            Set(lookRLeft, 0f); Set(lookRRight, 0f); Set(lookRUp, 0f); Set(lookRDown, 0f);
            Set(blinkL, 0f); Set(blinkR, 0f);
            blinkPhase = -1f;
        }

        private void Set(int index, float weight)
        {
            if (index >= 0) mesh.SetBlendShapeWeight(index, weight);
        }
    }
}
