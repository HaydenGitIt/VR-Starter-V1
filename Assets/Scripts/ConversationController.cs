using System;
using UnityEngine;

/// <summary>
/// Drives one turn of the conversation: listen, send, wait, play, listen again.
///
/// This is the piece that makes the parts into a loop. The VAD knows how to
/// find an utterance, the client knows how to reach the server, the player
/// knows how to make sound - none of them know about each other, and this
/// script is the only place that does.
///
/// THE MICROPHONE GATE IS THE IMPORTANT PART:
/// The Quest's speakers feed its own microphone. If the mic stays open while
/// Mel is talking, her voice gets detected as speech, transcribed, and sent
/// back to the server as though the trainee had said it - she would end up in
/// conversation with herself. The gate closes the moment an utterance is sent
/// and reopens only once her reply has finished PLAYING, which is later than
/// when it finished arriving.
/// </summary>
public class ConversationController : MonoBehaviour
{
    public enum LoopState
    {
        /// <summary>Not connected, or the scenario has not started.</summary>
        Waiting,

        /// <summary>Microphone open. The trainee's turn.</summary>
        Listening,

        /// <summary>Utterance sent. Waiting for the server.</summary>
        Processing,

        /// <summary>Mel is talking. Microphone closed.</summary>
        Speaking,

        /// <summary>The scenario has ended.</summary>
        Ended
    }

    [Header("References")]
    [SerializeField] private ConversationClient client;
    [SerializeField] private StreamingAudioPlayer player;
    [SerializeField] private VoiceActivityDetector detector;
    [SerializeField] private MicrophoneCapture microphone;
    [SerializeField] private PipelineConfig config;

    [Header("Behaviour")]
    [Tooltip("Start the scenario as soon as the server is ready. Useful for testing; " +
             "the real flow will trigger it deliberately once the trainee is oriented.")]
    [SerializeField] private bool beginOnReady = true;

    [Tooltip("Seconds to wait after Mel stops speaking before reopening the microphone. " +
             "A short pause stops the tail of her voice, or a room echo of it, being " +
             "picked up as the start of the trainee's reply.")]
    [SerializeField] private float pauseBeforeListening = 0.35f;

    // ---------------------------------------------------------------
    // PUBLIC STATE
    // ---------------------------------------------------------------

    public LoopState State { get; private set; } = LoopState.Waiting;

    /// <summary>Raised whenever the state changes, for UI and animation.</summary>
    public event Action<LoopState> OnStateChanged;

    /// <summary>The most recent verdict and emotion, for animation.</summary>
    public DeescalationVerdict LastVerdict { get; private set; } = DeescalationVerdict.Unknown;
    public ParentEmotion LastEmotion { get; private set; } = ParentEmotion.Unknown;

    public string LastTranscript { get; private set; } = "";
    public string LastReply { get; private set; } = "";

    // ---------------------------------------------------------------
    // INTERNAL
    // ---------------------------------------------------------------

    // Whether any audio arrived for the turn in progress. Determines how the
    // turn ends: if she spoke, we wait for playback to finish; if she did not
    // (a discarded utterance, or a failure ending), there is nothing to wait for.
    private bool audioThisTurn;

    private float reopenAt = -1f;

    // True once every required reference was found. Guards the subscribe and
    // unsubscribe below, which would otherwise run against nulls.
    private bool wired;

    private void Awake()
    {
        // Prefer the singleton over a scene search.
        //
        // A scene-local ConversationClient that has STOOD DOWN - because the
        // persistent one from Bootstrap already claimed Instance - is still
        // sitting in the scene and is still findable. FindAnyObjectByType
        // makes no promise about which of the two it returns, so roughly half
        // the time this would subscribe to the disabled copy and then wait
        // forever for a client that never connects, with nothing in the
        // Console to explain why Mel is silent.
        if (client == null) client = ConversationClient.Instance;
        if (client == null) client = FindInScene<ConversationClient>();

        if (player == null) player = FindInScene<StreamingAudioPlayer>();
        if (detector == null) detector = FindInScene<VoiceActivityDetector>();
        if (microphone == null) microphone = FindInScene<MicrophoneCapture>();

        // NAME THE MISSING PIECE, rather than saying something is missing.
        //
        // The old message listed all three dependencies whichever one was
        // absent, which sent you checking components that were perfectly fine.
        // The usual cause is a component that has lost its script reference:
        // the GameObject still sits in the Hierarchy looking entirely normal,
        // so there is nothing to notice without being told where to look.
        string missing = "";

        if (client == null) missing += "\n   - ConversationClient  (lives on the Bootstrap object)";
        if (player == null) missing += "\n   - StreamingAudioPlayer";
        if (detector == null) missing += "\n   - VoiceActivityDetector";

        if (missing.Length > 0)
        {
            Debug.LogError(
                "[ConversationController] Disabling myself. Could not find:" + missing +
                "\n\nIf that object IS in the Hierarchy, select it and look for a component " +
                "reading 'Missing (Mono Script)'. A lost script reference leaves the " +
                "GameObject looking normal while making the component invisible to code.", this);

            enabled = false;
            return;
        }

        wired = true;

        if (config == null && microphone != null) config = microphone.Config;
    }

