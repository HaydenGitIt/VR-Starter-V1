using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Streaming speech-to-text over the ElevenLabs Scribe realtime WebSocket.
    /// Taps the MicrophoneRecorder's capture stream and uploads audio while
    /// the user is still talking, so partial transcripts arrive live and the
    /// final text lands ~150 ms after the utterance ends instead of after a
    /// full record-then-upload round-trip.
    ///
    /// Add next to the MicrophoneRecorder. Events (fired on the main thread):
    ///  - OnPartialTranscript — live text while speaking, for UI feedback;
    ///  - OnTranscribed — committed text of the finished utterance. Same
    ///    contract as SpeechToTextPipeline.OnTranscribed; when this component
    ///    is present the conversation logic should subscribe here and the
    ///    batch pipeline is not needed.
    ///
    /// The recorder's local VAD stays in charge of segmentation (so
    /// suppressWhilePlaying and the utterance length limits keep working):
    /// audio is streamed with commit=false while capturing and the segment is
    /// committed when the recorder ends the capture.
    ///
    /// Uses System.Net.WebSockets.ClientWebSocket — works in the editor, on
    /// desktop and on Android/Quest IL2CPP (not WebGL).
    /// </summary>
    public class ElevenLabsRealtimeSTT : MonoBehaviour
    {
        [Tooltip("Capture source. Found on this GameObject when left empty.")]
        [SerializeField] private MicrophoneRecorder microphone;

        [Tooltip("Realtime Scribe model id.")]
        [SerializeField] private string modelId = "scribe_v2_realtime";

        [Tooltip("ISO language code hint, e.g. \"en\", \"pl\". Empty = auto-detect.")]
        [SerializeField] private string languageCode = "";

        [Tooltip("Log session events and transcripts — handy while testing.")]
        [SerializeField] private bool verboseLogging = true;

        /// <summary>Live partial transcript of the utterance in progress.</summary>
        public event Action<string> OnPartialTranscript;

        /// <summary>Committed transcript of a finished utterance.</summary>
        public event Action<string> OnTranscribed;

        /// <summary>
        /// An utterance the VAD kept was lost because the socket was down —
        /// no OnTranscribed will follow for it. Lets the UI recover instead of
        /// showing "transcribing" forever.
        /// </summary>
        public event Action OnUtteranceDropped;

        /// <summary>
        /// The finished utterance's commit was queued for the socket (main
        /// thread, enqueue time — the actual write happens on the send loop
        /// moments later). Timing hook for ConversationStatsLogic.
        /// </summary>
        public event Action OnCommitSent;

        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        // ~256 ms of 16 kHz audio per websocket message.
        private const int FlushSamples = 4096;
        private const float ReconnectCooldown = 2f;

        private ClientWebSocket _socket;
        private CancellationTokenSource _lifetimeCts;
        private TaskCompletionSource<bool> _sessionReady;
        private bool _connecting;
        private float _nextConnectTime;

        private readonly List<float> _pending = new List<float>();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new ConcurrentQueue<byte[]>();
        private SemaphoreSlim _sendSignal;

        private readonly ConcurrentQueue<Incoming> _received = new ConcurrentQueue<Incoming>();
        private int _awaitingCommits;   // commits sent but not yet answered (main thread only)
        private int _dropCommits;       // how many of those are discarded captures (main thread only)

        private void Awake()
        {
            if (microphone == null) microphone = GetComponent<MicrophoneRecorder>();
            if (microphone == null)
            {
                Debug.LogError("ElevenLabsRealtimeSTT: no MicrophoneRecorder assigned or found — disabling.", this);
                enabled = false;
            }
        }

        private void OnEnable()
        {
            microphone.OnCaptureBlock += HandleCaptureBlock;
            microphone.OnCaptureEnded += HandleCaptureEnded;
            EnsureConnected();
        }

        private void OnDisable()
        {
            microphone.OnCaptureBlock -= HandleCaptureBlock;
            microphone.OnCaptureEnded -= HandleCaptureEnded;
            Disconnect();
        }

        // ------------------------------------------------------------------ capture → socket

        private void HandleCaptureBlock(float[] samples)
        {
            EnsureConnected();
            _pending.AddRange(samples);
            while (_pending.Count >= FlushSamples)
                Flush(FlushSamples, commit: false);
        }

        private void HandleCaptureEnded(bool kept)
        {
            if (_socket == null && _pending.Count == 0)
                return;

            if (!IsConnected && !_connecting)
            {
                // Connection died mid-utterance and cooldown blocked a retry.
                if (kept)
                {
                    Debug.LogWarning("ElevenLabsRealtimeSTT: not connected — utterance dropped.", this);
                    OnUtteranceDropped?.Invoke();
                }
                _pending.Clear();
                while (_sendQueue.TryDequeue(out _)) { } // don't prepend this dead audio to the next session
                return;
            }

            Flush(_pending.Count, commit: true);
            OnCommitSent?.Invoke();
            _awaitingCommits++;
            if (!kept) _dropCommits++;
        }

        private void Flush(int count, bool commit)
        {
            byte[] pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                short s = (short)(Mathf.Clamp(_pending[i], -1f, 1f) * short.MaxValue);
                pcm[i * 2] = (byte)s;
                pcm[i * 2 + 1] = (byte)(s >> 8);
            }
            _pending.RemoveRange(0, count);

            // Base64 is JSON-safe, so the message can be assembled directly.
            string json = "{\"message_type\":\"input_audio_chunk\",\"audio_base_64\":\"" + Convert.ToBase64String(pcm) +
                          "\",\"commit\":" + (commit ? "true" : "false") +
                          ",\"sample_rate\":" + MicrophoneRecorder.SampleRate + "}";
            _sendQueue.Enqueue(Encoding.UTF8.GetBytes(json));
            _sendSignal?.Release();
        }

        // ------------------------------------------------------------------ connection

        private void EnsureConnected()
        {
            if (IsConnected || _connecting || Time.unscaledTime < _nextConnectTime)
                return;

            _connecting = true;
            _nextConnectTime = Time.unscaledTime + ReconnectCooldown;
            Connect();
        }

        private async void Connect()
        {
            Disconnect();
            _lifetimeCts = new CancellationTokenSource();
            // One semaphore count per already-queued message, so audio that
            // piled up while disconnected is drained without waiting for the
            // next Flush to Release.
            var sendSignal = new SemaphoreSlim(_sendQueue.Count, int.MaxValue);
            var sessionReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sendSignal = sendSignal;
            _sessionReady = sessionReady;
            CancellationToken ct = _lifetimeCts.Token;

            string url = $"{ElevenLabsApi.RealtimeSttUrl}?model_id={modelId}&audio_format=pcm_16000&commit_strategy=manual";
            if (!string.IsNullOrEmpty(languageCode))
                url += $"&language_code={Uri.EscapeDataString(languageCode)}";

            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("xi-api-key", ElevenLabsApi.Key);

            try
            {
                await socket.ConnectAsync(new Uri(url), ct);
            }
            catch (Exception e)
            {
                socket.Dispose();
                _connecting = false;
                Debug.LogWarning($"ElevenLabsRealtimeSTT: connection failed — {e.Message}", this);
                return;
            }

            _socket = socket;
            _connecting = false;
            if (verboseLogging) Debug.Log("ElevenLabsRealtimeSTT: connected.", this);
            _ = Task.Run(() => ReceiveLoop(socket, ct), ct);
            _ = Task.Run(() => SendLoop(socket, sendSignal, sessionReady, ct), ct);
        }

        private void Disconnect()
        {
            // Cancel-then-Dispose: without the Dispose every reconnect leaked a
            // CancellationTokenSource (kernel wait handle) and a SemaphoreSlim.
            var lifetimeCts = _lifetimeCts;
            _lifetimeCts = null;
            if (lifetimeCts != null)
            {
                lifetimeCts.Cancel();
                lifetimeCts.Dispose();
            }
            var sendSignal = _sendSignal;
            _sendSignal = null;
            sendSignal?.Dispose();

            if (_socket != null)
            {
                var socket = _socket;
                _socket = null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
                    }
                    catch (Exception) { /* closing a dead socket is fine */ }
                    finally { socket.Dispose(); }
                });
            }
        }

        private async Task SendLoop(ClientWebSocket socket, SemaphoreSlim sendSignal, TaskCompletionSource<bool> sessionReady, CancellationToken ct)
        {
            // Follow the documented workflow: wait for session_started before
            // sending audio (with a defensive timeout in case it never comes).
            await Task.WhenAny(sessionReady.Task, Task.Delay(5000, ct));

            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await sendSignal.WaitAsync(ct);
                    if (_sendQueue.TryDequeue(out byte[] message))
                        await socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { /* torn down by a reconnect */ }
            catch (Exception e)
            {
                _received.Enqueue(new Incoming { message_type = "local_error", error = $"send failed — {e.Message}", socket = socket });
            }
        }

        private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            // Idle sessions get closed server-side (e.g. during
                            // an intro) — surface it so the session is torn
                            // down instead of lingering half-dead until the
                            // next send trips over it.
                            _received.Enqueue(new Incoming { message_type = "local_error", error = "server closed the session", socket = socket });
                            return;
                        }
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    Incoming incoming = null;
                    try { incoming = JsonUtility.FromJson<Incoming>(json); }
                    catch (Exception) { /* not JSON — ignore */ }
                    if (incoming != null && !string.IsNullOrEmpty(incoming.message_type))
                        _received.Enqueue(incoming);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _received.Enqueue(new Incoming { message_type = "local_error", error = $"receive failed — {e.Message}", socket = socket });
            }
        }

        // ------------------------------------------------------------------ socket → events (main thread)

        private void Update()
        {
            while (_received.TryDequeue(out Incoming msg))
                Handle(msg);
        }

        private void Handle(Incoming msg)
        {
            switch (msg.message_type)
            {
                case "session_started":
                    _sessionReady?.TrySetResult(true);
                    if (verboseLogging) Debug.Log("ElevenLabsRealtimeSTT: session started.", this);
                    break;

                case "partial_transcript":
                    if (_dropCommits == 0 && !string.IsNullOrEmpty(msg.text))
                    {
                        // Adaptive endpointing: while the words so far sound
                        // unfinished, the recorder stretches its silence
                        // window so a thinking pause doesn't cut the speaker.
                        if (microphone != null)
                            microphone.HoldUtteranceOpen = LooksMidThought(msg.text);
                        OnPartialTranscript?.Invoke(msg.text);
                    }
                    break;

                case "committed_transcript":
                case "committed_transcript_with_timestamps":
                case "final_transcript":
                case "final_transcript_with_timestamps":
                    if (_awaitingCommits == 0)
                        break; // unexpected duplicate — already answered
                    _awaitingCommits--;
                    if (_dropCommits > 0)
                    {
                        _dropCommits--; // transcript of a capture the VAD discarded as noise
                        break;
                    }
                    if (verboseLogging) Debug.Log($"ElevenLabsRealtimeSTT: \"{msg.text}\"", this);
                    if (!string.IsNullOrEmpty(msg.text))
                        OnTranscribed?.Invoke(msg.text.Trim());
                    break;

                case "insufficient_audio_activity":
                    // A commit with (near-)silence — expected for discarded captures.
                    if (_awaitingCommits > 0) _awaitingCommits--;
                    if (_dropCommits > 0) _dropCommits--;
                    break;

                case "local_error":
                    // Only the CURRENT session's errors may tear anything down.
                    // A zombie loop of an already-replaced socket reporting its
                    // death used to Disconnect() here and abort the fresh
                    // connection racing up right next to it — dropping the
                    // first utterance after every idle period.
                    if (msg.socket != null && msg.socket != _socket)
                    {
                        if (verboseLogging) Debug.Log($"ElevenLabsRealtimeSTT: stale session — {msg.error}.", this);
                        break;
                    }
                    Debug.LogWarning($"ElevenLabsRealtimeSTT: {msg.error} — reconnecting on next speech.", this);
                    Disconnect();
                    break;

                default:
                    if (!string.IsNullOrEmpty(msg.error))
                        Debug.LogWarning($"ElevenLabsRealtimeSTT: {msg.message_type}: {msg.error}", this);
                    else if (verboseLogging)
                        Debug.Log($"ElevenLabsRealtimeSTT: unhandled message '{msg.message_type}'.", this);
                    break;
            }
        }

        /// <summary>Words that signal an unfinished thought when they end the
        /// partial transcript — fillers, conjunctions, articles, half-built
        /// phrases. Deliberately conservative: a false "unfinished" only costs
        /// extra listening time, a false "finished" cuts the speaker off.</summary>
        private static readonly HashSet<string> MidThoughtWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "um", "uh", "uhm", "erm", "hmm", "and", "but", "or", "so", "because", "like",
            "the", "a", "an", "to", "of", "with", "for", "if", "then", "is", "are", "was",
            "were", "i'm", "it's", "that's", "you're", "my", "your", "very", "really", "just",
        };

        private static bool LooksMidThought(string text)
        {
            string t = text.TrimEnd();
            if (t.Length == 0) return false;

            char last = t[t.Length - 1];
            if (last == '.' || last == '?' || last == '!') return false;   // finished sentence
            if (last == ',' || last == '-' || last == ':' || last == ';') return true;

            int i = t.LastIndexOf(' ');
            string word = (i >= 0 ? t.Substring(i + 1) : t).Trim('"', '\'', ')');
            return MidThoughtWords.Contains(word);
        }

        [Serializable]
        private class Incoming
        {
            public string message_type;
            public string text;
            public string error;

            /// <summary>Which session a local_error belongs to — lets the
            /// handler ignore errors from an already-replaced socket. Never
            /// part of the wire JSON.</summary>
            [NonSerialized] public ClientWebSocket socket;
        }
    }
}
