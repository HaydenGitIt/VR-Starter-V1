using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace catalyst.xr67
{
    /// <summary>
    /// ILLMService implementation that talks directly to the Anthropic Messages API.
    /// </summary>
    public class ClaudeLLMService : ILLMService
    {
        // Key and endpoints come from the CharacterServicesConfig asset
        // (Resources/CharacterServicesConfig), never from source. Anyone with a
        // build can still read the asset — a public build should point the base
        // URL at a proxy that holds the key.
        static string ApiKey => CharacterServicesConfig.AnthropicKey;
        static string MessagesEndpoint => $"{CharacterServicesConfig.AnthropicBaseUrl}/messages";
        static string ModelsEndpoint => $"{CharacterServicesConfig.AnthropicBaseUrl}/models?limit=1";
        static string ApiVersion => CharacterServicesConfig.AnthropicApiVersion;

        readonly string model;
        readonly int maxTokens;
        readonly int timeoutSeconds;
        string outputConfigJson;

        public bool IsAuthorized { get; private set; }

        /// <summary>Raw JSON sent as "output_config" with every request (null = none). Changing it applies from the next request — e.g. a reply schema rebuilt for a new gesture catalog.</summary>
        public string OutputConfigJson
        {
            get => outputConfigJson;
            set => outputConfigJson = value;
        }

        /// <param name="outputConfigJson">Optional raw JSON for the request's
        /// "output_config" field — e.g. effort level and a json_schema format
        /// forcing structured replies. Applied to every request this service
        /// sends; null sends none.</param>
        public ClaudeLLMService(string model = "claude-opus-5", int maxTokens = 16000, int timeoutSeconds = 300, string outputConfigJson = null)
        {
            this.model = model;
            this.maxTokens = maxTokens;
            this.timeoutSeconds = timeoutSeconds;
            this.outputConfigJson = outputConfigJson;
        }

        /// <summary>Attempts per streaming request: the first may hit a stale keep-alive connection.</summary>
        private const int MaxSendAttempts = 2;

        /// <summary>
        /// Re-opens the HTTP connection with a cheap GET right before the
        /// conversation goes live, so the first real request does not land
        /// on a keep-alive connection the server closed while the app sat in
        /// menus. Never touches IsAuthorized — a failure here just means the
        /// real request retries.
        /// </summary>
        public async UniTask WarmUp(CancellationToken ct = default)
        {
            using var request = UnityWebRequest.Get(ModelsEndpoint);
            SetHeaders(request);
            request.timeout = 10;
            try
            {
                await request.SendWebRequest().WithCancellation(ct);
            }
            catch (UnityWebRequestException e)
            {
                Debug.Log($"[ClaudeLLMService] warm-up request failed ({DescribeError(e)}) — the first message will retry if needed.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async UniTask<bool> Authorize(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(ApiKey) && CharacterServicesConfig.Current == null)
            {
                IsAuthorized = false;
                return false;   // CharacterServicesConfig already reported the missing asset
            }

            using var request = UnityWebRequest.Get(ModelsEndpoint);
            SetHeaders(request);
            try
            {
                await request.SendWebRequest().WithCancellation(ct);
                IsAuthorized = true;
            }
            catch (UnityWebRequestException e)
            {
                IsAuthorized = false;
                Debug.LogWarning($"[ClaudeLLMService] Authorization failed: {DescribeError(e)}");
            }
            return IsAuthorized;
        }

        public void Deauthorize()
        {
            IsAuthorized = false;
        }

        public UniTask<string> SendQuery(string query, CancellationToken ct = default)
        {
            return SendQuery(null, query, ct);
        }

        public UniTask<string> SendQueryStreaming(string systemPrompt, string query, Action<string> onTextDelta, CancellationToken ct = default)
        {
            return SendConversationStreaming(systemPrompt, new[] { new LlmMessage { role = "user", content = query } }, onTextDelta, ct);
        }

        public async UniTask<string> SendConversationStreaming(string systemPrompt, IReadOnlyList<LlmMessage> messages, Action<string> onTextDelta, CancellationToken ct = default)
        {
            if (!IsAuthorized)
                throw new LLMServiceException("Not authorized — call Authorize() first.");

            var fullText = new StringBuilder();
            string refusal = null;
            string streamError = null;

            // The SSE handler receives each "data: {...}" payload as it streams in.
            // UnityWebRequest invokes it on the main thread, so the callback can
            // safely touch Unity objects.
            SseDownloadHandler MakeHandler() => new SseDownloadHandler(payload =>
            {
                StreamEvent ev = null;
                try { ev = JsonUtility.FromJson<StreamEvent>(payload); }
                catch (Exception) { /* not an event we understand — skip */ }
                if (ev == null) return;

                if (ev.type == "content_block_delta" && ev.delta != null && ev.delta.type == "text_delta" && !string.IsNullOrEmpty(ev.delta.text))
                {
                    fullText.Append(ev.delta.text);
                    onTextDelta?.Invoke(ev.delta.text);
                }
                else if (ev.type == "message_delta" && ev.delta != null && ev.delta.stop_reason == "refusal")
                {
                    refusal = "Claude declined to answer this query (stop_reason: refusal).";
                }
                else if (ev.type == "error" && ev.error != null)
                {
                    streamError = $"{ev.error.type}: {ev.error.message}";
                }
            });

            var payloadBytes = Encoding.UTF8.GetBytes(BuildRequestJson(systemPrompt, messages, stream: true));

            // A request that dies before a single byte arrives is almost always
            // Unity reusing a keep-alive connection the server closed while the
            // app sat in menus (the key check at startup opened it). Nothing was
            // consumed, so one silent retry on a fresh connection is safe.
            for (int attempt = 1; ; attempt++)
            {
                SseDownloadHandler handler = MakeHandler();
                using var request = new UnityWebRequest(MessagesEndpoint, UnityWebRequest.kHttpVerbPOST);
                request.uploadHandler = new UploadHandlerRaw(payloadBytes) { contentType = "application/json" };
                request.downloadHandler = handler;
                SetHeaders(request);
                request.timeout = timeoutSeconds;

                try
                {
                    await request.SendWebRequest().WithCancellation(ct);
                }
                catch (UnityWebRequestException e)
                {
                    // A listener that threw inside the stream handler aborts the
                    // request with a bare "0 Unknown Error" — name the real culprit.
                    if (handler.ListenerException != null)
                        throw new LLMServiceException(
                            $"Claude stream aborted by a listener exception: {handler.ListenerException.GetType().Name}: {handler.ListenerException.Message}",
                            handler.ListenerException);

                    bool diedBeforeAnyByte = request.result == UnityWebRequest.Result.ConnectionError
                                             && handler.BytesReceived == 0 && !ct.IsCancellationRequested;
                    if (diedBeforeAnyByte && attempt < MaxSendAttempts)
                    {
                        Debug.LogWarning($"[ClaudeLLMService] request failed before any byte arrived ({request.error}) — retrying on a fresh connection.");
                        fullText.Clear();
                        refusal = null;
                        streamError = null;
                        continue;
                    }
                    throw new LLMServiceException(
                        $"Claude streaming request failed: {DescribeError(e)} (result {request.result}, error {request.error}, {handler.BytesReceived} bytes received, attempt {attempt})", e);
                }

                if (handler.ListenerException != null)
                    throw new LLMServiceException(
                        $"A stream listener threw: {handler.ListenerException.GetType().Name}: {handler.ListenerException.Message}",
                        handler.ListenerException);
                break;
            }

            if (streamError != null)
                throw new LLMServiceException($"Claude stream error: {streamError}");
            if (refusal != null)
                throw new LLMServiceException(refusal);

            return fullText.ToString();
        }

        public async UniTask<string> SendQuery(string systemPrompt, string query, CancellationToken ct = default)
        {
            if (!IsAuthorized)
                throw new LLMServiceException("Not authorized — call Authorize() first.");

            var payload = BuildRequestJson(systemPrompt, new[] { new LlmMessage { role = "user", content = query } });
            using var request = UnityWebRequest.Post(MessagesEndpoint, payload, "application/json");
            SetHeaders(request);
            request.timeout = timeoutSeconds;

            try
            {
                await request.SendWebRequest().WithCancellation(ct);
            }
            catch (UnityWebRequestException e)
            {
                throw new LLMServiceException($"Claude request failed: {DescribeError(e)}", e);
            }

            var response = JsonUtility.FromJson<MessagesResponse>(request.downloadHandler.text);

            // Safety classifiers can decline a request with HTTP 200 — check before reading content.
            if (response.stop_reason == "refusal")
                throw new LLMServiceException("Claude declined to answer this query (stop_reason: refusal).");

            var text = new StringBuilder();
            if (response.content != null)
            {
                foreach (var block in response.content)
                {
                    if (block.type == "text")
                        text.Append(block.text);
                }
            }
            return text.ToString();
        }

        void SetHeaders(UnityWebRequest request)
        {
            request.SetRequestHeader("x-api-key", ApiKey);
            request.SetRequestHeader("anthropic-version", ApiVersion);
        }

        string BuildRequestJson(string systemPrompt, IReadOnlyList<LlmMessage> messages, bool stream = false)
        {
            var messageArray = new LlmMessage[messages.Count];
            for (int i = 0; i < messages.Count; i++)
                messageArray[i] = messages[i];

            string json = string.IsNullOrEmpty(systemPrompt)
                ? JsonUtility.ToJson(new MessagesRequest
                {
                    model = model,
                    max_tokens = maxTokens,
                    messages = messageArray,
                    stream = stream,
                })
                : JsonUtility.ToJson(new MessagesRequestWithSystem
                {
                    model = model,
                    max_tokens = maxTokens,
                    messages = messageArray,
                    system = systemPrompt,
                    stream = stream,
                });

            // output_config carries an arbitrary caller-provided schema, which
            // JsonUtility cannot express — splice the raw JSON into the body.
            string config = outputConfigJson;   // read once — the property may be swapped between requests
            if (!string.IsNullOrEmpty(config))
                json = json.Substring(0, json.Length - 1) + ",\"output_config\":" + config + "}";

            return json;
        }

        static string DescribeError(UnityWebRequestException e)
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                var body = JsonUtility.FromJson<ErrorResponse>(e.Text);
                if (body?.error != null)
                    return $"{e.ResponseCode} {body.error.type}: {body.error.message}";
            }
            return $"{e.ResponseCode} {e.Message}";
        }

        [Serializable]
        class MessagesRequest
        {
            public string model;
            public int max_tokens;
            public LlmMessage[] messages;
            public bool stream;
        }

        [Serializable]
        class MessagesRequestWithSystem : MessagesRequest
        {
            public string system;
        }

        [Serializable]
        class ContentBlock
        {
            public string type;
            public string text;
        }

        [Serializable]
        class MessagesResponse
        {
            public ContentBlock[] content;
            public string stop_reason;
            public string model;
        }

        [Serializable]
        class ErrorResponse
        {
            public ApiError error;
        }

        [Serializable]
        class ApiError
        {
            public string type;
            public string message;
        }

        [Serializable]
        class StreamEvent
        {
            public string type;
            public StreamDelta delta;
            public ApiError error;
        }

        [Serializable]
        class StreamDelta
        {
            public string type;
            public string text;
            public string stop_reason;
        }

        /// <summary>
        /// Incremental server-sent-events parser: buffers incoming bytes,
        /// splits on complete lines (multi-byte UTF-8 never gets cut) and
        /// forwards each "data: {...}" payload as it arrives.
        /// </summary>
        class SseDownloadHandler : DownloadHandlerScript
        {
            private readonly Action<string> _onData;
            private readonly List<byte> _buffer = new List<byte>();
            private string _rawBody = ""; // non-SSE lines (e.g. an HTTP-error JSON body)

            /// <summary>First exception a data listener threw — logged there and then, kept here so the caller can name it.</summary>
            public Exception ListenerException { get; private set; }

            /// <summary>Bytes the server sent so far — 0 on a failed request means it never connected.</summary>
            public int BytesReceived { get; private set; }

            public SseDownloadHandler(Action<string> onData) : base(new byte[8192])
            {
                _onData = onData;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                BytesReceived += dataLength;
                for (int i = 0; i < dataLength; i++)
                    _buffer.Add(data[i]);

                int consumed = 0;
                for (int i = 0; i < _buffer.Count; i++)
                {
                    if (_buffer[i] != (byte)'\n')
                        continue;

                    int length = i - consumed;
                    if (length > 0 && _buffer[consumed + length - 1] == (byte)'\r')
                        length--;

                    if (length > 0)
                    {
                        byte[] lineBytes = new byte[length];
                        _buffer.CopyTo(consumed, lineBytes, 0, length);
                        HandleLine(Encoding.UTF8.GetString(lineBytes));
                    }
                    consumed = i + 1;
                }
                if (consumed > 0)
                    _buffer.RemoveRange(0, consumed);
                return true;
            }

            private void HandleLine(string line)
            {
                if (line.StartsWith("data:"))
                {
                    // An exception here would abort the whole request as a
                    // "0 Unknown Error" — log it with its stack and keep the
                    // stream alive; the caller reports it after the request.
                    try
                    {
                        _onData(line.Substring(5).Trim());
                    }
                    catch (Exception e)
                    {
                        if (ListenerException == null) ListenerException = e;
                        Debug.LogException(e);
                    }
                }
                else if (!line.StartsWith("event:") && !line.StartsWith(":"))
                    _rawBody += line; // error responses are plain JSON, keep for diagnostics

                if (_rawBody.Length > 4096)
                    _rawBody = _rawBody.Substring(0, 4096);
            }

            protected override string GetText()
            {
                return _rawBody;
            }
        }
    }
}