    private void OnEnable()
    {
        if (!wired) return;

        client.OnReady += HandleReady;
        client.OnTags += HandleTags;
        client.OnTranscript += HandleTranscript;
        client.OnDiscarded += HandleDiscarded;
        client.OnReply += HandleReply;
        client.OnEnding += HandleEnding;
        client.OnTurnEnd += HandleTurnEnd;
        client.OnAudio += HandleAudioArrived;
        client.OnDisconnected += HandleDisconnected;

        player.OnSpeechFinished += HandleSpeechFinished;

        detector.OnUtteranceCaptured += HandleUtterance;
    }

    private void OnDisable()
    {
        // Awake gives up when a reference is missing, but Unity still calls
        // OnDisable - on the way out of Play mode if nothing else. Without
        // this guard that throws a NullReferenceException on top of the real
        // error, and the stack trace points here rather than at the cause,
        // which reads like a second, unrelated fault.
        if (!wired) return;

        client.OnReady -= HandleReady;
        client.OnTags -= HandleTags;
        client.OnTranscript -= HandleTranscript;
        client.OnDiscarded -= HandleDiscarded;
        client.OnReply -= HandleReply;
        client.OnEnding -= HandleEnding;
        client.OnTurnEnd -= HandleTurnEnd;
        client.OnAudio -= HandleAudioArrived;
        client.OnDisconnected -= HandleDisconnected;

        player.OnSpeechFinished -= HandleSpeechFinished;

        detector.OnUtteranceCaptured -= HandleUtterance;
    }

    private void Start()
    {
        // Closed until the scenario actually begins. Without this the trainee
        // could talk over the connection sequence and have it sent as turn one.
        detector.SetListening(false);
        SetState(LoopState.Waiting);
    }

    private void Update()
    {
        // Deferred reopening, so the tail of Mel's voice is not caught.
        if (reopenAt > 0f && Time.time >= reopenAt)
        {
            reopenAt = -1f;
            OpenMicrophone();
        }
    }

    // ---------------------------------------------------------------
    // STARTING
    // ---------------------------------------------------------------

    private void HandleReady()
    {
        if (beginOnReady)
        {
            BeginScenario();
        }
    }

    /// <summary>
    /// Turns automatic starting on or off.
    ///
    /// In the flat test scene the controller starts itself as soon as the
    /// server answers, which is convenient. In the real scene the
    /// ScenarioDirector decides, because the trainee needs a moment to arrive
    /// in the room before an agitated parent starts talking at them.
    /// </summary>
    public void SetAutoBegin(bool value)
    {
        beginOnReady = value;
    }

    /// <summary>
    /// Starts the scenario. Mel speaks first, so the microphone stays closed
    /// until her opening line has finished playing.
    /// </summary>
    public void BeginScenario()
    {
        audioThisTurn = false;
        detector.SetListening(false);

        SetState(LoopState.Speaking);
        client.Begin();
    }

    // ---------------------------------------------------------------
    // THE TRAINEE SPEAKS
    // ---------------------------------------------------------------

    private void HandleUtterance(float[] samples, AudioFormat format)
    {
        if (State != LoopState.Listening)
        {
            // Should not happen while the gate is working, but if it does,
            // dropping the utterance is far better than sending a second one
            // mid-turn and desynchronising the conversation.
            Debug.LogWarning($"[ConversationController] Utterance while {State}; ignored.");
            return;
        }

        // Close the gate IMMEDIATELY, not when her reply starts arriving.
        // There is a second or so of processing in between, and anything the
        // trainee says during it would otherwise be captured as a second turn.
        detector.SetListening(false);

        float[] toSend = samples;

        // The server expects the configured capture rate. If the device
        // refused it, resample rather than sending audio at the wrong rate -
        // that does not error, it just produces a bad transcript, which is a
        // miserable thing to track down later.
        int wanted = config != null ? config.captureSampleRate : format.sampleRate;

        if (format.sampleRate != wanted)
        {
            Debug.LogWarning($"[ConversationController] Resampling {format.sampleRate}Hz " +
                             $"-> {wanted}Hz before sending.");
            toSend = Resample(samples, format.sampleRate, wanted);
        }

        audioThisTurn = false;
        SetState(LoopState.Processing);

        client.SendUtterance(toSend);
    }

