using System;
using UnityEngine;

public sealed class MelAnimationMenuData : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        public string label;
        public string statePath;
        public int parameterIndex;
    }

    public RuntimeAnimatorController controller;
    public Entry[] baseAnimations = Array.Empty<Entry>();
    public Entry[] faceAnimations = Array.Empty<Entry>();
    public float defaultBlendTime = 0.5f;
}
