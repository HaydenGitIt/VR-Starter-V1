using System;
using UnityEngine;

/// <summary>
/// Watches the microphone stream and decides when the trainee has started
/// and stopped speaking, then hands out the complete utterance as one buffer.
///
/// This replaces the push-to-talk button from the UnityNeuroSpeech demo.
///
/// All thresholds and timings come from PipelineConfig, so they can be tuned
/// per deployment - a quiet office and a working kindergarten will need
/// different values, and that should not mean editing code.
///
/// THE THREE IDEAS THAT MAKE THIS WORK:
///
/// 1. HYSTERESIS - the level needed to decide "speech started" is HIGHER
///    than the level needed to decide "speech continues". Without this,
///    natural dips mid-sentence flip the detector off and on, chopping one
///    sentence into fragments.
///
/// 2. HANG TIME - the utterance does not end the instant the level drops.
///    We wait for a set period of quiet, so a pause between clauses is not
///    mistaken for the end of a turn.
///
/// 3. PRE-ROLL - we continuously retain the last fraction of a second of
///    audio and prepend it to the utterance. Detection always lags the true
///    start of speech, so without pre-roll the first consonant is clipped
///    and the STT engine mishears the opening word.
/// </summary>
[RequireComponent(typeof(MicrophoneCapture))]
public class VoiceActivityDetector : MonoBehaviour
{
    public enum VadState
    {
        /// <summary>Quiet. Not recording, but pre-roll keeps filling.</summary>
        Idle,

        /// <summary>Level has risen, but not yet long enough to confirm speech.</summary>
        Onset,

        /// <summary>Confirmed speech. Recording.</summary>
        Speaking,

        /// <summary>Level has dropped, but not yet long enough to confirm the end.</summary>
        Trailing,

        /// <summary>Detection suspended, e.g. while the NPC is talking.</summary>
        Gated
    }

    [Header("Configuration")]
    [Tooltip("Thresholds and timings come from here. Leave empty to use the config on MicrophoneCapture.")]
    [SerializeField] private PipelineConfig config;

    [Header("Behaviour")]
    [Tooltip("If false, all audio is ignored. Used to mute the trainee while the NPC speaks.")]
    [SerializeField] private bool listeningEnabled = true;

    // ---------------------------------------------------------------
    // PUBLIC STATE
    // ---------------------------------------------------------------

    public VadState State { get; private set; } = VadState.Idle;

    /// <summary>Seconds of audio in the utterance currently being recorded, including pre-roll.</summary>
    public float CurrentUtteranceLength =>
        utteranceSampleCount / (float)Mathf.Max(1, activeFormat.sampleRate);

    public int UtteranceCount { get; private set; }

    /// <summary>
    /// Raised when a complete utterance has been captured.
    /// The buffer is a fresh copy and safe to retain.
    /// </summary>
    public event Action<float[], AudioFormat> OnUtteranceCaptured;

    /// <summary>Raised the moment speech is confirmed - useful for UI feedback.</summary>
    public event Action OnSpeechStarted;

    // ---------------------------------------------------------------
    // INTERNAL
    // ---------------------------------------------------------------

    private MicrophoneCapture capture;
    private AudioFormat activeFormat;
    private bool buffersReady;

    // Rolling buffer holding the most recent pre-roll window at all times.
    private float[] preRollBuffer;
    private int preRollCapacity;
    private int preRollWritePos;
    private int preRollFilled;

    // Buffer accumulating the utterance in progress.
    private float[] utteranceBuffer;
    private int utteranceSampleCount;

    // How much of the current utterance is pre-roll rather than detected
    // speech. Needed so the minimum-length test measures real speech and
    // is not satisfied by pre-roll padding alone.
    private int preRollSamplesIncluded;

    // Timers measured in seconds of audio, not frame time, so behaviour
    // does not shift with frame rate.
    private float timeAboveThreshold;
    private float timeBelowThreshold;

    private void Awake()
    {
        capture = GetComponent<MicrophoneCapture>();

        if (config == null)
        {
            config = capture.Config;
        }
    }

