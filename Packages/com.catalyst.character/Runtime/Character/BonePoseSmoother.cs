using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace catalyst.xr67
{
    /// <summary>
    /// Per-bone-subtree low-pass filter that removes mocap jitter after the
    /// animation has posed the skeleton — assign a group per noisy region
    /// (typically each hand root for twitchy mediapipe fingers) and every
    /// bone under it gets its local rotation exponentially smoothed toward
    /// the animated value. Lower rates smooth harder but lag more; hands
    /// read well around 10-20. Runs in LateUpdate before StudentGazeController
    /// (-10) and the Reallusion BoneDriver (0); in Edit Mode it follows the
    /// PoseSampled event of whichever <see cref="IPoseSampler"/> is previewing
    /// (AnimationSequencePlayer, an edit-mode preview, an ambient player),
    /// so previews and recordings show the filtered result too. Don't add the
    /// head bone's subtree on characters using gaze — the gaze offset would
    /// be smoothed against itself.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-11)]
    public class BonePoseSmoother : MonoBehaviour
    {
        [Serializable]
        public class Group
        {
            [Tooltip("Root of the smoothed subtree, e.g. CC_Base_L_Hand — the root and every child bone are filtered.")]
            public Transform root;

            [Tooltip("Response rate: lower = smoother but laggier. Fingers read well around 10-20.")]
            public float rate = 15f;
        }

        [Tooltip("Bone subtrees to smooth, each with its own strength.")]
        public List<Group> groups = new List<Group>();

        private readonly List<Transform[]> bones = new List<Transform[]>();
        private readonly List<Quaternion[]> smoothed = new List<Quaternion[]>();
        private bool primed;

#if UNITY_EDITOR
        private double lastEditorTime;
#endif

        private void OnEnable()
        {
            RebuildCaches();
            SubscribePlayers(true);
#if UNITY_EDITOR
            lastEditorTime = EditorApplication.timeSinceStartup;
#endif
        }

        private void OnDisable()
        {
            SubscribePlayers(false);
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) RebuildCaches();
        }

        /// <summary>Finds the humanoid hand bones and adds a group per hand (the common case).</summary>
        [ContextMenu("Add Hand Groups")]
        private void AddHandGroups()
        {
            Animator animator = GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman) return;
            AddGroupFor(animator.GetBoneTransform(HumanBodyBones.LeftHand));
            AddGroupFor(animator.GetBoneTransform(HumanBodyBones.RightHand));
            RebuildCaches();
        }

        private void AddGroupFor(Transform root)
        {
            if (root == null) return;
            foreach (Group existing in groups)
                if (existing.root == root) return;
            groups.Add(new Group { root = root });
        }

        private void SubscribePlayers(bool on)
        {
            // Edit-mode previews evaluate outside LateUpdate; follow their
            // PoseSampled so the filter runs on every previewed pose.
            foreach (IPoseSampler sampler in GetComponents<IPoseSampler>())
            {
                if (sampler == null) continue;
                sampler.PoseSampled -= OnPoseSampled;
                if (on) sampler.PoseSampled += OnPoseSampled;
            }
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            Step(Time.deltaTime);
        }

        private void OnPoseSampled()
        {
#if UNITY_EDITOR
            if (Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - lastEditorTime), 0f, 0.1f);
            lastEditorTime = now;
            // Follow a sequencer's exact step so recordings are deterministic.
            AnimationSequencePlayer sequence = GetComponent<AnimationSequencePlayer>();
            if (sequence != null && sequence.isActiveAndEnabled) dt = sequence.LastStepDelta;
            Step(dt);
#endif
        }

        /// <summary>
        /// Filters the current animated pose. Public so a deterministic
        /// driver (the recorder) can step it with a fixed dt.
        /// </summary>
        public void Step(float dt)
        {
            if (bones.Count != groups.Count) RebuildCaches();

            for (int g = 0; g < groups.Count; g++)
            {
                Transform[] groupBones = bones[g];
                Quaternion[] groupSmoothed = smoothed[g];
                if (groupBones == null) continue;

                float blend = primed ? GazeMath.SmoothingStep(Mathf.Max(groups[g].rate, 0.01f), dt) : 1f;
                for (int i = 0; i < groupBones.Length; i++)
                {
                    Transform bone = groupBones[i];
                    if (bone == null) continue;
                    Quaternion filtered = Quaternion.Slerp(groupSmoothed[i], bone.localRotation, blend);
                    groupSmoothed[i] = filtered;
                    bone.localRotation = filtered;
                }
            }
            primed = true;
        }

        private void RebuildCaches()
        {
            bones.Clear();
            smoothed.Clear();
            primed = false;
            foreach (Group group in groups)
            {
                if (group == null || group.root == null)
                {
                    bones.Add(null);
                    smoothed.Add(null);
                    continue;
                }
                Transform[] groupBones = group.root.GetComponentsInChildren<Transform>(true);
                var groupSmoothed = new Quaternion[groupBones.Length];
                for (int i = 0; i < groupBones.Length; i++)
                    groupSmoothed[i] = groupBones[i].localRotation;
                bones.Add(groupBones);
                smoothed.Add(groupSmoothed);
            }
        }
    }
}
