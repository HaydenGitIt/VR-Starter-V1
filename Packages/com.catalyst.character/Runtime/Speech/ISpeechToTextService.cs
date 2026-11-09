using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace catalyst.xr67
{
    /// <summary>
    /// Abstraction over a speech-to-text backend so the concrete service can
    /// be swapped — on-device whisper today, a cloud transcription service
    /// later — without touching the callers.
    /// </summary>
    public interface ISpeechToTextService
    {
        bool IsReady { get; }

        /// <summary>Loads the model / performs the backend handshake. Must succeed before Transcribe.</summary>
        UniTask<bool> Initialize(CancellationToken ct = default);

        /// <summary>Transcribes a finished utterance: mono PCM samples in -1..1 at the given rate.</summary>
        UniTask<string> Transcribe(float[] samples, int sampleRate, CancellationToken ct = default);
    }

    public class SpeechToTextException : Exception
    {
        public SpeechToTextException(string message) : base(message) { }
        public SpeechToTextException(string message, Exception inner) : base(message, inner) { }
    }
}