    private void OnEnable()
    {
        capture.OnSamplesCaptured += HandleSamples;
        capture.OnCaptureStarted += HandleCaptureStarted;
    }

    private void OnDisable()
    {
        capture.OnSamplesCaptured -= HandleSamples;
        capture.OnCaptureStarted -= HandleCaptureStarted;
    }

    /// <summary>
    /// Buffers are sized here rather than in Start(), because the true sample
    /// rate is only known once the device is open - and the device may have
    /// refused the configured rate. Sizing from the config value in Start()
    /// would be a race: if MicrophoneCapture had not started yet, every
    /// timing in this detector would be wrong by the ratio between the
    /// requested and actual rates.
    /// </summary>
    private void HandleCaptureStarted(AudioFormat format)
    {
        activeFormat = format;

        if (config == null)
        {
            Debug.LogError("[VAD] No PipelineConfig available. Detection disabled.");
            enabled = false;
            return;
        }

        if (!config.Validate(out string problem))
        {
            Debug.LogError($"[VAD] Config problem: {problem}");
            enabled = false;
            return;
        }

        int rate = format.sampleRate;

        preRollCapacity = Mathf.Max(1, Mathf.CeilToInt(config.preRollTime * rate));
        preRollBuffer = new float[preRollCapacity];
        preRollWritePos = 0;
        preRollFilled = 0;

        int maxSamples = Mathf.CeilToInt((config.maxUtteranceLength + config.preRollTime) * rate);
        utteranceBuffer = new float[maxSamples];
        utteranceSampleCount = 0;
        preRollSamplesIncluded = 0;

        buffersReady = true;

        Debug.Log($"[VAD] Ready at {format}. Onset {config.onsetThresholdDb}dB / " +
                  $"release {config.releaseThresholdDb}dB, hang {config.hangTime}s, pre-roll {config.preRollTime}s.");
    }

    /// <summary>
    /// Turn detection on or off. Call with false while the NPC is speaking,
    /// so its voice from the headset speakers is not picked up by the headset
    /// microphone and transcribed as if the trainee had said it.
    /// </summary>
    public void SetListening(bool enabled)
    {
        if (listeningEnabled == enabled) return;

        listeningEnabled = enabled;

        if (!enabled)
        {
            // Discard anything part-recorded, and clear pre-roll so no NPC
            // audio can leak into the start of the next utterance.
            ResetUtterance();
            preRollFilled = 0;
            preRollWritePos = 0;
            State = VadState.Gated;
        }
        else
        {
            State = VadState.Idle;
        }
    }

    private void HandleSamples(float[] samples, int count)
    {
        if (!listeningEnabled || !buffersReady) return;

        float blockDuration = count / (float)activeFormat.sampleRate;
        float blockDb = MicrophoneCapture.ComputeDb(samples, count);

        bool aboveOnset = blockDb > config.onsetThresholdDb;
        bool aboveRelease = blockDb > config.releaseThresholdDb;

        switch (State)
        {
            case VadState.Idle:
                if (aboveOnset)
                {
                    timeAboveThreshold = blockDuration;
                    State = VadState.Onset;
                }
                break;

            case VadState.Onset:
                if (aboveOnset)
                {
                    timeAboveThreshold += blockDuration;

                    if (timeAboveThreshold >= config.onsetConfirmTime)
                    {
                        BeginUtterance(samples, count);
                    }
                }
                else
                {
                    // Just a blip. Stand down.
                    timeAboveThreshold = 0f;
                    State = VadState.Idle;
                }
                break;

            case VadState.Speaking:
                AppendToUtterance(samples, count);

                if (!aboveRelease)
                {
                    timeBelowThreshold = blockDuration;
                    State = VadState.Trailing;
                }

                CheckMaxLength();
                break;

            case VadState.Trailing:
                // Keep recording through the trailing quiet: if speech
                // resumes we want these samples, and if it does not, a little
                // silence on the end is harmless and helps the STT engine.
                AppendToUtterance(samples, count);

                if (aboveRelease)
                {
                    timeBelowThreshold = 0f;
                    State = VadState.Speaking;
                }
                else
                {
                    timeBelowThreshold += blockDuration;

                    if (timeBelowThreshold >= config.hangTime)
                    {
                        EndUtterance();
                    }
                }

                CheckMaxLength();
                break;
        }

        // Pre-roll is pushed AFTER the state machine has run, and in every
        // state. Pushing after avoids double-counting the current block when
        // an utterance begins, and pushing always means a trainee who speaks
        // again immediately still gets clean pre-roll on their next turn.
        PushToPreRoll(samples, count);
    }

