using System;
using System.Collections.Generic;

/// <summary>
/// The contracts between Unity and whatever AI engines sit behind the server.
///
/// THE POINT OF THESE:
/// Every other script in the project depends on these interfaces, never on a
/// concrete engine. Swapping Whisper for something else, or Kokoro for a more
/// expressive TTS, means writing one new adapter class - the conversation
/// state machine, the animation driver and the UI never change, because they
/// only ever knew about the interface.
///
/// This is worth doing now rather than later. Kokoro in particular has no
/// emotion control, and a de-escalation trainer whose parent always sounds
/// calm may well need replacing. Better that the replacement is a contained
/// job than a rewrite.
/// </summary>

// ===================================================================
// SHARED DATA TYPES
// ===================================================================

/// <summary>
/// Describes a block of audio without assuming any particular engine.
/// Capture and playback can differ - Whisper wants 16kHz in, Kokoro
/// produces 24kHz out - so format travels with the data instead of being
/// assumed by whoever receives it.
/// </summary>
[Serializable]
public struct AudioFormat
{
    public int sampleRate;
    public int channels;

    public AudioFormat(int sampleRate, int channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
    }

    public override string ToString() => $"{sampleRate}Hz/{channels}ch";
}

/// <summary>What the STT engine returns.</summary>
public struct TranscriptResult
{
    public string text;
    public bool isFinal;

    /// <summary>Engine confidence 0..1 where available, negative where not reported.</summary>
    public float confidence;

    /// <summary>Round-trip time, for latency budgeting.</summary>
    public float elapsedSeconds;

    public bool IsEmpty => string.IsNullOrWhiteSpace(text);
}

/// <summary>
/// How the trainee's response was judged. Deliberately named for the
/// pedagogy rather than for any model's tag vocabulary, so changing the
/// prompt or the model does not ripple into the game logic.
///
/// NOTE: there is no "Escalated" value. The parent has an agitation ceiling
/// and never gets worse - a poorly handled exchange leaves her agitated and
/// more withdrawn, rather than angrier. So an exchange either reaches her or
/// it does not, and failing to reach her is what counts against the trainee.
/// </summary>
public enum DeescalationVerdict
{
    /// <summary>
    /// No verdict could be parsed from the model output. Logged, but NOT
    /// counted as a strike - a model formatting glitch must never fail a
    /// trainee who may have responded perfectly well.
    /// </summary>
    Unknown,

    /// <summary>The trainee reached her. Progress was made.</summary>
    Deescalated,

    /// <summary>
    /// The trainee did not reach her. A strike, but she stays where she is.
    /// </summary>
    Neutral,

    /// <summary>
    /// The trainee made it worse. A strike, and she moves back one level. She
    /// never goes past her starting state, so there is no matching emotion.
    /// </summary>
    Escalated
}

/// <summary>
/// The parent's emotional state, used to drive animation and potentially
/// voice selection. Kept separate from the verdict: how the parent FEELS is
/// not the same as how the trainee PERFORMED.
///
/// Agitated is the ceiling. There is deliberately no "Angry" state - the
/// simulation must stay safe for trainees, and a parent who withdraws is
/// both more realistic and more affecting than one who shouts.
/// </summary>
public enum ParentEmotion
{
    Unknown,

    /// <summary>Default state. Tense, clipped, guarded.</summary>
    Agitated,

    /// <summary>
    /// Fear or shame has surfaced. NOT a worse state than agitated - a
    /// different register, and often the turning point. Trainees commonly
    /// mishandle it by retreating into procedure exactly when warmth is needed.
    /// </summary>
    Distressed,

    /// <summary>Beginning to accept the trainee is trying.</summary>
    Softening,

    /// <summary>Resolved, or close to it.</summary>
    Calm
}

/// <summary>
/// One streamed piece of a dialogue response. The engine emits tags first,
/// then dialogue text in fragments, so gameplay can react before any audio
/// has been synthesised.
/// </summary>
public struct DialogueChunk
{
    public enum ChunkKind
    {
        /// <summary>Verdict and emotion, arriving before any dialogue.</summary>
        Metadata,

        /// <summary>A fragment of spoken text, usually one sentence.</summary>
        Text,

        /// <summary>Generation finished normally.</summary>
        Complete,

        /// <summary>Generation was cancelled or failed.</summary>
        Aborted
    }

