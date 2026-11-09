using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace catalyst.xr67
{
    /// <summary>
    /// ITTSService implementation that talks directly to the ElevenLabs
    /// text-to-speech API and decodes the returned MP3 into an AudioClip.
    /// Play the clip on the NPC's AudioSource and the LipSyncAnalyzer picks it
    /// up automatically — it analyzes the live output, not the clip asset.
    /// </summary>
    public class ElevenLabsTTSService : ITTSService
    {
        // Key and endpoints are shared with the STT services — see ElevenLabsApi
        // (read from the CharacterServicesConfig asset).
        static string ApiKey => ElevenLabsApi.Key;
        static string BaseUrl => ElevenLabsApi.BaseUrl;

        readonly string voiceId;
        readonly string modelId;
        readonly float stability;
        readonly float similarityBoost;
        readonly int timeoutSeconds;

        public bool IsAuthorized { get; private set; }

        /// <param name="voiceId">ElevenLabs voice id — default is "Rachel", a stock voice.
        /// Browse ids at https://elevenlabs.io/app/voice-library or via GET /v1/voices.</param>
        /// <param name="modelId">"eleven_multilingual_v2" for quality; switch to
        /// "eleven_flash_v2_5" if TTS latency in the conversation loop becomes an issue.</param>
        public ElevenLabsTTSService(
            string voiceId = "21m00Tcm4TlvDq8ikWAM",
            string modelId = "eleven_multilingual_v2",
            float stability = 0.5f,
            float similarityBoost = 0.75f,
            int timeoutSeconds = 120)
        {
            this.voiceId = voiceId;
            this.modelId = modelId;
            this.stability = stability;
            this.similarityBoost = similarityBoost;
            this.timeoutSeconds = timeoutSeconds;
        }

        public async UniTask<bool> Authorize(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(ApiKey) && CharacterServicesConfig.Current == null)
            {
                IsAuthorized = false;
                return false;   // CharacterServicesConfig already reported the missing asset
            }

            using var request = UnityWebRequest.Get($"{BaseUrl}/user");
            request.SetRequestHeader("xi-api-key", ApiKey);
            try
            {
                await request.SendWebRequest().WithCancellation(ct);
                IsAuthorized = true;
            }
            catch (UnityWebRequestException e)
            {
                // Scoped API keys (e.g. TTS/STT-only) can't read /v1/user and
                // answer 401 missing_permissions while being perfectly valid
                // for synthesis — treat that as authorized and let Synthesize
                // surface any real permission problem.
                if (e.Text != null && e.Text.Contains("missing_permissions"))
                {
                    IsAuthorized = true;
                    Debug.Log("[ElevenLabsTTSService] API key is scoped (no user-read permission) — proceeding.");
                }
                else
                {
                    IsAuthorized = false;
                    Debug.LogWarning($"[ElevenLabsTTSService] Authorization failed: {DescribeError(e)}");
                }
            }
            return IsAuthorized;
        }

        public void Deauthorize()
        {
            IsAuthorized = false;
        }

        public async UniTask<AudioClip> Synthesize(string text, CancellationToken ct = default)
        {
            if (!IsAuthorized)
                throw new TTSServiceException("Not authorized — call Authorize() first.");
            if (string.IsNullOrWhiteSpace(text))
                throw new TTSServiceException("Cannot synthesize empty text.");

            string url = $"{BaseUrl}/text-to-speech/{voiceId}?output_format=mp3_44100_128";
            byte[] payload = Encoding.UTF8.GetBytes(BuildRequestJson(text));

            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(payload);
            request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.MPEG);
            request.SetRequestHeader("xi-api-key", ApiKey);
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = timeoutSeconds;

            try
            {
                await request.SendWebRequest().WithCancellation(ct);
            }
            catch (UnityWebRequestException e)
            {
                throw new TTSServiceException($"ElevenLabs request failed: {DescribeError(e)}", e);
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip == null || clip.length <= 0f)
                throw new TTSServiceException("ElevenLabs returned audio that could not be decoded.");

            clip.name = $"TTS_{voiceId}_{text.Substring(0, Mathf.Min(24, text.Length))}";
            return clip;
        }

        string BuildRequestJson(string text)
        {
            return JsonUtility.ToJson(new TtsRequest
            {
                text = text,
                model_id = modelId,
                voice_settings = new VoiceSettings
                {
                    stability = stability,
                    similarity_boost = similarityBoost,
                },
            });
        }

        static string DescribeError(UnityWebRequestException e)
        {
            // Error bodies are JSON like {"detail":{"status":"...","message":"..."}},
            // but "detail" can also be a plain string or an array — parse best-effort.
            if (!string.IsNullOrEmpty(e.Text))
            {
                try
                {
                    var body = JsonUtility.FromJson<ErrorResponse>(e.Text);
                    if (body?.detail != null && !string.IsNullOrEmpty(body.detail.message))
                        return $"{e.ResponseCode} {body.detail.status}: {body.detail.message}";
                }
                catch (Exception)
                {
                    // fall through to the generic description
                }
                return $"{e.ResponseCode} {e.Message}: {e.Text}";
            }
            return $"{e.ResponseCode} {e.Message}";
        }

        [Serializable]
        class TtsRequest
        {
            public string text;
            public string model_id;
            public VoiceSettings voice_settings;
        }

        [Serializable]
        class VoiceSettings
        {
            public float stability;
            public float similarity_boost;
        }

        [Serializable]
        class ErrorResponse
        {
            public ErrorDetail detail;
        }

        [Serializable]
        class ErrorDetail
        {
            public string status;
            public string message;
        }
    }
}
