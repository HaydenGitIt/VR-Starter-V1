using UnityEngine;

/// <summary>
/// Sets up the AudioSource carrying Mel's voice so it behaves like a person
/// talking rather than a soundtrack.
///
/// Put this on a child GameObject positioned at her head, alongside the
/// AudioSource and StreamingAudioPlayer.
///
/// WHY THIS NEEDS ITS OWN SCRIPT:
/// The default AudioSource settings are wrong for a voice in VR in several
/// ways at once, and each one is a separate field buried in the Inspector.
/// Getting any of them wrong produces something that sounds almost right,
/// which is harder to notice and harder to diagnose than something obviously
/// broken. Doing it in code means it is applied consistently and the reasons
/// are written down.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class VoiceSourceSetup : MonoBehaviour
{
    [Header("Distance")]
    [Tooltip("Below this distance the voice is at full volume. A person talking to you " +
             "is about a metre away, so this should cover normal conversation distance " +
             "without the volume changing as the trainee shifts their weight.")]
    [SerializeField] private float minDistance = 1.5f;

    [Tooltip("Beyond this the voice is inaudible. A room-sized value - she is in the " +
             "same room, not across a field.")]
    [SerializeField] private float maxDistance = 12f;

    [Header("Volume")]
    [SerializeField, Range(0f, 1f)] private float volume = 1f;

    [Header("Verification")]
    [Tooltip("Warn if this object is not near the character's head. A voice coming from " +
             "the floor or the hips is subtly wrong in a way trainees notice without " +
             "being able to say why.")]
    [SerializeField] private bool warnIfLow = true;

    [Tooltip("Minimum height above the floor before it looks like a mistake.")]
    [SerializeField] private float expectedMinHeight = 1.2f;

    private void Awake()
    {
        AudioSource source = GetComponent<AudioSource>();

        // Fully 3D. This is the setting that makes the voice come from HER
        // rather than from everywhere at once - the single most important one.
        source.spatialBlend = 1f;

        // Logarithmic rolloff, which is how sound actually behaves. Linear
        // rolloff is a game convention that sounds artificial for a voice.
        source.rolloffMode = AudioRolloffMode.Logarithmic;

        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        source.volume = volume;

        // No Doppler. She is not moving fast, and any pitch shift from the
        // trainee turning their head would be an artefact rather than realism.
        source.dopplerLevel = 0f;

        // Full stereo spread rather than a point source, which sounds slightly
        // more natural for a voice at conversational distance.
        source.spread = 20f;

        source.loop = true;
        source.playOnAwake = false;

        // Voices should not be quietly culled when several things are playing.
        source.priority = 0;
    }

    private void Start()
    {
        if (!warnIfLow) return;

        if (transform.position.y < expectedMinHeight)
        {
            Debug.LogWarning(
                $"[VoiceSourceSetup] This voice source is at y={transform.position.y:F2}, " +
                $"below the expected {expectedMinHeight}m. It should sit at the character's " +
                "head. A voice coming from floor level is subtly wrong in VR.");
        }
    }
}
