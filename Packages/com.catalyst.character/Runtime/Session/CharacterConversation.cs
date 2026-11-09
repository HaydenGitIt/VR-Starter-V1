using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// A spoken conversation with one character, with no scoring attached:
    /// a transcript comes in (realtime or batch speech-to-text, or
    /// <see cref="Say"/> from code), Claude streams a reply, the reply is
    /// handed to the character's <see cref="NpcSpeechPlayer"/> sentence by
    /// sentence while it is still arriving, and — when <see cref="reportMood"/>
    /// is on — the mood the reply carries (0 = happy and calm, 100 = as worked
    /// up as the character gets) is raised through <see cref="OnMoodAssessed"/>
    /// for the body and face (see <see cref="AgitationResponder"/>).
    ///
    /// Every enable is a fresh conversation. Utterances that arrive while a
    /// reply is in flight, or while <see cref="Paused"/>, are dropped. Keys,
    /// endpoints and model come from <see cref="CharacterServicesConfig"/>
    /// and the fields below.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterConversation : MonoBehaviour
    {
        [Header("Sources (found in the scene when left empty)")]
        [Tooltip("Streaming transcription — preferred over the batch pipeline when present.")]
        [SerializeField] private ElevenLabsRealtimeSTT realtimeSpeechToText;

        [Tooltip("Batch transcription, used when there is no realtime source.")]
        [SerializeField] private SpeechToTextPipeline speechToText;

        [Tooltip("Microphone — only read for the silence timer and the state text.")]
        [SerializeField] private MicrophoneRecorder microphone;

        [Tooltip("Speaks the reply on the character while it streams in. Without one the reply stays text-only.")]
        [SerializeField] private NpcSpeechPlayer speechPlayer;

        [Header("LLM")]
        [Tooltip("Who the character is and how they react. Replace at runtime with SetSystemPrompt.")]
        [TextArea(3, 10)]
        [SerializeField] private string systemPrompt =
            "You are a friendly character standing in front of the user, chatting with them. Stay in character.";

        [Tooltip("Appended to every system prompt: how a reply must be shaped for speech.")]
        [TextArea(2, 6)]
        [SerializeField] private string responseRules =
            "Reply as spoken dialogue only: one or two short, casual sentences, plain text, no markdown, no stage directions, no emoji.";

        [Tooltip("Anthropic model id.")]
        [SerializeField] private string model = "claude-sonnet-5-5";

        [Tooltip("Response budget — keep small for short spoken replies.")]
        [SerializeField] private int maxTokens = 512;

        [Tooltip("Anthropic output_config effort — low keeps replies snappy.")]
        [SerializeField] private string effort = "low";

        [Tooltip("Ask for a {reply, mood} JSON so the mood can drive expression and speaking loops. Off = plain text replies, OnMoodAssessed never fires.")]
        [SerializeField] private bool reportMood = true;

        [Tooltip("With Report Mood on and a gesture catalog set (CharacterSession passes the character's profile one-shots): how the LLM should use the reply's \"gesture\" field. The list of gestures, their phases and descriptions is appended automatically.")]
        [TextArea(2, 6)]
        [SerializeField] private string gestureInstructions =
            "A one-shot body gesture the character makes while saying this reply, or \"none\". Pick one when the reply clearly expresses what the gesture means (a head shake for disagreeing or refusing, not for a plain question), and prefer a gesture listed for the phase you chose. Never pick a gesture on two replies in a row.";

        [Tooltip("Roughly what share of replies should carry a one-shot — told to the LLM with the gesture list. 0.2 = about one reply in five. The Agitation Responder's Min Gesture Fit and cooldowns filter on top.")]
        [Range(0f, 1f)]
        [SerializeField] private float gestureShare = 0.35f;

        [Header("Silence")]
        [Tooltip("Seconds the user can stay silent on their turn before the character speaks up on their own. 0 = never.")]
        [SerializeField] private float silenceNudgeSeconds = 0f;

        [Tooltip("How many silences in a row the character fills before waiting for the user. Resets when the user says something.")]
        [SerializeField] private int maxSilenceNudges = 2;

        [Tooltip("Added to the system prompt for a silence turn.")]
        [TextArea(2, 5)]
        [SerializeField] private string silenceContext =
            "The user has said nothing for a while. Fill the silence yourself in one short line — a question back to them or a remark about the situation. Do not comment on the silence.";

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = true;

        /// <summary>What the user said (or the silence marker) — raised before the request goes out.</summary>
        public event Action<string> OnUserUtterance;

        /// <summary>The spoken reply, once it fully arrived.</summary>
        public event Action<string> OnReply;

        /// <summary>Mood the reply reported, 0..100; -1 when the reply carried none. Only with Report Mood on.</summary>
        public event Action<int> OnMoodAssessed;

        /// <summary>Phase (calm / transition / agitated) the reply reported. Not raised when the reply carried none or an unknown value. Only with Report Mood on.</summary>
        public event Action<ConversationPhase> OnPhaseAssessed;

        /// <summary>
        /// The LLM's one-shot pick for this reply (see <see cref="SetGestureOptions"/>):
        /// the gesture id, null for "none", and how well it says the gesture
        /// fits (0..100, -1 when not reported). Raised after the phase and
        /// mood, for every reply while gestures are offered.
        /// </summary>
        public event Action<string, int> OnGestureChosen;

        /// <summary>Gestures currently offered to the LLM (empty = the reply format has no gesture field).</summary>
        public IReadOnlyList<StudentBehaviourProfile.LlmGesture> GestureOptions => _gestures;

        /// <summary>Short status for a debug label: listening, thinking..., speaking...</summary>
        public event Action<string> OnStateChanged;

        /// <summary>Partial transcript while the user is still talking (realtime source only).</summary>
        public event Action<string> OnPartialTranscript;

        /// <summary>Optional extra context appended to the system prompt on every request (e.g. the character's current situation).</summary>
        public Func<string> ContextAugmenter { get; set; }

        /// <summary>While true, incoming utterances are dropped (something scripted is playing).</summary>
        public bool Paused { get; set; }

        public bool IsBusy => _busy;
        public string State { get; private set; } = "idle";
        public IReadOnlyList<LlmMessage> History => _history;

        /// <summary>What <see cref="OnUserUtterance"/> carries for a silence turn instead of a transcript.</summary>
        public const string SilenceMarker = "(The user said nothing.)";

        private ILLMService _llm;
        private bool _busy;
        private CancellationTokenSource _queryCts;
        private readonly List<LlmMessage> _history = new List<LlmMessage>();
        private int _silenceNudges;
        private float _silenceSince;
        private readonly List<StudentBehaviourProfile.LlmGesture> _gestures = new List<StudentBehaviourProfile.LlmGesture>();

        [Serializable]
        private class StructuredReply
        {
            public string reply;
            public int mood = -1;
            public string phase;
            public string gesture;
            public int gesture_fit = -1;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// The one-shot gestures the LLM may pick from, by id (null / empty =
        /// none: the reply format drops its "gesture" field). Rebuilds the
        /// reply schema; applies from the next request.
        /// </summary>
        public void SetGestureOptions(IReadOnlyList<StudentBehaviourProfile.LlmGesture> options)
        {
            _gestures.Clear();
            if (options != null)
                foreach (StudentBehaviourProfile.LlmGesture g in options)
                    if (g != null && !string.IsNullOrWhiteSpace(g.id)) _gestures.Add(g);
            if (_llm is ClaudeLLMService claude) claude.OutputConfigJson = BuildOutputConfig();
        }

        /// <summary>Replaces the system prompt; takes effect from the next request.</summary>
        public void SetSystemPrompt(string prompt)
        {
            if (!string.IsNullOrWhiteSpace(prompt)) systemPrompt = prompt;
        }

        /// <summary>Forgets the exchange history (the next request starts a new conversation).</summary>
        public void ResetHistory()
        {
            _history.Clear();
            _silenceNudges = 0;
        }

        /// <summary>Feeds a line as if the user had said it — typed input, a test, a scripted opener.</summary>
        public void Say(string userText)
        {
            HandleTranscription(userText);
        }

        /// <summary>Stops the reply in flight and the speech.</summary>
        public void Interrupt()
        {
            CancelQuery();
            if (speechPlayer != null) speechPlayer.Cancel();
            _busy = false;
            SetState("listening");
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (realtimeSpeechToText == null) realtimeSpeechToText = FindFirstObjectByType<ElevenLabsRealtimeSTT>(FindObjectsInactive.Include);
            if (speechToText == null) speechToText = FindFirstObjectByType<SpeechToTextPipeline>(FindObjectsInactive.Include);
            if (microphone == null) microphone = FindFirstObjectByType<MicrophoneRecorder>(FindObjectsInactive.Include);
            if (speechPlayer == null) speechPlayer = FindFirstObjectByType<NpcSpeechPlayer>(FindObjectsInactive.Include);

            // Awake runs even when the component starts disabled, so the LLM
            // is warm by the time a session switches the conversation on.
            Initialize().Forget();
        }

        private bool UseRealtime => realtimeSpeechToText != null;

        private void OnEnable()
        {
            ResetHistory();
            Paused = false;
            _silenceSince = Time.unscaledTime;
            if (_llm is ClaudeLLMService claude && claude.IsAuthorized)
                claude.WarmUp(destroyCancellationToken).Forget();
            if (_llm != null && _llm.IsAuthorized) SetState("listening");

            if (UseRealtime)
            {
                realtimeSpeechToText.OnTranscribed += HandleTranscription;
                realtimeSpeechToText.OnPartialTranscript += HandlePartialTranscript;
                realtimeSpeechToText.OnUtteranceDropped += HandleUtteranceDropped;
            }
            else if (speechToText != null)
            {
                speechToText.OnTranscribed += HandleTranscription;
            }

            if (speechPlayer != null)
            {
                speechPlayer.OnSpeechStarted += HandleNpcSpeechStarted;
                speechPlayer.OnSpeechFinished += HandleNpcSpeechFinished;
            }

            if (microphone != null)
            {
                microphone.OnSpeechStarted += HandleMicSpeechStarted;
                microphone.OnUtteranceCaptured += HandleUtteranceCaptured;
            }
        }

        private void OnDisable()
        {
            if (UseRealtime)
            {
                realtimeSpeechToText.OnTranscribed -= HandleTranscription;
                realtimeSpeechToText.OnPartialTranscript -= HandlePartialTranscript;
                realtimeSpeechToText.OnUtteranceDropped -= HandleUtteranceDropped;
            }
            else if (speechToText != null)
            {
                speechToText.OnTranscribed -= HandleTranscription;
            }

            if (speechPlayer != null)
            {
                speechPlayer.OnSpeechStarted -= HandleNpcSpeechStarted;
                speechPlayer.OnSpeechFinished -= HandleNpcSpeechFinished;
            }

            if (microphone != null)
            {
                microphone.OnSpeechStarted -= HandleMicSpeechStarted;
                microphone.OnUtteranceCaptured -= HandleUtteranceCaptured;
            }

            CancelQuery();
            if (speechPlayer != null) speechPlayer.Cancel();
            _busy = false;
            if (_llm != null && _llm.IsAuthorized) SetState("idle");
        }

        private void OnDestroy()
        {
            CancelQuery();
        }

        private async UniTaskVoid Initialize()
        {
            if (speechToText == null && !UseRealtime)
                Debug.LogWarning("CharacterConversation: no speech-to-text source in the scene — only Say() can drive this conversation.", this);

            SetState("authorizing LLM...");
            _llm = new ClaudeLLMService(model, maxTokens, outputConfigJson: BuildOutputConfig());
            bool ok = await _llm.Authorize(destroyCancellationToken);
            if (!ok) SetState("LLM authorization failed");
            else SetState(isActiveAndEnabled ? "listening" : "idle");
        }

        private string BuildOutputConfig()
        {
            string effortJson = string.IsNullOrWhiteSpace(effort) ? "" : $"\"effort\":\"{effort.Trim()}\"";
            if (!reportMood)
                return effortJson.Length > 0 ? "{" + effortJson + "}" : null;

            // "reply" stays first: the stream extractor speaks it while the
            // rest (mood, phase, gesture) is still arriving.
            var schema = new StringBuilder();
            schema.Append("\"format\":{\"type\":\"json_schema\",\"schema\":{")
                .Append("\"type\":\"object\",\"properties\":{")
                .Append("\"reply\":{\"type\":\"string\",\"description\":\"What the character says out loud: one or two short, casual spoken sentences, plain text, no markdown, no stage directions.\"},")
                .Append("\"mood\":{\"type\":\"integer\",\"description\":\"How worked up the character is after this message, as an integer from 0 (happy and calm) to 100 (as agitated as they can get).\"},")
                .Append("\"phase\":{\"type\":\"string\",\"enum\":[\"calm\",\"transition\",\"agitated\"],\"description\":\"The character's emotional phase after this message, which drives their body language. ")
                .Append("agitated: restless, fidgeting, avoiding eye contact, confrontational, insistent. ")
                .Append("transition: still showing tension but with mild concern, starting to consider and think about what is said. ")
                .Append("calm: relaxed, reassured, neutral stance. ")
                .Append("Keep it in line with mood: calm once mood is about 30 or lower, agitated at about 65 or higher, transition in between.\"}");
            bool gestures = _gestures.Count > 0;
            if (gestures)
            {
                schema.Append(",\"gesture\":{\"type\":\"string\",\"enum\":[\"none\"");
                foreach (StudentBehaviourProfile.LlmGesture g in _gestures)
                    schema.Append(",\"").Append(JsonEscape(g.id)).Append('"');
                schema.Append("],\"description\":\"").Append(JsonEscape(GestureDescription())).Append("\"}");
                schema.Append(",\"gesture_fit\":{\"type\":\"integer\",\"description\":\"")
                    .Append(JsonEscape("How unmistakably this reply calls for the chosen gesture, 0-100. 90-100: the words themselves are that gesture (e.g. \"No, absolutely not\" for a head shake). 60-80: it suits the feeling but the words don't demand it. Below 50: a stretch. 0 when the gesture is none."))
                    .Append("\"}");
            }
            schema.Append("},\"required\":[\"reply\",\"mood\",\"phase\"").Append(gestures ? ",\"gesture\",\"gesture_fit\"" : "")
                .Append("],\"additionalProperties\":false}}");
            return "{" + (effortJson.Length > 0 ? effortJson + "," : "") + schema + "}";
        }

        /// <summary>Gesture instructions + one line per gesture: id (phases): description.</summary>
        private string GestureDescription()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(gestureInstructions)) sb.Append(gestureInstructions.Trim()).Append(' ');
            int perTen = Mathf.Clamp(Mathf.RoundToInt(gestureShare * 10f), 0, 10);
            sb.Append(perTen == 0
                ? "Use \"none\" unless a gesture is unmistakable. "
                : $"Across the conversation, aim for roughly {perTen} in 10 replies with a gesture; all the others are \"none\". ");
            sb.Append("Options —");
            foreach (StudentBehaviourProfile.LlmGesture g in _gestures)
            {
                sb.Append(' ').Append(g.id);
                if (g.phases.Count > 0)
                {
                    sb.Append(" (");
                    for (int i = 0; i < g.phases.Count; i++)
                        sb.Append(i > 0 ? ", " : "").Append(g.phases[i].ToString().ToLowerInvariant());
                    sb.Append(')');
                }
                if (!string.IsNullOrWhiteSpace(g.description)) sb.Append(": ").Append(g.description.Trim().TrimEnd('.'));
                sb.Append('.');
            }
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private bool IsGestureOption(string id)
        {
            foreach (StudentBehaviourProfile.LlmGesture g in _gestures)
                if (g.id == id) return true;
            return false;
        }

        private void CancelQuery()
        {
            if (_queryCts == null) return;
            _queryCts.Cancel();
            _queryCts.Dispose();
            _queryCts = null;
        }

        // ------------------------------------------------------------------ input

        private void HandleMicSpeechStarted()
        {
            if (!_busy) SetState("hearing you...");
        }

        private void HandleUtteranceCaptured(float[] _)
        {
            if (!_busy) SetState("transcribing...");
        }

        private void HandlePartialTranscript(string text)
        {
            if (!_busy) OnPartialTranscript?.Invoke(text);
        }

        private void HandleUtteranceDropped()
        {
            if (!_busy) SetState("didn't catch that — say it again");
        }

        private void HandleTranscription(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Paused)
            {
                TestFlowLog.Emit("user_dropped", $"You said (ignored — a scripted line is playing): \"{text}\"", this,
                    "text", text, "reason", "scripted line playing");
                return;
            }
            if (_busy)
            {
                TestFlowLog.Emit("user_dropped", $"You said (ignored — still waiting for the previous reply): \"{text}\"", this,
                    "text", text, "reason", "previous reply still pending");
                return;
            }
            if (_llm == null || !_llm.IsAuthorized)
            {
                TestFlowLog.Emit("user_dropped", $"You said (ignored — LLM not authorized): \"{text}\"", this,
                    "text", text, "reason", "LLM not authorized");
                SetState("LLM not authorized");
                return;
            }
            _silenceNudges = 0;
            TestFlowLog.Emit("user", $"You said: \"{text}\"", this, "text", text);
            OnUserUtterance?.Invoke(text);   // before Ask — listeners arm themselves for this exchange
            Ask(text, silence: false).Forget();
        }

        // ------------------------------------------------------------------ silence

        private void Update()
        {
            if (silenceNudgeSeconds <= 0f || !UsersTurnAndQuiet())
            {
                _silenceSince = Time.unscaledTime;
                return;
            }
            if (_silenceNudges >= maxSilenceNudges || Time.unscaledTime - _silenceSince < silenceNudgeSeconds)
                return;

            _silenceNudges++;
            _silenceSince = Time.unscaledTime;
            TestFlowLog.Emit("silence", $"You were silent for {silenceNudgeSeconds:0.#} s — the character speaks up ({_silenceNudges}/{maxSilenceNudges}).", this,
                "seconds", silenceNudgeSeconds, "nudge", _silenceNudges, "max_nudges", maxSilenceNudges);
            OnUserUtterance?.Invoke(SilenceMarker);
            Ask(SilenceMarker, silence: true).Forget();
        }

        private bool UsersTurnAndQuiet()
        {
            if (Paused || _busy || _llm == null || !_llm.IsAuthorized) return false;
            if (speechPlayer != null && speechPlayer.IsSpeaking) return false;
            return microphone == null || (microphone.IsListening && !microphone.IsCapturing);
        }

        // ------------------------------------------------------------------ the exchange

        private async UniTaskVoid Ask(string userText, bool silence)
        {
            _busy = true;
            SetState("thinking...");
            speechPlayer?.Cancel();   // drop any leftover speech from the previous reply

            CancelQuery();
            var myCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _queryCts = myCts;
            CancellationToken ct = myCts.Token;
            var latency = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var prose = new StringBuilder();
                // With the JSON schema on, the raw stream is the structured
                // object — the extractor peels the spoken "reply" value out of
                // it as it arrives so only prose reaches the speech player.
                LlmStreamReplyExtractor extractor = reportMood ? new LlmStreamReplyExtractor() : null;
                var request = new List<LlmMessage>(_history)
                {
                    new LlmMessage { role = "user", content = userText },
                };
                string raw = await _llm.SendConversationStreaming(BuildContext(silence), request, delta =>
                {
                    if (ct.IsCancellationRequested) return;   // disabled mid-stream — drop late fragments
                    string spoken = extractor != null ? extractor.Feed(delta) : delta;
                    if (string.IsNullOrEmpty(spoken)) return;
                    prose.Append(spoken);
                    speechPlayer?.FeedText(spoken);
                }, ct);
                speechPlayer?.EndOfText();

                string reply = prose.ToString().Trim();
                int mood = -1;
                bool hasPhase = false;
                string rawPhase = null;
                ConversationPhase phase = ConversationPhase.Calm;
                string rawGesture = null, gesture = null;
                int gestureFit = -1;
                if (reportMood)
                {
                    StructuredReply parsed = null;
                    try { parsed = JsonUtility.FromJson<StructuredReply>(raw); }
                    catch (Exception) { /* malformed — fall back to the streamed prose */ }
                    if (parsed != null)
                    {
                        if (!string.IsNullOrWhiteSpace(parsed.reply)) reply = parsed.reply.Trim();
                        mood = parsed.mood < 0 ? -1 : Mathf.Min(parsed.mood, 100);
                        rawPhase = parsed.phase;
                        hasPhase = ConversationPhaseUtil.TryParse(parsed.phase, out phase);
                        rawGesture = string.IsNullOrWhiteSpace(parsed.gesture) ? null : parsed.gesture.Trim();
                        string candidate = rawGesture?.ToLowerInvariant();
                        if (candidate != null && candidate != "none" && IsGestureOption(candidate)) gesture = candidate;
                        gestureFit = parsed.gesture_fit < 0 ? -1 : Mathf.Min(parsed.gesture_fit, 100);
                    }
                    // The extractor never found a reply field (plain-text answer
                    // despite the schema) — say the raw text rather than nothing.
                    if (prose.Length == 0 && reply.Length == 0 && !string.IsNullOrWhiteSpace(raw))
                    {
                        reply = raw.Trim();
                        speechPlayer?.Speak(reply);
                    }
                }

                // The exchange only enters the history once it fully succeeded,
                // so a failed or cancelled turn never leaves a half-turn behind.
                _history.Add(new LlmMessage { role = "user", content = userText });
                _history.Add(new LlmMessage { role = "assistant", content = raw });

                if (speechPlayer == null || !speechPlayer.IsSpeaking) SetState("listening");
                if (verboseLogging && !TestFlowLog.Enabled)
                    Debug.Log($"CharacterConversation: \"{userText}\" -> \"{reply}\"{(reportMood ? $" [mood {(mood >= 0 ? mood.ToString() : "?")}, phase {(hasPhase ? phase.ToString() : "?")}, gesture {gesture ?? "none"}]" : "")}", this);
                float seconds = (float)latency.Elapsed.TotalSeconds;
                string phaseWord = hasPhase ? phase.ToString().ToLowerInvariant() : null;
                string moodText = !reportMood ? "" : $" — mood {(mood >= 0 ? mood + "/100" : "?")}, phase \"{phaseWord ?? (string.IsNullOrEmpty(rawPhase) ? "missing" : rawPhase + " (invalid)")}\"";
                bool offersGestures = reportMood && _gestures.Count > 0;
                string gestureText = !offersGestures ? ""
                    : gesture != null ? $", one-shot \"{gesture}\" (fit {(gestureFit >= 0 ? gestureFit.ToString() : "?")})"
                    : rawGesture == null || rawGesture.Equals("none", StringComparison.OrdinalIgnoreCase) ? ", no one-shot"
                    : $", one-shot \"{rawGesture}\" (not in the catalog — ignored)";
                TestFlowLog.Emit("llm_reply", $"LLM replied ({seconds:0.0} s): \"{reply}\"{moodText}{gestureText}", this,
                    "user", silence ? null : userText,
                    "silence", silence,
                    "reply", reply,
                    "mood", reportMood && mood >= 0 ? mood : (object)null,
                    "phase", phaseWord,
                    "phase_raw", rawPhase,
                    "gesture", gesture,
                    "gesture_fit", gesture != null && gestureFit >= 0 ? gestureFit : (object)null,
                    "gesture_raw", offersGestures ? rawGesture : null,
                    "gestures_offered", offersGestures,
                    "latency_s", seconds);

                Raise(OnReply, reply, "OnReply");
                // Phase before mood: the responder picks the phase's face floor
                // up in the same frame the new mood lands. Gesture last, so it
                // plays over the new phase's loop.
                if (reportMood && hasPhase) Raise(OnPhaseAssessed, phase, "OnPhaseAssessed");
                else if (reportMood) Debug.LogWarning("CharacterConversation: the LLM reply carried no valid phase (calm / transition / agitated) — the body keeps its previous phase.", this);
                if (reportMood) Raise(OnMoodAssessed, mood, "OnMoodAssessed");
                if (offersGestures)
                {
                    try { OnGestureChosen?.Invoke(gesture, gesture != null ? gestureFit : -1); }
                    catch (Exception e) { Debug.LogError($"CharacterConversation: OnGestureChosen handler threw — {e.Message}", this); }
                }
            }
            catch (OperationCanceledException)
            {
                // Conversation switched off (or object destroyed) — nothing to report.
            }
            catch (Exception e)
            {
                SetState("LLM error — see console");
                Debug.LogError($"CharacterConversation: query failed — {e.Message}", this);
                TestFlowLog.Emit("llm_error", $"LLM request failed: {e.Message}", this, "user", userText, "error", e.Message);
            }
            finally
            {
                // A cancelled query (OnDisable) has already been cleared there;
                // only the current query may release the busy flag.
                if (ReferenceEquals(_queryCts, myCts)) _busy = false;
            }
        }

        private static void Raise<T>(Action<T> handler, T value, string name)
        {
            try { handler?.Invoke(value); }
            catch (Exception e) { Debug.LogError($"CharacterConversation: {name} handler threw — {e.Message}"); }
        }

        private void HandleNpcSpeechStarted()
        {
            SetState("speaking...");
        }

        private void HandleNpcSpeechFinished()
        {
            if (!_busy) SetState("listening");
        }

        /// <summary>System prompt + response rules + silence instruction + whatever the augmenter adds.</summary>
        private string BuildContext(bool silence)
        {
            var sb = new StringBuilder(systemPrompt);
            if (!string.IsNullOrWhiteSpace(responseRules)) sb.Append("\n\n").Append(responseRules.Trim());
            if (silence && !string.IsNullOrWhiteSpace(silenceContext)) sb.Append("\n\n").Append(silenceContext.Trim());

            string addition = null;
            try { addition = ContextAugmenter?.Invoke(); }
            catch (Exception e)
            {
                Debug.LogWarning($"CharacterConversation: context augmenter threw — using the base context. {e.Message}", this);
            }
            if (!string.IsNullOrWhiteSpace(addition)) sb.Append("\n\n").Append(addition.Trim());
            return sb.ToString();
        }

        private void SetState(string state)
        {
            State = state;
            OnStateChanged?.Invoke(state);
        }
    }
}
