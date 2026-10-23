using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Talks to the Python conversation server.
///
/// This is the only script that knows the server exists. Everything else in
/// the project subscribes to its events, so if the transport ever changes -
/// a different protocol, a different host, engines running in-process - only
/// this file changes.
///
/// THE PROTOCOL, BRIEFLY:
///   We send  TEXT frames  = JSON commands (begin, text, reset, summary)
///   We send  BINARY frames = one complete utterance, float32 little-endian
///   We get   TEXT frames  = JSON state updates
///   We get   BINARY frames = one synthesised sentence, float32 little-endian
///
/// ORDERING MATTERS: the server always sends "tags" before any audio for that
/// turn. That is what lets us detect a third strike and start the failure
/// Timeline with no speech to interrupt.
/// </summary>
public class ConversationClient : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField] private PipelineConfig config;

    [Header("Behaviour")]
    [Tooltip("Check /health and connect as soon as this component starts.")]
    [SerializeField] private bool connectOnStart = true;

    [Tooltip("How many times to retry if the server is not reachable yet. " +
             "The server takes ~20s to warm its engines on a cold start, so pressing Play " +
             "immediately after launching it will otherwise fail for no good reason.")]
    [SerializeField] private int connectionAttempts = 10;

    [Tooltip("Seconds between retries.")]
    [SerializeField] private float retryDelay = 3f;

    [Tooltip("Log every frame sent and received. Useful while building, noisy afterwards.")]
    [SerializeField] private bool verboseLogging = true;

    // ---------------------------------------------------------------
    // EVENTS
    // Other scripts listen to these rather than touching the socket.
    // ---------------------------------------------------------------

    /// <summary>Connected and the server has sent its opening handshake.</summary>
    public event Action OnReady;

    /// <summary>The verdict and emotion for this turn. ALWAYS arrives before audio.</summary>
    public event Action<DeescalationVerdict, ParentEmotion> OnTags;

    /// <summary>What the server heard the trainee say.</summary>
    public event Action<string> OnTranscript;

    /// <summary>Nothing intelligible was heard. No turn was used.</summary>
    public event Action<string> OnDiscarded;

    /// <summary>One synthesised sentence, as float samples at the playback rate.</summary>
    public event Action<float[]> OnAudio;

    /// <summary>Mel's full line as text, for the transcript log.</summary>
    public event Action<string> OnReply;

    /// <summary>The scenario has ended. True = resolved, false = disengaged.</summary>
    public event Action<bool> OnEnding;

    /// <summary>Counters after a completed turn.</summary>
    public event Action<int, DeescalationVerdict> OnTurnComplete;

    /// <summary>No more audio is coming for this turn. Safe to reopen the mic.</summary>
    public event Action<bool> OnTurnEnd;

    public event Action<string> OnError;
    public event Action OnDisconnected;

    // ---------------------------------------------------------------
    // STATE
    // ---------------------------------------------------------------

    public bool IsConnected => socket != null && socket.State == WebSocketState.Open;

    /// <summary>Playback rate the server reported. Trust this over the local config.</summary>
    public int PlaybackSampleRate { get; private set; }

    /// <summary>
    /// True once the server has sent its handshake, not merely once the socket
    /// opened.
    ///
    /// The difference matters: an open socket says nothing about whether the
    /// server has finished warming its engines, and the handshake is what
    /// carries the playback sample rate and the scenario rules. Anything
    /// deciding whether it is safe to start should check THIS, not IsConnected.
    /// </summary>
    public bool IsReady => handshakeReceived;

    private WebSocket socket;
    private bool handshakeReceived;

    // ---------------------------------------------------------------
    // MESSAGE SHAPES
    //
    // Unity's built-in JsonUtility cannot deserialise into a dictionary, so
    // we use one class carrying every field the server might send. Fields not
    // present in a given message are simply left at their defaults - which is
    // fine, because we switch on "type" before reading anything else.
    // ---------------------------------------------------------------

    [Serializable]
    private class ServerMessage
    {
        public string type;

        // ready
        public int capture_sample_rate;
        public int playback_sample_rate;
        public int max_turns;
        public int strikes_allowed;
        public int progress_to_resolve;

        // tags
        public string verdict;
        public string emotion;

        // transcript / reply
        public string text;

        // discarded
        public string reason;

        // ending
        public string outcome;

        // turn_complete
        public int turn;
        public float latency;

        // turn_end
        public bool ended;

        // error
        public string message;
    }

    [Serializable]
    private class Command
    {
        public string type;
        public string content;

        public Command(string type, string content = null)
        {
            this.type = type;
            this.content = content;
        }
    }

    // ---------------------------------------------------------------
    // LIFECYCLE
    // ---------------------------------------------------------------

    /// <summary>
    /// The live client. There must be exactly one, because two would each
    /// open their own socket and start their own scenario on the server, and
    /// other scripts finding "a" client could end up talking to different ones.
    /// </summary>
    public static ConversationClient Instance { get; private set; }

    private void Awake()
    {
        // The real client lives on the persistent Bootstrap object, so it can
        // start connecting during the splash and introduction and hide the
        // server's warm-up.
        //
        // A second one in a scene is not an error though - it is how you press
        // Play directly in the Deescalation scene while building, without
        // starting from Bootstrap every time. When the persistent one already
        // exists, this one simply stands down.
        if (Instance != null && Instance != this)
        {
            Debug.Log("[ConversationClient] A client already exists (probably the " +
                      "persistent one from Bootstrap). This scene-local copy is standing down.");

            enabled = false;
            return;
        }

        Instance = this;
    }

    private void Start()
    {
        if (!enabled) return;

        if (config == null)
        {
            Debug.LogError("[ConversationClient] No PipelineConfig assigned.");
            enabled = false;
            return;
        }

        PlaybackSampleRate = config.playbackSampleRate;

        if (connectOnStart)
        {
            StartCoroutine(CheckHealthThenConnect());
        }
    }

    private void Update()
    {
        // NativeWebSocket receives on a background thread and queues messages.
        // This hands them to the main thread, where Unity APIs are legal to
        // call. Without it, no callback ever fires.
#if !UNITY_WEBGL || UNITY_EDITOR
        socket?.DispatchMessageQueue();
#endif
    }

    private async void OnDestroy()
    {
        // Only clear the static reference if this object is the one holding it.
        // A stood-down duplicate being destroyed must not orphan the real client.
        if (Instance == this) Instance = null;

        await Disconnect();
    }

    private async void OnApplicationQuit()
    {
        await Disconnect();
    }

    // ---------------------------------------------------------------
    // CONNECTING
    // ---------------------------------------------------------------

    /// <summary>
    /// Checks /health before opening the socket.
    ///
    /// Worth doing rather than connecting blind. The server takes 15-20
    /// seconds to warm its engines at startup, and a clear "server not ready"
    /// in the Console beats a WebSocket that fails for reasons that could be
    /// anything. The health response also reports the audio formats, so a
    /// sample rate mismatch is caught here rather than showing up later as
    /// speech at the wrong pitch.
    /// </summary>
    private IEnumerator CheckHealthThenConnect()
    {
        string url = config.HealthUrl;

        for (int attempt = 1; attempt <= Mathf.Max(1, connectionAttempts); attempt++)
        {
            if (verboseLogging)
            {
                Debug.Log($"[ConversationClient] Checking {url} (attempt {attempt})");
            }

            bool succeeded = false;

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.CeilToInt(config.connectionTimeout);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    succeeded = ReadHealth(request.downloadHandler.text);
                }
                else if (attempt >= connectionAttempts)
                {
                    Debug.LogError(
                        $"[ConversationClient] Server not reachable at {url}\n" +
                        $"   {request.error}\n" +
                        "   Is server.py running? A cold start takes ~20s to warm the engines,\n" +
                        "   and it will not answer until it prints 'Ready.'");

                    OnError?.Invoke("Server not reachable.");
                    yield break;
                }
                else
                {
                    Debug.LogWarning(
                        $"[ConversationClient] Not reachable yet ({request.error}). " +
                        $"Retrying in {retryDelay}s...");
                }
            }

            if (succeeded)
            {
                Connect();
                yield break;
            }

            yield return new WaitForSeconds(retryDelay);
        }

        Debug.LogError("[ConversationClient] Gave up waiting for the server.");
        OnError?.Invoke("Server did not become ready.");
    }

    /// <summary>
    /// Reads the health response. Returns true if the server is warmed and
    /// ready to take a conversation.
    /// </summary>
    private bool ReadHealth(string json)
    {
        HealthResponse health = JsonUtility.FromJson<HealthResponse>(json);

        if (health == null)
        {
            Debug.LogWarning("[ConversationClient] Health response could not be parsed.");
            return false;
        }

        if (health.playback != null)
        {
            PlaybackSampleRate = health.playback.sample_rate;

            if (PlaybackSampleRate != config.playbackSampleRate)
            {
                Debug.LogWarning(
                    $"[ConversationClient] Sample rate mismatch. Server plays back at " +
                    $"{PlaybackSampleRate}Hz, PipelineConfig says {config.playbackSampleRate}Hz. " +
                    "Using the server's value. Update the config asset to match, or audio " +
                    "will play at the wrong pitch anywhere that reads the config directly.");
            }
        }

        if (health.capture != null && health.capture.sample_rate != config.captureSampleRate)
        {
            Debug.LogWarning(
                $"[ConversationClient] Capture rate mismatch. Server expects " +
                $"{health.capture.sample_rate}Hz, we capture at {config.captureSampleRate}Hz. " +
                "Speech recognition will be unreliable until these agree.");
        }

        if (!health.ready)
        {
            Debug.Log("[ConversationClient] Server is up but still warming its engines...");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs the full health check and connect sequence. This is what the
    /// Reconnect button should call - connecting the socket directly skips
    /// the health check, and then a sample rate mismatch would go unnoticed.
    /// </summary>
    public void ConnectWithHealthCheck()
    {
        StopAllCoroutines();
        StartCoroutine(CheckHealthThenConnect());
    }

    [Serializable] private class HealthFormat { public int sample_rate; public int channels; public string format; }
    [Serializable] private class HealthResponse { public bool ready; public HealthFormat capture; public HealthFormat playback; }

    public async void Connect()
    {
        if (IsConnected) return;

        string url = config.ConversationUrl;
        Debug.Log($"[ConversationClient] Connecting to {url}");

        socket = new WebSocket(url);

        socket.OnOpen += () =>
        {
            Debug.Log("[ConversationClient] Connected.");
        };

        socket.OnError += (error) =>
        {
            Debug.LogError($"[ConversationClient] Socket error: {error}");
            OnError?.Invoke(error);
        };

        socket.OnClose += (code) =>
        {
            Debug.Log($"[ConversationClient] Closed ({code}).");
            handshakeReceived = false;
            OnDisconnected?.Invoke();
        };

        socket.OnMessage += HandleMessage;

        await socket.Connect();
    }

    public async Task Disconnect()
    {
        if (socket == null) return;

        if (socket.State == WebSocketState.Open)
        {
            await socket.Close();
        }

        socket = null;
    }

    // ---------------------------------------------------------------
    // RECEIVING
    // ---------------------------------------------------------------

    private void HandleMessage(byte[] data)
    {
        // Binary frames are audio. We distinguish them from JSON by looking
        // at the first byte: JSON from this server always begins with '{'.
        //
        // This is safe because the audio is float32 PCM, and a float whose
        // first byte is 0x7B ('{') would be a wildly out-of-range sample -
        // roughly 1.6e+37 - which cannot occur in normalised audio.
        if (data.Length > 0 && data[0] != (byte)'{')
        {
            HandleAudioFrame(data);
            return;
        }

        string json = Encoding.UTF8.GetString(data);
        HandleJsonFrame(json);
    }

    private void HandleAudioFrame(byte[] data)
    {
        // float32 little-endian, which is also the layout Unity uses, so this
        // is a straight reinterpretation with no conversion needed.
        int sampleCount = data.Length / 4;
        float[] samples = new float[sampleCount];

        Buffer.BlockCopy(data, 0, samples, 0, sampleCount * 4);

        if (verboseLogging)
        {
            Debug.Log($"[ConversationClient] audio: {sampleCount / (float)PlaybackSampleRate:F2}s");
        }

        OnAudio?.Invoke(samples);
    }

    private void HandleJsonFrame(string json)
    {
        ServerMessage message;

        try
        {
            message = JsonUtility.FromJson<ServerMessage>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ConversationClient] Could not parse: {json}\n{e.Message}");
            return;
        }

        if (message == null || string.IsNullOrEmpty(message.type)) return;

        switch (message.type)
        {
            case "ready":
                PlaybackSampleRate = message.playback_sample_rate;
                handshakeReceived = true;

                Debug.Log(
                    $"[ConversationClient] Ready. Playback {PlaybackSampleRate}Hz. " +
                    $"Reach her {message.progress_to_resolve}x to resolve, " +
                    $"fail {message.strikes_allowed}x and she leaves, " +
                    $"max {message.max_turns} turns.");

                OnReady?.Invoke();
                break;

            case "tags":
                OnTags?.Invoke(ParseVerdict(message.verdict), ParseEmotion(message.emotion));
                break;

            case "transcript":
                if (verboseLogging) Debug.Log($"[ConversationClient] heard: \"{message.text}\"");
                OnTranscript?.Invoke(message.text);
                break;

            case "discarded":
                Debug.Log($"[ConversationClient] discarded: {message.reason}");
                OnDiscarded?.Invoke(message.reason);
                break;

            case "reply":
                Debug.Log($"[ConversationClient] MEL: {message.text}");
                OnReply?.Invoke(message.text);
                break;

            case "ending":
                bool resolved = message.outcome == "resolved";
                Debug.Log($"[ConversationClient] ENDING: {message.outcome}");
                OnEnding?.Invoke(resolved);
                break;

            case "turn_complete":
                OnTurnComplete?.Invoke(message.turn, ParseVerdict(message.verdict));
                break;

            case "turn_end":
                OnTurnEnd?.Invoke(message.ended);
                break;

            case "reset_complete":
                Debug.Log("[ConversationClient] Session reset.");
                break;

            case "summary":
                // Handled by the debrief screen in a later section.
                break;

            case "error":
                Debug.LogError($"[ConversationClient] Server error: {message.message}");
                OnError?.Invoke(message.message);
                break;
        }
    }

    // ---------------------------------------------------------------
    // SENDING
    // ---------------------------------------------------------------

    /// <summary>Starts the scenario. Mel speaks first.</summary>
    public async void Begin()
    {
        await SendCommand(new Command("begin"));
    }

    /// <summary>Sends a typed line. For testing without a microphone.</summary>
    public async void SendText(string content)
    {
        await SendCommand(new Command("text", content));
    }

    /// <summary>Abandons the current run and starts a fresh one.</summary>
    public async void ResetSession()
    {
        await SendCommand(new Command("reset"));
    }

    /// <summary>Asks for the debrief data.</summary>
    public async void RequestSummary()
    {
        await SendCommand(new Command("summary"));
    }

    /// <summary>
    /// Sends one complete utterance.
    ///
    /// The whole thing goes in a single frame - our VAD has already found the
    /// start and end, so there is nothing to be gained by streaming it up.
    /// </summary>
    public async void SendUtterance(float[] samples)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("[ConversationClient] Not connected; utterance dropped.");
            return;
        }

        byte[] bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        if (verboseLogging)
        {
            Debug.Log($"[ConversationClient] sending {samples.Length / (float)config.captureSampleRate:F2}s");
        }

        await socket.Send(bytes);
    }

    private async Task SendCommand(Command command)
    {
        if (!IsConnected)
        {
            Debug.LogWarning($"[ConversationClient] Not connected; '{command.type}' dropped.");
            return;
        }

        string json = JsonUtility.ToJson(command);

        if (verboseLogging)
        {
            Debug.Log($"[ConversationClient] sending {json}");
        }

        await socket.SendText(json);
    }

    // ---------------------------------------------------------------
    // PARSING
    //
    // The server speaks in strings; the rest of the project uses enums. This
    // is the boundary. Unknown values become Unknown rather than throwing,
    // because a prompt change on the server should never crash the headset.
    // ---------------------------------------------------------------

    private static DeescalationVerdict ParseVerdict(string value)
    {
        switch (value)
        {
            case "deescalated": return DeescalationVerdict.Deescalated;
            case "neutral": return DeescalationVerdict.Neutral;
            default: return DeescalationVerdict.Unknown;
        }
    }

    private static ParentEmotion ParseEmotion(string value)
    {
        switch (value)
        {
            case "agitated": return ParentEmotion.Agitated;
            case "distressed": return ParentEmotion.Distressed;
            case "softening": return ParentEmotion.Softening;
            case "calm": return ParentEmotion.Calm;
            default: return ParentEmotion.Unknown;
        }
    }
}
