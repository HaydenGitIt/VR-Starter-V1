using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace catalyst.xr67
{
    /// <summary>
    /// One turn of a conversation. Role is "user" (the player) or "assistant"
    /// (the LLM's earlier reply, stored verbatim — for structured replies that
    /// is the raw JSON). The API is stateless: the caller keeps the list and
    /// resends it with every query.
    /// </summary>
    [Serializable]
    public class LlmMessage
    {
        public string role;
        public string content;
    }

    /// <summary>
    /// Abstraction over an LLM backend so the concrete service can be swapped —
    /// e.g. direct provider access during development vs. the Catalyst AWS proxy
    /// in production builds (so no API key ships inside the app).
    /// </summary>
    public interface ILLMService
    {
        bool IsAuthorized { get; }

        /// <summary>
        /// Validates credentials / performs the backend handshake.
        /// Must succeed before SendQuery can be used.
        /// </summary>
        UniTask<bool> Authorize(CancellationToken ct = default);

        void Deauthorize();

        /// <summary>Sends a user query and returns the model's text response.</summary>
        UniTask<string> SendQuery(string query, CancellationToken ct = default);

        /// <summary>Sends a user query with a system prompt steering the model's behavior.</summary>
        UniTask<string> SendQuery(string systemPrompt, string query, CancellationToken ct = default);

        /// <summary>
        /// Streaming variant of SendQuery: onTextDelta fires on the main
        /// thread with each text fragment as the model produces it (so e.g.
        /// TTS can start before the reply is complete); returns the full text.
        /// </summary>
        UniTask<string> SendQueryStreaming(string systemPrompt, string query, Action<string> onTextDelta, CancellationToken ct = default);

        /// <summary>
        /// Multi-turn variant: the whole conversation so far goes out with the
        /// query (the last entry is the message being answered). Streams like
        /// SendQueryStreaming and returns the full raw response text.
        /// </summary>
        UniTask<string> SendConversationStreaming(string systemPrompt, IReadOnlyList<LlmMessage> messages, Action<string> onTextDelta, CancellationToken ct = default);
    }

    public class LLMServiceException : Exception
    {
        public LLMServiceException(string message) : base(message) { }
        public LLMServiceException(string message, Exception inner) : base(message, inner) { }
    }
}
