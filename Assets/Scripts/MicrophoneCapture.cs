using System;
using UnityEngine;

/// <summary>
/// Opens a microphone and continuously exposes newly-arrived samples plus a
/// loudness reading.
///
/// The audio format comes from PipelineConfig, not from constants here, so
/// changing STT engine to one wanting a different sample rate is an edit to
/// one asset rather than a hunt through code.
///
/// HOW UNITY'S MICROPHONE API WORKS (it is unusual):
/// Microphone.Start() returns an AudioClip that behaves as a RING BUFFER.
/// The device writes into it continuously and wraps to the start when full.
/// Nothing is delivered to you - you ask Microphone.GetPosition() where the
/// write head is, and read the region since your last read.
/// </summary>
public class MicrophoneCapture : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("Shared pipeline settings. Sample rate and channel count come from here.")]
    [SerializeField] private PipelineConfig config;

    [Header("Device Selection")]
    [Tooltip("Picks the first device whose name contains this text. For Quest over Link, try 'Oculus'. " +
             "Leave blank to use the system default.")]
    [SerializeField] private string preferredDeviceKeyword = "Oculus";

    [Tooltip("If the preferred device is missing, use the system default rather than failing.")]
    [SerializeField] private bool fallBackToDefaultDevice = true;

    [Header("Buffering")]
    [Tooltip("Ring buffer length in seconds. Only needs to exceed the longest gap between reads.")]
    [SerializeField] private int ringBufferSeconds = 2;

    [Header("Behaviour")]
    [SerializeField] private bool captureOnStart = true;

    // ---------------------------------------------------------------
    // PUBLIC STATE
    // ---------------------------------------------------------------

    public bool IsCapturing { get; private set; }
    public string ActiveDeviceName { get; private set; } = "";
    public float CurrentRms { get; private set; }
    public float CurrentDb { get; private set; } = MinDb;
    public float PeakRms { get; private set; }

    /// <summary>
    /// The format actually in use. May differ from the configured format if
    /// the device refused it, so downstream code should read this rather
    /// than assuming the config value took effect.
    /// </summary>
    public AudioFormat Format { get; private set; }

    public int SampleRate => Format.sampleRate;

    public PipelineConfig Config => config;

    /// <summary>
    /// Raised once per frame when new audio has arrived.
    /// The buffer is reused between calls - copy anything you need to keep.
    /// </summary>
    public event Action<float[], int> OnSamplesCaptured;

    /// <summary>
    /// Raised once the device is open and the true format is known. Anything
    /// sizing buffers from the sample rate should wait for this rather than
    /// reading the rate in Start(), because component start order is not
    /// guaranteed and the rate may still be the unvalidated config value.
    /// </summary>
    public event Action<AudioFormat> OnCaptureStarted;

    public const float MinDb = -80f;

    // ---------------------------------------------------------------
    // INTERNAL
    // ---------------------------------------------------------------

    private AudioClip micClip;
    private int ringBufferSampleCount;
    private int lastReadPosition;

    // Pre-allocated. Allocating per frame creates garbage, and GC spikes
    // in VR show up as dropped frames.
    private float[] readBuffer;
    private float[] chunkBuffer;

    // Reading in fixed-size chunks avoids two problems at once: GetData
    // fills whatever array you hand it (so a ring-sized array copies far
    // more than needed), and a variable-sized array would reallocate every
    // frame as frame times fluctuate.
    private const int ChunkSize = 1024;

    private void Start()
    {
        if (config == null)
        {
            Debug.LogError("[MicrophoneCapture] No PipelineConfig assigned. Create one via " +
                           "Create > De-escalation Sim > Pipeline Config and assign it in the Inspector.");
            enabled = false;
            return;
        }

        if (captureOnStart)
        {
            StartCapture();
        }
    }

    private void OnDestroy() => StopCapture();
    private void OnApplicationQuit() => StopCapture();

    public static string[] ListDevices() => Microphone.devices;

    public void StartCapture()
    {
        if (IsCapturing) return;
        if (config == null) return;

        string[] devices = Microphone.devices;

        if (devices.Length == 0)
        {
            Debug.LogError("[MicrophoneCapture] No microphone devices found. If using Quest Link, check " +
                           "Link is running and Windows Settings > Privacy > Microphone allows desktop apps.");
            return;
        }

        string chosen = ChooseDevice(devices);
        if (chosen == null) return;

        int requestedRate = config.captureSampleRate;

        // Ask what the device supports. Many report 0,0 meaning "anything".
        Microphone.GetDeviceCaps(chosen, out int minFreq, out int maxFreq);

        int actualRate = requestedRate;
        if (maxFreq > 0)
        {
            actualRate = Mathf.Clamp(requestedRate, minFreq, maxFreq);

            if (actualRate != requestedRate)
            {
                Debug.LogWarning($"[MicrophoneCapture] Device '{chosen}' cannot do {requestedRate}Hz " +
                                 $"(supports {minFreq}-{maxFreq}); using {actualRate}Hz. " +
                                 "Audio will need resampling before it reaches the STT engine.");
            }
        }

        micClip = Microphone.Start(chosen, true, ringBufferSeconds, actualRate);

        if (micClip == null)
        {
            Debug.LogError($"[MicrophoneCapture] Microphone.Start failed for '{chosen}'.");
            return;
        }

        ringBufferSampleCount = micClip.samples;
        lastReadPosition = 0;

        readBuffer = new float[ringBufferSampleCount];
        chunkBuffer = new float[ChunkSize];

        ActiveDeviceName = chosen;

        // Report the clip's real rate, which is authoritative.
        Format = new AudioFormat(micClip.frequency, config.captureChannels);
        IsCapturing = true;

        Debug.Log($"[MicrophoneCapture] Capturing from '{chosen}' at {Format} " +
                  $"({ringBufferSeconds}s ring = {ringBufferSampleCount} samples).");

        OnCaptureStarted?.Invoke(Format);
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

    public void ResetPeak() => PeakRms = 0f;

    private string ChooseDevice(string[] devices)
    {
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
                Debug.LogError($"[MicrophoneCapture] No device matching '{preferredDeviceKeyword}', " +
                               "and fallback is disabled.");
                return null;
            }

            Debug.LogWarning($"[MicrophoneCapture] No device matching '{preferredDeviceKeyword}'. " +
                             $"Using '{devices[0]}'.");
        }

        return devices[0];
    }

    private void Update()
    {
        if (!IsCapturing || micClip == null) return;

        int newSampleCount = ReadNewSamples();
        if (newSampleCount <= 0) return;

        UpdateLevels(readBuffer, newSampleCount);

        OnSamplesCaptured?.Invoke(readBuffer, newSampleCount);
    }

    /// <summary>
    /// Copies everything written since the last read into readBuffer.
    /// Returns how many samples were read.
    /// </summary>
    private int ReadNewSamples()
    {
        int writePosition = Microphone.GetPosition(ActiveDeviceName);

        // Can briefly return -1 before the device is ready.
        if (writePosition < 0) return 0;

        int available = writePosition - lastReadPosition;

        // Negative means the write head wrapped past the end and restarted.
        if (available < 0)
        {
            available += ringBufferSampleCount;
        }

        if (available == 0) return 0;

        if (available > ringBufferSampleCount)
        {
            Debug.LogWarning("[MicrophoneCapture] Ring buffer overrun - dropping stale audio.");
            lastReadPosition = writePosition;
            return 0;
        }

        // Read in fixed chunks. GetData wraps around the clip automatically
        // when a read runs past the end, so the offset arithmetic stays simple.
        int copied = 0;
        int readPos = lastReadPosition;

        while (copied < available)
        {
            micClip.GetData(chunkBuffer, readPos);

            int toCopy = Mathf.Min(ChunkSize, available - copied);
            Array.Copy(chunkBuffer, 0, readBuffer, copied, toCopy);

            copied += toCopy;
            readPos = (readPos + toCopy) % ringBufferSampleCount;
        }

        lastReadPosition = writePosition;
        return available;
    }

    private void UpdateLevels(float[] samples, int count)
    {
        // RMS: square every sample, average, square-root. Squaring makes
        // negative samples count the same as positive ones.
        double sumOfSquares = 0.0;

        for (int i = 0; i < count; i++)
        {
            float s = samples[i];
            sumOfSquares += s * s;
        }

        CurrentRms = Mathf.Sqrt((float)(sumOfSquares / count));

        if (CurrentRms > PeakRms) PeakRms = CurrentRms;

        CurrentDb = RmsToDb(CurrentRms);
    }

    /// <summary>
    /// Shared conversion, so the detector and the meter cannot drift apart
    /// in how they measure loudness.
    /// </summary>
    public static float RmsToDb(float rms)
    {
        return rms > 0.0000001f
            ? Mathf.Max(MinDb, 20f * Mathf.Log10(rms))
            : MinDb;
    }

    public static float ComputeDb(float[] samples, int count)
    {
        if (count <= 0) return MinDb;

        double sumOfSquares = 0.0;
        for (int i = 0; i < count; i++)
        {
            float s = samples[i];
            sumOfSquares += s * s;
        }

        return RmsToDb(Mathf.Sqrt((float)(sumOfSquares / count)));
    }

    public float GetNormalisedLevel(float floorDb = -60f)
    {
        return Mathf.InverseLerp(floorDb, 0f, CurrentDb);
    }
}
