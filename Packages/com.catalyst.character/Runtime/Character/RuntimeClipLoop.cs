using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace catalyst.xr67
{
    /// <summary>
    /// Plays a single clip on the Animator in Play Mode without needing an
    /// AnimatorController — for quick tests. Add it next to the character's
    /// Animator. Loops when the clip's Loop Time import setting is on; root
    /// motion is suppressed so the character stays anchored at its scene
    /// position, matching an edit-mode preview of the same clip.
    /// Playback starts at a random point in the loop and at a random speed
    /// between <see cref="minSpeed"/> and <see cref="maxSpeed"/>, so several
    /// characters sharing the clip don't move in lockstep.
    /// </summary>
    [DisallowMultipleComponent]
    public class RuntimeClipLoop : MonoBehaviour
    {
        [Tooltip("Clip to play.")]
        public AnimationClip clip;

        [Tooltip("Lower bound of the random playback speed.")]
        public float minSpeed = 0.9f;

        [Tooltip("Upper bound of the random playback speed.")]
        public float maxSpeed = 1.05f;

        private PlayableGraph graph;

        private void OnEnable()
        {
            Animator animator = GetComponent<Animator>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);

            AnimationClip resolved = clip;
            if (animator == null || resolved == null) return;

            animator.applyRootMotion = false;
            AnimationClipPlayable playable = AnimationPlayableUtilities.PlayClip(animator, resolved, out graph);
            playable.SetSpeed(Random.Range(minSpeed, maxSpeed));
            playable.SetTime(Random.Range(0f, resolved.length));
        }

        private void OnDisable()
        {
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
