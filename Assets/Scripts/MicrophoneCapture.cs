using System;
using UnityEngine;

/// <summary>
/// Opens a microphone and continuously exposes:
///   - the newly-arrived audio samples since the last frame
///   - the current loudness (RMS and dBFS)
///
/// This is the foundation for the whole speech pipeline. Step 2 (voice
/// activity detection) will consume the same sample stream this class
/// produces, so it is deliberately written to be reusable rather than
/// just a test harness.
///
/// HOW UNITY'S MICROPHONE API WORKS (important - it is unusual):
/// Microphone.Start() hands you an AudioClip that behaves as a RING BUFFER.
/// The microphone continuously writes into it and wraps around to the start
/// when it reaches the end. Nothing is "delivered" to you - you must ask
/// Microphone.GetPosition() where the write head currently is, and read the
/// region between wherever you last read and that position.
/// </summary>
public class MicrophoneCapture : MonoBehaviour
{
    [Header("Device Selection")]
    [Tooltip("If set, picks the first device whose name contains this text (case-insensitive). " +
             "For Quest over Link, try 'Oculus'. Leave blank to use the system default device.")]
    [SerializeField] private string preferredDeviceKeyword = "Oculus";

    [Tooltip("If the preferred device is not found, fall back to the system default rather than failing.")]
    [SerializeField] private bool fallBackToDefaultDevice = true;

    [Header("Capture Settings")]
    [Tooltip("Sample rate in Hz. Whisper expects 16000, so we capture at 16000 to avoid resampling later.")]
    [SerializeField] private int sampleRate = 16000;

    [Tooltip("Length of the internal ring buffer in seconds. Only needs to be long enough that we always " +
             "read faster than it wraps. 2 seconds is generous at 60fps.")]
    [SerializeField] private int ringBufferSeconds = 2;

    [Header("Behaviour")]
    [Tooltip("Start capturing as soon as this component wakes up.")]
    [SerializeField] private bool captureOnStart = true;

    // ---------------------------------------------------------------
    // PUBLIC STATE - other scripts read these
    // ---------------------------------------------------------------

    /// <summary>True once the mic is open and delivering samples.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>Name of the device currently in use, or empty.</summary>
    public string ActiveDeviceName { get; private set; } = "";

    /// <summary>Root-mean-square amplitude of the most recent block, 0..1.</summary>
    public float CurrentRms { get; private set; }

    /// <summary>Current level in decibels relative to full scale. Silence is about -80, loud speech about -15.</summary>
    public float CurrentDb { get; private set; } = MinDb;

    /// <summary>Highest RMS seen since the last ResetPeak() call. Useful for checking a mic works at all.</summary>
    public float PeakRms { get; private set; }

    /// <summary>The sample rate actually in use.</summary>
    public int SampleRate => sampleRate;

    /// <summary>
    /// Raised once per frame when new audio has arrived.
    /// Parameters: the sample buffer, and how many entries in it are valid.
    /// NOTE: the buffer is reused between calls, so copy anything you need to keep.
    /// </summary>
    public event Action<float[], int> OnSamplesCaptured;

    public const float MinDb = -80f;

    // ---------------------------------------------------------------
    // INTERNAL
    // ---------------------------------------------------------------

    private AudioClip micClip;
    private int ringBufferSampleCount;
    private int lastReadPosition;

    // Pre-allocated buffers. We allocate once and reuse, because allocating
    // arrays every frame creates garbage that the collector must clean up -
    // and GC spikes in VR show up as dropped frames.
    private float[] readBuffer;
    private float[] scratchBuffer;

    private void Start()
    {
        if (captureOnStart)
        {
            StartCapture();
        }
    }

    private void OnDestroy()
    {
        StopCapture();
    }

    // Stop the mic if the app loses focus, so we do not hold the device open.
    private void OnApplicationQuit()
    {
        StopCapture();
    }

    /// <summary>
    /// Returns every microphone Unity can see. Useful for logging during setup.
    /// </summary>
    public static string[] ListDevices()
    {
        return Microphone.devices;
    }

    public void StartCapture()
    {
        if (IsCapturing) return;

        string[] devices = Microphone.devices;

        if (devices.Length == 0)
        {
            Debug.LogError("[MicrophoneCapture] No microphone devices found. " +
                           "If you are using Quest Link, make sure Link is running and that Windows " +
                           "Settings > Privacy > Microphone allows desktop apps to access the mic.");
            return;
        }

        string chosen = ChooseDevice(devices);
        if (chosen == null) return;

        // Ask the device what sample rates it supports. Many report 0,0 which
        // means "anything goes"; otherwise we must stay within the range.
        Microphone.GetDeviceCaps(chosen, out int minFreq, out int maxFreq);

        int rateToUse = sampleRate;
        if (maxFreq > 0)
        {
            rateToUse = Mathf.Clamp(sampleRate, minFreq, maxFreq);
            if (rateToUse != sampleRate)
            {
                Debug.LogWarning($"[MicrophoneCapture] Device '{chosen}' does not support {sampleRate}Hz " +
                                 $"(supports {minFreq}-{maxFreq}). Using {rateToUse}Hz instead. " +
                                 "Audio will need resampling to 16000Hz before Whisper.");
            }
        }
        sampleRate = rateToUse;

        // loop:true makes the clip behave as a ring buffer rather than stopping when full.
        micClip = Microphone.Start(chosen, true, ringBufferSeconds, sampleRate);

        if (micClip == null)
        {
            Debug.LogError($"[MicrophoneCapture] Microphone.Start failed for device '{chosen}'.");
            return;
        }

        ringBufferSampleCount = micClip.samples;
        lastReadPosition = 0;

        // Worst case in one frame we read the entire ring buffer.
        readBuffer = new float[ringBufferSampleCount];
        scratchBuffer = new float[ringBufferSampleCount];

        ActiveDeviceName = chosen;
        IsCapturing = true;

        Debug.Log($"[MicrophoneCapture] Capturing from '{chosen}' at {sampleRate}Hz " +
                  $"({ringBufferSeconds}s ring buffer = {ringBufferSampleCount} samples).");
    }

