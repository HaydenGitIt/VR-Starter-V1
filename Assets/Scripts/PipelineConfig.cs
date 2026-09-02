using UnityEngine;

/// <summary>
/// Single source of truth for everything about the AI pipeline that might
/// change: which engines are used, what they are called, where the server
/// lives, and what audio format is expected.
///
/// WHY THIS EXISTS:
/// Whisper, Ollama and Kokoro are today's choices, not permanent ones. If a
/// better STT appears, or Kokoro's lack of emotional range proves limiting,
/// we want to swap it by editing an asset in the Inspector - not by hunting
/// through scripts for hardcoded values. Nothing in the Unity codebase should
/// ever contain the words "whisper", "ollama" or "kokoro" outside this file.
///
/// Unity never talks to a model directly. It sends a request naming an
/// engine, and the Python server decides what that means. Swapping a model
/// is then a config change on one side or the other, never a code change on
/// both.
///
/// TO CREATE: right-click in the Project window >
/// Create > De-escalation Sim > Pipeline Config
/// </summary>
[CreateAssetMenu(
    fileName = "PipelineConfig",
    menuName = "De-escalation Sim/Pipeline Config",
    order = 0)]
public class PipelineConfig : ScriptableObject
{
    [Header("Server Connection")]
    [Tooltip("Host running the Python pipeline server. 127.0.0.1 when it runs on this same PC (Quest Link).")]
    public string serverHost = "127.0.0.1";

    [Tooltip("Port the Python server listens on.")]
    public int serverPort = 8000;

    [Tooltip("WebSocket path for the streaming conversation session.")]
    public string conversationPath = "/conversation";

    [Tooltip("HTTP path used to check the server is up before starting a session.")]
    public string healthPath = "/health";

    [Tooltip("How long to wait for the server before giving up, in seconds.")]
    public float connectionTimeout = 5f;

    [Header("Engine Selection")]
    [Tooltip("Names an STT engine the server knows how to build. The server maps this string to an implementation.")]
    public string speechToTextEngine = "whisper";

    [Tooltip("Engine-specific model identifier, e.g. a Whisper model size.")]
    public string speechToTextModel = "base.en";

    [Tooltip("Names a dialogue engine the server knows how to build.")]
    public string dialogueEngine = "ollama";

    [Tooltip("Engine-specific model identifier, e.g. an Ollama model tag.")]
    public string dialogueModel = "llama3:8b";

    [Tooltip("Names a TTS engine the server knows how to build.")]
    public string textToSpeechEngine = "kokoro";

    [Tooltip("Engine-specific voice identifier.")]
    public string textToSpeechVoice = "af_heart";

    [Header("Capture Audio Format")]
    [Tooltip("Sample rate Unity captures at. Set this to whatever the chosen STT engine prefers. " +
             "Whisper wants 16000. If a future engine wants something else, change it here only.")]
    public int captureSampleRate = 16000;

    [Tooltip("Channels captured. Speech recognition is virtually always mono.")]
    public int captureChannels = 1;

    [Header("Playback Audio Format")]
    [Tooltip("Sample rate the TTS engine returns audio at. Kokoro outputs 24000. " +
             "Change this if the TTS engine changes.")]
    public int playbackSampleRate = 24000;

    [Tooltip("Channels the TTS engine returns.")]
    public int playbackChannels = 1;

    [Header("Voice Detection")]
    [Tooltip("Level in dB that must be exceeded to begin detecting speech.")]
    public float onsetThresholdDb = -50f;

    [Tooltip("Level below which we count toward the end of the utterance. Keep it BELOW the onset " +
             "threshold - the gap between the two is what stops sentences fragmenting.")]
    public float releaseThresholdDb = -55f;

    [Tooltip("Seconds above the onset threshold before speech is accepted as real.")]
    public float onsetConfirmTime = 0.15f;

    [Tooltip("Seconds of quiet before the utterance is considered finished.")]
    public float hangTime = 0.7f;

    [Tooltip("Seconds of audio kept from before detection, so the first consonant is not clipped.")]
    public float preRollTime = 0.3f;

    [Tooltip("Utterances with less than this much actual speech are discarded as noise.")]
    public float minSpeechLength = 0.4f;

    [Tooltip("Safety cap on a single utterance.")]
    public float maxUtteranceLength = 30f;

    [Header("Scenario Rules")]
    [Tooltip("Maximum turns the trainee gets before the scenario resolves.")]
    public int maxTurns = 8;

    [Tooltip("Number of exchanges that FAIL TO REACH her before she gives up and leaves. " +
             "Under the current design she never escalates, so a strike is a turn that made " +
             "no progress - including turns that were actively poor.")]
    public int strikesAllowed = 3;

    [Tooltip("Number of exchanges that genuinely reach her before the scenario resolves well. " +
             "Balance this against strikesAllowed: too low and the scenario ends before it has " +
             "taught anything, too high and a competent trainee still runs out of turns.")]
    public int progressToResolve = 4;

    // ---------------------------------------------------------------
    // DERIVED VALUES
    // ---------------------------------------------------------------

    public string HealthUrl => $"http://{serverHost}:{serverPort}{healthPath}";
    public string ConversationUrl => $"ws://{serverHost}:{serverPort}{conversationPath}";

    /// <summary>
    /// Catches misconfiguration early rather than letting it surface as
    /// mysterious runtime behaviour.
    /// </summary>
    public bool Validate(out string problem)
    {
        if (captureSampleRate <= 0)
        {
            problem = "Capture sample rate must be greater than zero.";
            return false;
        }

        if (releaseThresholdDb >= onsetThresholdDb)
        {
            problem = "Release threshold must be LOWER than onset threshold, " +
                      "otherwise sentences will fragment at every pause.";
            return false;
        }

        if (hangTime <= 0f || preRollTime < 0f)
        {
            problem = "Hang time must be positive and pre-roll cannot be negative.";
            return false;
        }

        if (strikesAllowed <= 0 || maxTurns <= 0)
        {
            problem = "Turn and strike limits must be greater than zero.";
            return false;
        }

        problem = null;
        return true;
    }

    private void OnValidate()
    {
        // Nudge obviously-wrong values back into range while editing.
        captureSampleRate = Mathf.Max(8000, captureSampleRate);
        captureChannels = Mathf.Clamp(captureChannels, 1, 2);
        playbackChannels = Mathf.Clamp(playbackChannels, 1, 2);
        maxTurns = Mathf.Max(1, maxTurns);
        strikesAllowed = Mathf.Max(1, strikesAllowed);
    }
}