    public ChunkKind kind;
    public string text;
    public DeescalationVerdict verdict;
    public ParentEmotion emotion;
    public int turnId;
}

/// <summary>A block of synthesised speech audio belonging to a turn.</summary>
public struct SpeechChunk
{
    public float[] samples;
    public AudioFormat format;
    public int turnId;
    public bool isFinal;
}

// ===================================================================
// SERVICE INTERFACES
// ===================================================================

/// <summary>Turns captured audio into text. Implementation could be Whisper, or anything else.</summary>
public interface ISpeechToTextService
{
    /// <summary>Audio format this engine expects. Capture should match it.</summary>
    AudioFormat RequiredInputFormat { get; }

    bool IsReady { get; }

    /// <summary>Submit an utterance for transcription.</summary>
    void Transcribe(float[] samples, AudioFormat format, Action<TranscriptResult> onResult, Action<string> onError);

    /// <summary>Abandon any in-flight transcription.</summary>
    void Cancel();
}

/// <summary>Generates the parent's replies. Implementation could be Ollama, or anything else.</summary>
public interface IDialogueService
{
    bool IsReady { get; }

    /// <summary>
    /// Send the trainee's line and stream back the response.
    /// Metadata (verdict and emotion) must arrive before any text, so the
    /// state machine can act on a third strike before a word is spoken.
    /// </summary>
    void GenerateReply(string traineeUtterance, int turnId, Action<DialogueChunk> onChunk, Action<string> onError);

    /// <summary>Stop generating immediately. Used when the scenario cuts to an ending.</summary>
    void Cancel();

    /// <summary>Clear conversation history, ready for a new run.</summary>
    void ResetConversation();
}

/// <summary>Turns text into speech audio. Implementation could be Kokoro, or anything else.</summary>
public interface ITextToSpeechService
{
    /// <summary>Format this engine produces. Playback must adapt to it, not assume it.</summary>
    AudioFormat OutputFormat { get; }

    bool IsReady { get; }

    /// <summary>
    /// Whether this engine can vary delivery by emotion. Kokoro cannot;
    /// a replacement might. Callers check this rather than assuming.
    /// </summary>
    bool SupportsEmotion { get; }

    /// <summary>
    /// Synthesise a fragment. Audio arrives in chunks so playback can begin
    /// before the whole line is rendered.
    /// </summary>
    void Synthesise(string text, ParentEmotion emotion, int turnId, Action<SpeechChunk> onChunk, Action<string> onError);

    void Cancel();
}

/// <summary>
/// One recorded exchange, for the debrief screen.
/// </summary>
[Serializable]
public struct ConversationTurn
{
    public int turnNumber;
    public string traineeText;
    public string parentText;
    public DeescalationVerdict verdict;
    public ParentEmotion emotion;
    public float responseLatencySeconds;
}

/// <summary>
/// Running record of a scenario attempt. Survives into the debrief.
/// </summary>
public class ConversationLog
{
    public readonly List<ConversationTurn> Turns = new List<ConversationTurn>();

    /// <summary>
    /// Turns that failed to reach her. Reaching the configured limit ends
    /// the scenario with her disengaging and leaving.
    /// </summary>
    public int StrikeCount { get; private set; }

    /// <summary>Turns that made genuine progress.</summary>
    public int ProgressCount { get; private set; }

    /// <summary>
    /// Turns where the model output could not be parsed. Deliberately kept
    /// out of the scoring - these represent a technical fault, not trainee
    /// performance - but tracked so a broken prompt shows up in the data
    /// rather than silently distorting results.
    /// </summary>
    public int UnparsedCount { get; private set; }

    public void Record(ConversationTurn turn)
    {
        Turns.Add(turn);

        switch (turn.verdict)
        {
            case DeescalationVerdict.Neutral:
                StrikeCount++;
                break;

            case DeescalationVerdict.Escalated:
                StrikeCount++;
                ProgressCount = Math.Max(0, ProgressCount - 1);
                break;

            case DeescalationVerdict.Deescalated:
                ProgressCount++;
                break;

            case DeescalationVerdict.Unknown:
                UnparsedCount++;
                break;
        }
    }

    public void Clear()
    {
        Turns.Clear();
        StrikeCount = 0;
        ProgressCount = 0;
        UnparsedCount = 0;
    }
}
