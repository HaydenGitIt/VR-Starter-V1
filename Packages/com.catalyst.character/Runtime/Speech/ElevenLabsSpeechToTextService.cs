using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace catalyst.xr67
{
    /// <summary>
    /// ISpeechToTextService backed by the ElevenLabs Scribe batch API — a
    /// drop-in replacement for WhisperSpeechToTextService on the
    /// SpeechToTextPipeline. Each utterance is WAV-encoded and POSTed to
    /// /v1/speech-to-text; expect a network round-trip per utterance but no
    /// on-device model loading or CPU cost.
    ///
    /// For live partial transcripts while the user is still speaking, use
    /// ElevenLabsRealtimeSTT instead — this class is the simple
    /// one-shot-per-utterance variant.
    /// </summary>
    public class ElevenLabsSpeechToTextService : MonoBehaviour, ISpeechToTextService
    {
        [Tooltip("Scribe model id — scribe_v2 (accuracy) or scribe_v1.")]
        [SerializeField] private string modelId = "scribe_v2";

        [Tooltip("ISO language code hint, e.g. \"en\", \"pl\". Empty = auto-detect.")]
        [SerializeField] private string languageCode = "";

        [Tooltip("Request timeout in seconds.")]
        [SerializeField] private int timeoutSeconds = 60;

        public bool IsReady { get; private set; }

        public async UniTask<bool> Initialize(CancellationToken ct = default)
        {
            using var request = UnityWebRequest.Get($"{ElevenLabsApi.BaseUrl}/user");
            request.SetRequestHeader("xi-api-key", ElevenLabsApi.Key);
            try
            {
                await request.SendWebRequest().WithCancellation(ct);
                IsReady = true;
            }
            catch (UnityWebRequestException e)
            {
                // Scoped keys can't read /v1/user but are still valid for STT.
                if (e.Text != null && e.Text.Contains("missing_permissions"))
                {
                    IsReady = true;
                    Debug.Log("[ElevenLabsSpeechToTextService] API key is scoped (no user-read permission) — proceeding.", this);
                }
                else
                {
                    IsReady = false;
                    Debug.LogWarning($"[ElevenLabsSpeechToTextService] Authorization failed: {e.ResponseCode} {e.Message}", this);
                }
            }
            return IsReady;
        }

        public async UniTask<string> Transcribe(float[] samples, int sampleRate, CancellationToken ct = default)
        {
            if (!IsReady)
                throw new SpeechToTextException("Not authorized — call Initialize() first.");

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("model_id", modelId),
                new MultipartFormFileSection("file", EncodeWav(samples, sampleRate), "utterance.wav", "audio/wav"),
            };
            if (!string.IsNullOrEmpty(languageCode))
                form.Add(new MultipartFormDataSection("language_code", languageCode));

            using var request = UnityWebRequest.Post($"{ElevenLabsApi.BaseUrl}/speech-to-text", form);
            request.SetRequestHeader("xi-api-key", ElevenLabsApi.Key);
            request.timeout = timeoutSeconds;

            try
            {
                await request.SendWebRequest().WithCancellation(ct);
            }
            catch (UnityWebRequestException e)
            {
                throw new SpeechToTextException($"Scribe request failed: {e.ResponseCode} {e.Message} {e.Text}", e);
            }

            var response = JsonUtility.FromJson<TranscriptionResponse>(request.downloadHandler.text);
            return response?.text?.Trim() ?? string.Empty;
        }

        /// <summary>Mono float PCM -1..1 → 16-bit PCM WAV bytes.</summary>
        private static byte[] EncodeWav(float[] samples, int sampleRate)
        {
            const int headerSize = 44;
            const short bitsPerSample = 16;
            int dataSize = samples.Length * 2;

            byte[] wav = new byte[headerSize + dataSize];
            void WriteAscii(int offset, string s) { for (int i = 0; i < s.Length; i++) wav[offset + i] = (byte)s[i]; }
            void WriteInt(int offset, int v) { wav[offset] = (byte)v; wav[offset + 1] = (byte)(v >> 8); wav[offset + 2] = (byte)(v >> 16); wav[offset + 3] = (byte)(v >> 24); }
            void WriteShort(int offset, short v) { wav[offset] = (byte)v; wav[offset + 1] = (byte)(v >> 8); }

            WriteAscii(0, "RIFF");
            WriteInt(4, 36 + dataSize);
            WriteAscii(8, "WAVE");
            WriteAscii(12, "fmt ");
            WriteInt(16, 16);                        // fmt chunk size
            WriteShort(20, 1);                       // PCM
            WriteShort(22, 1);                       // mono
            WriteInt(24, sampleRate);
            WriteInt(28, sampleRate * 2);            // byte rate
            WriteShort(32, 2);                       // block align
            WriteShort(34, bitsPerSample);
            WriteAscii(36, "data");
            WriteInt(40, dataSize);

            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                wav[headerSize + i * 2] = (byte)s;
                wav[headerSize + i * 2 + 1] = (byte)(s >> 8);
            }
            return wav;
        }

        [Serializable]
        private class TranscriptionResponse
        {
            public string text;
            public string language_code;
        }
    }
}
