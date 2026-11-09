namespace catalyst.xr67
{
    /// <summary>
    /// Shared ElevenLabs key and endpoints used by the TTS and STT services,
    /// read from the <see cref="CharacterServicesConfig"/> asset — never from
    /// source.
    /// </summary>
    public static class ElevenLabsApi
    {
        public static string Key => CharacterServicesConfig.ElevenLabsKey;
        public static string BaseUrl => CharacterServicesConfig.ElevenLabsBaseUrl;
        public static string RealtimeSttUrl => CharacterServicesConfig.ElevenLabsRealtimeSttUrl;
    }
}
