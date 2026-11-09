using System;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>How the character carries its weight — decides what a weight shift does to the pelvis.</summary>
    public enum IdleStance
    {
        /// <summary>Seated: weight shifts roll the pelvis; the seat contact never moves.</summary>
        Seated,

        /// <summary>Standing: the pelvis travels sideways with a smaller roll, and both legs are solved back so the feet stay planted.</summary>
        Standing,
    }

    /// <summary>
    /// Tuning for <see cref="ProceduralIdleMotion"/>: breathing, postural
    /// sway, weight shifts, arm micro-motion, neck drift, facial life and
    /// blinking. Lives on the component itself, or per mode inside a
    /// profile asset that implements <see cref="IIdleMotionSettingsSource"/>.
    /// </summary>
    [Serializable]
    public class IdleMotionSettings
    {
        [Tooltip("Master switch for procedural idle life.")]
        public bool enabled = true;

        [Tooltip("Master multiplier over all amplitudes.")]
        public float intensity = 1f;

        [Header("Stance")]
        [Tooltip("Seated: weight shifts roll the pelvis (the seat contact never moves). Standing: the pelvis translates sideways with a smaller roll and both legs are solved back so the feet stay planted.")]
        public IdleStance stance = IdleStance.Seated;

        [Header("Breathing")]
        [Tooltip("Base breathing rate; each character rolls a personal variation.")]
        public float breathsPerMinute = 14f;

        [Tooltip("Per-character random variation of the breathing rate, ± breaths per minute.")]
        public float breathVariation = 2f;

        [Tooltip("Chest pitch amplitude of a breath, degrees.")]
        public float chestPitchDegrees = 1.5f;

        [Tooltip("Shoulder rise amplitude of a breath, degrees.")]
        public float shoulderRiseDegrees = 0.8f;

        [Header("Postural sway")]
        [Tooltip("Sway amplitudes in degrees: x = yaw, y = pitch, z = roll. Perlin-driven, never repeats.")]
        public Vector3 swayDegrees = new Vector3(1.5f, 1f, 1f);

        [Tooltip("Sway speed in Hz — how fast the torso wanders.")]
        public float swayFrequency = 0.08f;

        [Header("Weight shifts")]
        [Tooltip("Random seconds between weight shifts.")]
        public Vector2 shiftInterval = new Vector2(20f, 60f);

        [Tooltip("Pelvis roll amplitude of a weight shift, degrees (torso counter-balances).")]
        public float shiftRollDegrees = 3.5f;

        [Tooltip("Seconds a weight shift takes to settle.")]
        public float shiftBlendSeconds = 2f;

        [Tooltip("Standing only — sideways pelvis travel of a full weight shift, centimetres. The legs are solved back so the feet stay where the animation put them.")]
        public float shiftLateralCentimetres = 3f;

        [Tooltip("Standing only — fraction of Shift Roll Degrees still applied as pelvis roll on top of the sideways travel.")]
        [Range(0f, 1f)] public float standingRollScale = 0.25f;

        [Header("Arms")]
        [Tooltip("Micro-motion of the upper arms and forearms, degrees. Gives held props and resting hands subtle life — set 0 for poses where a hand must stay planted on a surface (until arm IK exists).")]
        public float armSwayDegrees = 0.4f;

        [Header("Neck drift")]
        [Tooltip("Slow neck wander amplitude, degrees. The head bone itself is left to the gaze system.")]
        public float neckDriftDegrees = 1.5f;

        [Tooltip("Neck wander speed in Hz.")]
        public float neckDriftFrequency = 0.05f;

        [Header("Facial life (CC blendshapes)")]
        [Tooltip("Master switch for procedural facial micro-expression on top of the animation.")]
        public bool faceEnabled = true;

        [Tooltip("Master multiplier over all facial amplitudes.")]
        public float faceIntensity = 1f;

        [Tooltip("Resting expression bias, 0..1 — how much the mouth corners rest in a faint smile vs neutral. Absorbed readers ~0.15, engaged listeners ~0.3.")]
        [Range(0f, 1f)] public float restingSmile = 0.15f;

        [Tooltip("Animate the eyebrows (slow raise/drop drift + brow-raise / furrow events). Off by default — CC brow shapes read as odd on some characters; the rest of the face (lids, cheeks, mouth, swallow, nostrils) still animates.")]
        public bool animateBrows = false;

        [Tooltip("Slow Perlin drift of the brows/lids/mouth (blendshape weight units, 0-100). Never repeats.")]
        public float driftAmount = 6f;

        [Tooltip("Facial drift speed in Hz.")]
        public float driftFrequency = 0.06f;

        [Tooltip("Random seconds between discrete micro-events (a brow raise, a squint, a lip press, a swallow).")]
        public Vector2 eventInterval = new Vector2(6f, 18f);

        [Tooltip("Peak strength of micro-events, blendshape weight units (0-100).")]
        public float eventStrength = 22f;

        [Header("Blinking")]
        [Tooltip("Spontaneous blinks (~15-20/min). Runs here so every character with idle life blinks — including recording/billboard characters without a gaze controller.")]
        public bool blinkEnabled = true;

        [Tooltip("Random seconds between blinks.")]
        public Vector2 blinkInterval = new Vector2(2.5f, 5.5f);

        [Tooltip("Chance a blink comes as a quick double blink.")]
        [Range(0f, 1f)] public float doubleBlinkChance = 0.12f;
    }
}
