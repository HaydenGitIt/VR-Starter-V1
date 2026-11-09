using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Abstraction over a text-to-speech backend so the concrete service can
    /// be swapped — e.g. ElevenLabs during development vs. a proxy or an
    /// on-device voice in production builds.
    /// </summary>
    public interface ITTSService
    {
        bool IsAuthorized { get; }

        /// <summary>
        /// Validates credentials / performs the backend handshake.
        /// Must succeed before Synthesize can be used.
        /// </summary>
        UniTask<bool> Authorize(CancellationToken ct = default);

        void Deauthorize();

        /// <summary>Synthesizes speech for the given text and returns a playable AudioClip.</summary>
        UniTask<AudioClip> Synthesize(string text, CancellationToken ct = default);
    }

    public class TTSServiceException : Exception
    {
        public TTSServiceException(string message) : base(message) { }
        public TTSServiceException(string message, Exception inner) : base(message, inner) { }
    }
}