    private void BeginUtterance(float[] samples, int count)
    {
        utteranceSampleCount = 0;
        preRollSamplesIncluded = 0;

        // Prepend pre-roll so the start of the first word is not lost.
        CopyPreRollInto(utteranceBuffer, ref utteranceSampleCount);
        preRollSamplesIncluded = utteranceSampleCount;

        AppendToUtterance(samples, count);

        timeBelowThreshold = 0f;
        State = VadState.Speaking;

        OnSpeechStarted?.Invoke();
    }

    private void EndUtterance()
    {
        int rate = activeFormat.sampleRate;

        int capturedCount = utteranceSampleCount;
        int speechSamples = capturedCount - preRollSamplesIncluded;
        float speechSeconds = speechSamples / (float)rate;

        // Reset before dispatching, so a handler that immediately gates the
        // mic does not collide with our own bookkeeping.
        ResetUtterance();
        State = VadState.Idle;

        // Measure REAL speech, excluding pre-roll padding. Otherwise a
        // minimum of 0.4s with 0.3s of pre-roll would accept a 0.1s noise blip.
        if (speechSeconds < config.minSpeechLength)
        {
            Debug.Log($"[VAD] Discarded: {speechSeconds:F2}s of speech is below the " +
                      $"{config.minSpeechLength:F2}s minimum.");
            return;
        }

        float[] result = new float[capturedCount];
        Array.Copy(utteranceBuffer, result, capturedCount);

        UtteranceCount++;

        Debug.Log($"[VAD] Utterance #{UtteranceCount}: {capturedCount / (float)rate:F2}s total " +
                  $"({speechSeconds:F2}s speech + {preRollSamplesIncluded / (float)rate:F2}s pre-roll).");

        OnUtteranceCaptured?.Invoke(result, activeFormat);
    }

    private void ResetUtterance()
    {
        utteranceSampleCount = 0;
        preRollSamplesIncluded = 0;
        timeAboveThreshold = 0f;
        timeBelowThreshold = 0f;
    }

    private void CheckMaxLength()
    {
        if (CurrentUtteranceLength >= config.maxUtteranceLength)
        {
            Debug.LogWarning($"[VAD] Hit the {config.maxUtteranceLength}s cap - ending utterance early.");
            EndUtterance();
        }
    }

    private void AppendToUtterance(float[] samples, int count)
    {
        int space = utteranceBuffer.Length - utteranceSampleCount;
        int toCopy = Mathf.Min(count, space);

        if (toCopy <= 0) return;

        Array.Copy(samples, 0, utteranceBuffer, utteranceSampleCount, toCopy);
        utteranceSampleCount += toCopy;
    }

    // ---------------------------------------------------------------
    // PRE-ROLL RING BUFFER
    // Always holds the most recent pre-roll window, overwriting the oldest
    // samples as new ones arrive.
    // ---------------------------------------------------------------

    private void PushToPreRoll(float[] samples, int count)
    {
        for (int i = 0; i < count; i++)
        {
            preRollBuffer[preRollWritePos] = samples[i];
            preRollWritePos = (preRollWritePos + 1) % preRollCapacity;
        }

        preRollFilled = Mathf.Min(preRollFilled + count, preRollCapacity);
    }

    private void CopyPreRollInto(float[] destination, ref int destinationCount)
    {
        if (preRollFilled == 0) return;

        // Once the buffer is full, the oldest sample sits just after the
        // write head. Before that, it starts at zero.
        int start = preRollFilled < preRollCapacity ? 0 : preRollWritePos;

        for (int i = 0; i < preRollFilled; i++)
        {
            int index = (start + i) % preRollCapacity;
            destination[destinationCount++] = preRollBuffer[index];
        }
    }
}