    public void StopCapture()
    {
        if (!IsCapturing) return;

        Microphone.End(ActiveDeviceName);

        IsCapturing = false;
        CurrentRms = 0f;
        CurrentDb = MinDb;
        micClip = null;

        Debug.Log($"[MicrophoneCapture] Stopped capturing from '{ActiveDeviceName}'.");
    }

    public void ResetPeak()
    {
        PeakRms = 0f;
    }

    private string ChooseDevice(string[] devices)
    {
        // Log everything we can see - invaluable when a mic does not show up.
        Debug.Log($"[MicrophoneCapture] {devices.Length} microphone device(s) detected:");
        for (int i = 0; i < devices.Length; i++)
        {
            Debug.Log($"   [{i}] {devices[i]}");
        }

        if (!string.IsNullOrEmpty(preferredDeviceKeyword))
        {
            foreach (string device in devices)
            {
                if (device.IndexOf(preferredDeviceKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return device;
                }
            }

            if (!fallBackToDefaultDevice)
            {
                Debug.LogError($"[MicrophoneCapture] No device matching '{preferredDeviceKeyword}' found, " +
                               "and fallback is disabled.");
                return null;
            }

            Debug.LogWarning($"[MicrophoneCapture] No device matching '{preferredDeviceKeyword}'. " +
                             $"Falling back to '{devices[0]}'.");
        }

        // Unity treats index 0 as the system default recording device.
        return devices[0];
    }

    private void Update()
    {
        if (!IsCapturing || micClip == null) return;

        int newSampleCount = ReadNewSamples();

        if (newSampleCount <= 0)
        {
            // No new audio this frame. This is normal for the first few frames
            // after Microphone.Start, before the device has warmed up.
            return;
        }

        UpdateLevels(readBuffer, newSampleCount);

        OnSamplesCaptured?.Invoke(readBuffer, newSampleCount);
    }

    /// <summary>
    /// Copies everything the microphone has written since our last read into
    /// readBuffer, handling the wrap-around. Returns how many samples were read.
    /// </summary>
    private int ReadNewSamples()
    {
        int writePosition = Microphone.GetPosition(ActiveDeviceName);

        // GetPosition can briefly return 0 or -1 before the device is ready.
        if (writePosition < 0) return 0;

        int available = writePosition - lastReadPosition;

        // A negative result means the write head has wrapped past the end
        // of the buffer and started again from zero.
        if (available < 0)
        {
            available += ringBufferSampleCount;
        }

        if (available == 0) return 0;

        // Safety: if we somehow fell far enough behind that the mic has
        // overwritten unread audio, skip ahead rather than reading garbage.
        if (available > ringBufferSampleCount)
        {
            Debug.LogWarning("[MicrophoneCapture] Ring buffer overrun - dropping stale audio.");
            lastReadPosition = writePosition;
            return 0;
        }

        if (lastReadPosition + available <= ringBufferSampleCount)
        {
            // Simple case: the new audio is one contiguous run.
            // GetData fills the array from the given offset, and the array's
            // own length determines how much is read - so we read into a
            // scratch buffer and copy out only what we need.
            micClip.GetData(scratchBuffer, lastReadPosition);
            Array.Copy(scratchBuffer, 0, readBuffer, 0, available);
        }
        else
        {
            // Wrapped case: read the tail of the buffer, then the head.
            int tailCount = ringBufferSampleCount - lastReadPosition;
            int headCount = available - tailCount;

            micClip.GetData(scratchBuffer, lastReadPosition);
            Array.Copy(scratchBuffer, 0, readBuffer, 0, tailCount);

            micClip.GetData(scratchBuffer, 0);
            Array.Copy(scratchBuffer, 0, readBuffer, tailCount, headCount);
        }

        lastReadPosition = writePosition;
        return available;
    }

    private void UpdateLevels(float[] samples, int count)
    {
        // RMS (root mean square) is the standard way to measure perceived
        // loudness of a block of audio: square every sample, average them,
        // then take the square root. Squaring makes negative samples count
        // the same as positive ones.
        double sumOfSquares = 0.0;

        for (int i = 0; i < count; i++)
        {
            float sample = samples[i];
            sumOfSquares += sample * sample;
        }

        CurrentRms = Mathf.Sqrt((float)(sumOfSquares / count));

        if (CurrentRms > PeakRms)
        {
            PeakRms = CurrentRms;
        }

        // Convert to decibels, which matches how loudness is actually
        // perceived and makes thresholds far easier to reason about in step 2.
        CurrentDb = CurrentRms > 0.0000001f
            ? Mathf.Max(MinDb, 20f * Mathf.Log10(CurrentRms))
            : MinDb;
    }

    /// <summary>
    /// Maps the current level onto 0..1 for display on a meter.
    /// </summary>
    public float GetNormalisedLevel(float floorDb = -60f)
    {
        return Mathf.InverseLerp(floorDb, 0f, CurrentDb);
    }
}