    // ---------------------------------------------------------------
    // MEL RESPONDS
    // ---------------------------------------------------------------

    private void HandleTags(DeescalationVerdict verdict, ParentEmotion emotion)
    {
        LastVerdict = verdict;
        LastEmotion = emotion;

        // Arrives before any audio, which is what lets the animation layer
        // start on the right emotion rather than a beat behind her voice.
    }

    private void HandleTranscript(string text) => LastTranscript = text;

    private void HandleReply(string text) => LastReply = text;

    private void HandleAudioArrived(float[] samples)
    {
        audioThisTurn = true;

        if (State != LoopState.Ended)
        {
            SetState(LoopState.Speaking);
        }
    }

    private void HandleDiscarded(string reason)
    {
        // Nothing intelligible was heard, so no turn was used and no reply is
        // coming. Reopen at once rather than leaving the trainee waiting for
        // a response that will never arrive.
        Debug.Log($"[ConversationController] {reason} Listening again.");
        ScheduleReopen();
    }

    private void HandleTurnEnd(bool ended)
    {
        if (ended || State == LoopState.Ended) return;

        // If she spoke, playback is still running and OnSpeechFinished will
        // reopen the microphone. If she did not, nothing else will fire, so
        // reopen here.
        if (!audioThisTurn)
        {
            ScheduleReopen();
        }
    }

    private void HandleSpeechFinished()
    {
        if (State == LoopState.Ended) return;

        ScheduleReopen();
    }

    private void HandleEnding(bool resolved)
    {
        SetState(LoopState.Ended);

        // Stay closed. The ending Timeline is about to play and nothing the
        // trainee says now belongs to the scenario.
        detector.SetListening(false);
        reopenAt = -1f;

        Debug.Log(resolved
            ? "[ConversationController] Resolved. Success Timeline should play."
            : "[ConversationController] Disengaged. Failure Timeline should play.");
    }

    private void HandleDisconnected()
    {
        if (State == LoopState.Ended) return;

        Debug.LogError("[ConversationController] Lost the server mid-scenario.");
        detector.SetListening(false);
        SetState(LoopState.Waiting);
    }

    // ---------------------------------------------------------------
    // MICROPHONE GATE
    // ---------------------------------------------------------------

    private void ScheduleReopen()
    {
        if (State == LoopState.Ended) return;

        reopenAt = Time.time + Mathf.Max(0f, pauseBeforeListening);
    }

    private void OpenMicrophone()
    {
        if (State == LoopState.Ended) return;

        detector.SetListening(true);
        SetState(LoopState.Listening);
    }

    private void SetState(LoopState next)
    {
        if (State == next) return;

        State = next;
        OnStateChanged?.Invoke(next);
    }

    // ---------------------------------------------------------------
    // RESAMPLING
    // ---------------------------------------------------------------

    /// <summary>
    /// Linear-interpolation resampling.
    ///
    /// Crude by audio standards, and entirely adequate here: speech
    /// recognition cares about the shape of formants, not about the artefacts
    /// a proper filter would remove. It exists only as a safety net for a
    /// microphone that refuses the requested rate - in normal operation the
    /// Quest gives us 16kHz and this never runs.
    /// </summary>
    public static float[] Resample(float[] source, int fromRate, int toRate)
    {
        if (source == null || source.Length == 0 || fromRate == toRate)
        {
            return source;
        }

        double ratio = toRate / (double)fromRate;
        int outputLength = Mathf.Max(1, (int)(source.Length * ratio));

        float[] result = new float[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double position = i / ratio;
            int index = (int)position;
            double fraction = position - index;

            float a = source[Mathf.Clamp(index, 0, source.Length - 1)];
            float b = source[Mathf.Clamp(index + 1, 0, source.Length - 1)];

            result[i] = (float)(a + (b - a) * fraction);
        }

        return result;
    }

    private static T FindInScene<T>() where T : UnityEngine.Object
    {
#if UNITY_6000_0_OR_NEWER
        // FindAnyObjectByType does not guarantee WHICH instance it returns when
        // several exist - that is fine here, because every type looked up this
        // way is meant to be unique in the scene, and it is faster than the
        // ordered alternatives.
        return FindAnyObjectByType<T>();
#elif UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<T>();
#else
        return FindObjectOfType<T>();
#endif
    }
}
