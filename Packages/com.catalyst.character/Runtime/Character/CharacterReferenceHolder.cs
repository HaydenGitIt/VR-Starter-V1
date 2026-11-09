using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Scene-side registry for a single character (a Character Creator export:
    /// CC_Base_Body and its rig). Add it to the character root in the scene and
    /// assign the references in the Inspector, or right-click the component
    /// header and run "Resolve From Character Root" to fill the empty fields
    /// from the standard CC naming / humanoid rig.
    /// Depends on UnityEngine only — no Robotlegs or Reallusion types — so it
    /// can be moved to a shared package later.
    /// </summary>
    public class CharacterReferenceHolder : MonoBehaviour
    {
        [Header("Character")]
        [Tooltip("Root of the character hierarchy. This GameObject when left empty.")]
        public GameObject characterRoot;

        [Header("Animation")]
        [Tooltip("Animator driving the rig (sits on the character root in CC exports).")]
        public Animator animator;

        [Tooltip("Controller to run on the Animator, e.g. AC_boy1. Left empty, the Animator keeps whatever controller it already has.")]
        public RuntimeAnimatorController animatorController;

        [Header("Skinned meshes")]
        [Tooltip("CC_Base_Body — the main skinned mesh; carries the facial/viseme blendshapes.")]
        public SkinnedMeshRenderer bodyMesh;

        [Tooltip("CC_Base_Teeth — mirrors the body's facial blendshape names; drive it together with the body.")]
        public SkinnedMeshRenderer teethMesh;

        [Tooltip("CC_Base_Tongue — mirrors the body's facial blendshape names; drive it together with the body.")]
        public SkinnedMeshRenderer tongueMesh;

        [Tooltip("CC_Base_Eye — both eyes in one mesh.")]
        public SkinnedMeshRenderer eyeMesh;

        [Tooltip("Everything else skinned to the rig: hair, brows, lashes, clothing.")]
        public SkinnedMeshRenderer[] extraMeshes;

        [Header("Facial bones")]
        [Tooltip("CC_Base_Head — head look-at / IK target.")]
        public Transform headBone;

        [Tooltip("CC_Base_JawRoot — jaw openness, e.g. scaled by lip-sync MouthOpen.")]
        public Transform jawBone;

        [Tooltip("CC_Base_L_Eye — left eye gaze target.")]
        public Transform leftEyeBone;

        [Tooltip("CC_Base_R_Eye — right eye gaze target.")]
        public Transform rightEyeBone;

        [Header("Audio")]
        [Tooltip("This character's voice output; the input for lip-sync analysis.")]
        public AudioSource voiceSource;

        public GameObject Root => characterRoot != null ? characterRoot : gameObject;

        /// <summary>Controller in effect: the explicit override, else the Animator's own.</summary>
        public RuntimeAnimatorController EffectiveController =>
            animatorController != null ? animatorController
            : animator != null ? animator.runtimeAnimatorController
            : null;

        /// <summary>
        /// Fills every unassigned field from the hierarchy under <see cref="Root"/>:
        /// exact CC_Base_* names first, then the humanoid rig as fallback for
        /// bones. Already-assigned fields are left untouched, so it is safe to
        /// re-run after manual edits. Also callable at runtime for characters
        /// spawned from prefabs.
        /// </summary>
        [ContextMenu("Resolve From Character Root")]
        public void ResolveReferences()
        {
            GameObject root = Root;

            if (animator == null) animator = root.GetComponentInChildren<Animator>(true);
            if (animatorController == null && animator != null)
                animatorController = animator.runtimeAnimatorController;

            if (bodyMesh == null) bodyMesh = FindRenderer(root, "CC_Base_Body");
            if (teethMesh == null) teethMesh = FindRenderer(root, "CC_Base_Teeth");
            if (tongueMesh == null) tongueMesh = FindRenderer(root, "CC_Base_Tongue");
            if (eyeMesh == null) eyeMesh = FindRenderer(root, "CC_Base_Eye");
            if (extraMeshes == null || extraMeshes.Length == 0) extraMeshes = CollectExtraMeshes(root);

            if (headBone == null) headBone = FindBone(root, "CC_Base_Head", HumanBodyBones.Head);
            if (jawBone == null) jawBone = FindBone(root, "CC_Base_JawRoot", HumanBodyBones.Jaw);
            if (leftEyeBone == null) leftEyeBone = FindBone(root, "CC_Base_L_Eye", HumanBodyBones.LeftEye);
            if (rightEyeBone == null) rightEyeBone = FindBone(root, "CC_Base_R_Eye", HumanBodyBones.RightEye);

            if (voiceSource == null) voiceSource = root.GetComponentInChildren<AudioSource>(true);

            if (bodyMesh == null)
                Debug.LogWarning($"CharacterReferenceHolder: no CC_Base_Body found under '{root.name}' — assign the body mesh manually.", this);
        }

        private static SkinnedMeshRenderer FindRenderer(GameObject root, string exactName)
        {
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.name == exactName)
                    return smr;
            return null;
        }

        private Transform FindBone(GameObject root, string exactName, HumanBodyBones humanoidFallback)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == exactName)
                    return t;
            if (animator != null && animator.isHuman)
                return animator.GetBoneTransform(humanoidFallback);
            return null;
        }

        private SkinnedMeshRenderer[] CollectExtraMeshes(GameObject root)
        {
            var extras = new System.Collections.Generic.List<SkinnedMeshRenderer>();
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr != bodyMesh && smr != teethMesh && smr != tongueMesh && smr != eyeMesh)
                    extras.Add(smr);
            return extras.ToArray();
        }
    }
}
