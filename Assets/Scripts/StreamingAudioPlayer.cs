using System;
using UnityEngine;

/// <summary>
/// Plays the audio chunks arriving from the server as one continuous stream.
///
/// WHY NOT JUST QUEUE AUDIOCLIPS:
/// The obvious approach - make an AudioClip per sentence and PlayOneShot them
/// in order - produces an audible gap or click at every join, because clips
/// can only start on a frame boundary and the audio system is running at its
/// own rate underneath. Mel's replies arrive as several sentences, so those
/// joins happen constantly.
///
/// Instead we create ONE looping AudioClip whose samples are supplied by a
/// callback, and feed it from a ring buffer. The audio system pulls whatever
/// is available, so consecutive sentences join sample-perfectly and a late
/// chunk simply produces a moment of silence rather than a click.
///
/// THREADING:
/// OnAudioRead runs on Unity's AUDIO thread, not the main thread. Unity APIs
/// are illegal there and blocking is unacceptable. The ring buffer is
/// therefore lock-free: exactly one writer (main thread, appending received
/// chunks) and one reader (audio thread, consuming them), communicating only
/// through two integer positions. Integer reads and writes are atomic, so no
/// lock is needed and the audio thread is never made to wait.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class StreamingAudioPlayer : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField] private PipelineConfig config;

    [Header("Buffer")]
    [Tooltip("Seconds of audio the ring buffer can hold. Must comfortably exceed the " +
             "longest reply, since chunks arrive faster than they play back.")]
    [SerializeField] private float bufferSeconds = 30f;

    [Tooltip("Seconds of silence after the buffer empties before playback is " +
             "considered finished. Guards against the brief gaps between sentences " +
             "being mistaken for the end of her turn.")]
    [SerializeField] private float silenceBeforeFinished = 0.25f;

    // ---------------------------------------------------------------
    // PUBLIC STATE
    // ---------------------------------------------------------------

    /// <summary>True while there is audio buffered or recently played.</summary>
    public bool IsSpeaking { get; private set; }

    /// <summary>Seconds of audio waiting to be played.</summary>
    public float BufferedSeconds => Available / (float)sampleRate;

    /// <summary>Raised when she starts speaking.</summary>
    public event Action OnSpeechStarted;

    /// <summary>
    /// Raised once the buffer has been empty for silenceBeforeFinished.
    /// This is the cue to reopen the microphone - not the arrival of the last
    /// chunk, which happens well before the audio has finished playing.
    /// </summary>
    public event Action OnSpeechFinished;

    // ---------------------------------------------------------------
    // INTERNAL
    // ---------------------------------------------------------------

    private AudioSource audioSource;
    private AudioClip streamClip;

    private float[] ring;
    private int ringLength;
    private int sampleRate;

    // writePosition is touched ONLY by the main thread.
    // readPosition is touched ONLY by the audio thread.
    // Neither ever writes the other's variable, which is what makes this safe
    // without locking.
    private volatile int writePosition;
    private volatile int readPosition;

    // Set by the main thread, cleared by the audio thread. See Flush().
    private volatile bool flushRequested;

    private float silentFor;
    private bool everReceived;

    private int Available
    {
        get
        {
            int diff = writePosition - readPosition;
            return diff < 0 ? diff + ringLength : diff;
        }
    }

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();

        if (config == null)
        {
            Debug.LogError("[StreamingAudioPlayer] No PipelineConfig assigned.");
            enabled = false;
            return;
        }

        sampleRate = config.playbackSampleRate;

        ringLength = Mathf.CeilToInt(bufferSeconds * sampleRate);
        ring = new float[ringLength];

        CreateStreamingClip();
    }

    /// <summary>
    /// Builds the looping clip whose samples come from OnAudioRead.
    ///
    /// The clip is one second long and loops forever. Its contents are
    /// irrelevant - every time the audio system needs samples it calls back
    /// into us, so the clip is really just a handle onto the callback.
    /// </summary>
    private void CreateStreamingClip()
    {
        streamClip = AudioClip.Create(
            name: "MelStream",
            lengthSamples: sampleRate,      // one second, looped
            channels: 1,
            frequency: sampleRate,
            stream: true,
            pcmreadercallback: OnAudioRead
        );

        audioSource.clip = streamClip;
        audioSource.loop = true;
        audioSource.playOnAwake = false;
    }

    private void Start()
    {
        // Start immediately and never stop. With nothing buffered the callback
        // supplies silence, so this costs nothing and removes any chance of a
        // missed first chunk while the source spins up.
        audioSource.Play();

        WarnIfNotSpatialised();
    }

    /// <summary>
    /// In VR, Mel's voice must come FROM Mel. A 2D AudioSource plays her
    /// equally in both ears regardless of where the trainee is looking, which
    /// destroys the sense that a person is standing in front of them - and
    /// this scenario is entirely about a person standing in front of them.
    ///
    /// Only a warning, because a flat test scene has no character to attach
    /// to and 2D audio is perfectly reasonable there.
    /// </summary>
    private void WarnIfNotSpatialised()
    {
        if (audioSource.spatialBlend < 0.5f)
        {
            Debug.LogWarning(
                "[StreamingAudioPlayer] AudioSource is 2D (Spatial Blend " +
                $"{audioSource.spatialBlend:F1}). Fine for flat testing, but in the " +
                "scenario scene this component should sit on Mel's head with Spatial " +
                "Blend at 1.0, or her voice will not appear to come from her.");
        }
    }

    // ---------------------------------------------------------------
    // RECEIVING (main thread)
    // ---------------------------------------------------------------

    /// <summary>
    /// Appends one chunk from the server. Call this from OnAudio.
    /// </summary>
    public void Enqueue(float[] samples)
    {
        if (samples == null || samples.Length == 0) return;
        if (ring == null) return;

        if (samples.Length > ringLength)
        {
            Debug.LogError(
                $"[StreamingAudioPlayer] Chunk of {samples.Length} samples exceeds the " +
                $"{ringLength} sample buffer. Raise Buffer Seconds.");
            return;
        }

        // Drop the chunk rather than overwrite audio that has not played yet.
        // Overwriting would corrupt speech already queued, which sounds far
        // worse than a missing sentence.
        if (Available + samples.Length >= ringLength)
        {
            Debug.LogWarning("[StreamingAudioPlayer] Buffer full - chunk dropped.");
            return;
        }

        int position = writePosition;

        for (int i = 0; i < samples.Length; i++)
        {
            ring[position] = samples[i];
            position++;

            if (position >= ringLength) position = 0;
        }

        // Publish the new position only after every sample is in place, so
        // the audio thread can never read a half-written chunk.
        writePosition = position;

        if (!IsSpeaking)
        {
            IsSpeaking = true;
            silentFor = 0f;
            OnSpeechStarted?.Invoke();
        }

        everReceived = true;
    }

    /// <summary>
    /// Discards everything buffered and stops immediately.
    ///
    /// Used when the scenario cuts to an ending Timeline - any audio still
    /// queued belongs to a turn that no longer matters, and hearing Mel
    /// talking over her own exit animation would be worse than silence.
    ///
    /// NOTE ON HOW THIS IS DONE:
    /// The obvious implementation is "readPosition = writePosition", and it
    /// is wrong. This whole buffer is safe without locks only because each
    /// thread owns exactly one position variable and never writes the other's.
    /// Assigning readPosition from the main thread breaks that: the audio
    /// thread may be mid-callback and will write its own position back
    /// afterwards, undoing the flush - or worse, leave writePosition behind
    /// readPosition, which makes Available compute as nearly a full buffer of
    /// audio that does not exist.
    ///
    /// So we raise a flag instead, and let the audio thread do the reset the
    /// next time it runs. That keeps ownership intact.
    /// </summary>
    public void Flush()
    {
        flushRequested = true;

        IsSpeaking = false;
        silentFor = 0f;
    }

    private void Update()
    {
        if (!IsSpeaking) return;

        if (Available > 0)
        {
            silentFor = 0f;
            return;
        }

        // Buffer is empty. Wait a moment before declaring her finished - the
        // gaps between sentences are only 90ms, and treating one of those as
        // the end of her turn would reopen the microphone mid-sentence.
        silentFor += Time.deltaTime;

        if (silentFor >= silenceBeforeFinished)
        {
            IsSpeaking = false;
            silentFor = 0f;
            OnSpeechFinished?.Invoke();
        }
    }

    // ---------------------------------------------------------------
    // PLAYBACK (AUDIO THREAD - be careful here)
    // ---------------------------------------------------------------

    /// <summary>
    /// Called by Unity's audio system whenever it needs more samples.
    ///
    /// Runs on the audio thread. No Unity API calls, no allocation, no
    /// locking, no logging. Anything slow here produces audible crackling.
    /// </summary>
    private void OnAudioRead(float[] data)
    {
        // Honour a flush requested by the main thread. Doing the reset HERE
        // rather than there is what keeps readPosition owned by this thread
        // alone, which is what makes the whole buffer safe without locking.
        if (flushRequested)
        {
            readPosition = writePosition;
            flushRequested = false;
        }

        int position = readPosition;
        int available = writePosition - position;

        if (available < 0) available += ringLength;

        int toCopy = Mathf.Min(data.Length, available);

        for (int i = 0; i < toCopy; i++)
        {
            data[i] = ring[position];
            position++;

            if (position >= ringLength) position = 0;
        }

        // Underrun, or simply nothing to say - fill the rest with silence.
        for (int i = toCopy; i < data.Length; i++)
        {
            data[i] = 0f;
        }

        readPosition = position;
    }

    // ---------------------------------------------------------------
    // DIAGNOSTICS
    // ---------------------------------------------------------------

    public bool HasEverReceivedAudio => everReceived;
    public int BufferCapacitySamples => ringLength;
    public int BufferedSamples => Available;
}
