using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Keys and endpoints for the hosted services the character talks to
    /// (Anthropic for the conversation, ElevenLabs for speech-to-text and
    /// voice). Create one with Assets &gt; Create &gt; Catalyst &gt; Character
    /// Services Config and keep it at <c>Resources/CharacterServicesConfig</c>
    /// in the consuming project, or hand one over with <see cref="Use"/>
    /// before the first service starts. Keys never live in source: anyone
    /// with a build can read them, so a public build should point the base
    /// URLs at a proxy that holds the real keys instead.
    /// </summary>
    [CreateAssetMenu(fileName = ResourcesName, menuName = "Catalyst/Character Services Config")]
    public class CharacterServicesConfig : ScriptableObject
    {
        /// <summary>Resources path the config is looked up at when none was handed over.</summary>
        public const string ResourcesName = "CharacterServicesConfig";

        [Header("Anthropic")]
        [Tooltip("Anthropic API key, or empty when the base URL points at a proxy that adds it.")]
        public string anthropicApiKey = "";

        [Tooltip("Messages API root. Point it at a proxy to keep the key off the device.")]
        public string anthropicBaseUrl = "https://api.anthropic.com/v1";

        [Tooltip("anthropic-version header.")]
        public string anthropicApiVersion = "2023-06-01";

        [Header("ElevenLabs")]
        [Tooltip("ElevenLabs API key (xi-api-key), or empty when the base URL points at a proxy that adds it.")]
        public string elevenLabsApiKey = "";

        [Tooltip("REST root for text-to-speech and batch speech-to-text.")]
        public string elevenLabsBaseUrl = "https://api.elevenlabs.io/v1";

        [Tooltip("WebSocket endpoint of the realtime (Scribe) speech-to-text.")]
        public string elevenLabsRealtimeSttUrl = "wss://api.elevenlabs.io/v1/speech-to-text/realtime";

        private static CharacterServicesConfig _current;
        private static bool _missingReported;

        /// <summary>
        /// The config in effect: whatever <see cref="Use"/> handed over, else
        /// the asset at <c>Resources/CharacterServicesConfig</c>. Null (with
        /// one error) when there is neither.
        /// </summary>
        public static CharacterServicesConfig Current
        {
            get
            {
                if (_current == null)
                    _current = Resources.Load<CharacterServicesConfig>(ResourcesName);
                if (_current == null && !_missingReported)
                {
                    _missingReported = true;
                    Debug.LogError($"CharacterServicesConfig: no asset at Resources/{ResourcesName} and none handed over with Use() — every hosted service will fail to authorize.");
                }
                return _current;
            }
        }

        /// <summary>Overrides the Resources lookup — e.g. a config chosen per build or per user.</summary>
        public static void Use(CharacterServicesConfig config)
        {
            _current = config;
            _missingReported = false;
        }

        public static string AnthropicKey => Current != null ? Trim(Current.anthropicApiKey) : "";
        public static string AnthropicBaseUrl => Current != null && Trim(Current.anthropicBaseUrl).Length > 0 ? TrimSlash(Current.anthropicBaseUrl) : "https://api.anthropic.com/v1";
        public static string AnthropicApiVersion => Current != null && Trim(Current.anthropicApiVersion).Length > 0 ? Trim(Current.anthropicApiVersion) : "2023-06-01";

        public static string ElevenLabsKey => Current != null ? Trim(Current.elevenLabsApiKey) : "";
        public static string ElevenLabsBaseUrl => Current != null && Trim(Current.elevenLabsBaseUrl).Length > 0 ? TrimSlash(Current.elevenLabsBaseUrl) : "https://api.elevenlabs.io/v1";
        public static string ElevenLabsRealtimeSttUrl => Current != null && Trim(Current.elevenLabsRealtimeSttUrl).Length > 0 ? Trim(Current.elevenLabsRealtimeSttUrl) : "wss://api.elevenlabs.io/v1/speech-to-text/realtime";

        private static string Trim(string s) => s == null ? "" : s.Trim();
        private static string TrimSlash(string s) => Trim(s).TrimEnd('/');
    }
}
