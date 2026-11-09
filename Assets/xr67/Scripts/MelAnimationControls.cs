using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public sealed class MelAnimationControls : MonoBehaviour
{
    public const string BaseParameter = "MelBaseAnimation";
    public const string FaceParameter = "MelFaceAnimation";
    public Animator Animator => GetComponent<Animator>();

    public void PlayBase(int animationIndex) => Animator.SetInteger(BaseParameter, animationIndex);
    public void PlayFace(int animationIndex) => Animator.SetInteger(FaceParameter, animationIndex);
}
