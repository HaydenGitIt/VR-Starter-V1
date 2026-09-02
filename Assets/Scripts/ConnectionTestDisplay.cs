using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// TEST SCAFFOLDING for Section A.
///
/// Verifies the connection works before any audio, microphone or VR is
/// involved. Press Play in a flat scene, use the on-screen buttons, and watch
/// the Console.
///
/// This proves: the socket opens, commands go out, JSON comes back, and the
/// tags arrive before the audio. Once that holds, Section B can wire up
/// playback knowing the transport is sound.
///
/// Delete or disable once Section B is working.
/// </summary>
[RequireComponent(typeof(ConversationClient))]
public class ConnectionTestDisplay : MonoBehaviour
{
    private ConversationClient client;

    // Optional. Present from Section B onward.
    private StreamingAudioPlayer player;

    // Optional. Present from Section C onward.
    private ConversationController controller;
    private VoiceActivityDetector detector;
    private MicrophoneCapture microphone;

    private string status = "starting...";
    private string lastMel = "";
    private string lastTags = "";
    private string lastTranscript = "";
    private int audioFrames;
    private float audioSeconds;
    private string typed = "I hear you.";

    private void Awake()
    {
        client = GetComponent<ConversationClient>();
        player = FindInScene<StreamingAudioPlayer>();
        controller = FindInScene<ConversationController>();
        detector = FindInScene<VoiceActivityDetector>();
        microphone = FindInScene<MicrophoneCapture>();
    }

    private void OnEnable()
    {
        client.OnReady += HandleReady;
        client.OnTags += HandleTags;
        client.OnTranscript += HandleTranscript;
        client.OnReply += HandleReply;
        client.OnAudio += HandleAudio;
        client.OnEnding += HandleEnding;
        client.OnTurnEnd += HandleTurnEnd;
        client.OnError += HandleError;
        client.OnDisconnected += HandleDisconnected;
    }

    private void OnDisable()
    {
        client.OnReady -= HandleReady;
        client.OnTags -= HandleTags;
        client.OnTranscript -= HandleTranscript;
        client.OnReply -= HandleReply;
        client.OnAudio -= HandleAudio;
        client.OnEnding -= HandleEnding;
        client.OnTurnEnd -= HandleTurnEnd;
        client.OnError -= HandleError;
        client.OnDisconnected -= HandleDisconnected;
    }

    private void HandleReady() => status = "connected and ready";

    private void HandleTags(DeescalationVerdict verdict, ParentEmotion emotion)
    {
        // Note the ORDER this appears in the Console relative to audio frames.
        // Tags must always come first - that is what makes the strike cutoff
        // work later.
        lastTags = $"{verdict} / {emotion}";
        audioFrames = 0;
        audioSeconds = 0f;
    }

    private void HandleTranscript(string text) => lastTranscript = text;

    private void HandleReply(string text) => lastMel = text;

    private void HandleAudio(float[] samples)
    {
        audioFrames++;
        audioSeconds += samples.Length / (float)client.PlaybackSampleRate;
    }

    private void HandleEnding(bool resolved)
    {
        status = resolved ? "ENDED: she was reached" : "ENDED: she gave up";
    }

    private void HandleTurnEnd(bool ended)
    {
        if (!ended) status = "waiting for your turn";
    }

    private void HandleError(string message) => status = $"error: {message}";

    private void HandleDisconnected() => status = "disconnected";

    private void OnGUI()
    {
        const float pad = 20f;
        const float width = 560f;

        GUIStyle label = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        GUIStyle bold = new GUIStyle(label) { fontStyle = FontStyle.Bold };

        float y = pad;

        GUI.Label(new Rect(pad, y, width, 24), $"Status: {status}", bold); y += 28;
        GUI.Label(new Rect(pad, y, width, 24), $"Connected: {client.IsConnected}", label); y += 24;
        GUI.Label(new Rect(pad, y, width, 24), $"Playback rate: {client.PlaybackSampleRate} Hz", label); y += 30;

        GUI.Label(new Rect(pad, y, width, 24), $"Tags: {lastTags}", bold); y += 26;
        GUI.Label(new Rect(pad, y, width, 24),
            $"Audio received: {audioFrames} frame(s), {audioSeconds:F2}s", label); y += 30;

        GUI.Label(new Rect(pad, y, width, 24), $"Heard: {lastTranscript}", label); y += 26;

        GUI.Label(new Rect(pad, y, width, 60), $"MEL: {lastMel}", label); y += 66;

        if (player != null)
        {
            GUI.Label(new Rect(pad, y, width, 24),
                $"Speaking: {player.IsSpeaking}    buffered: {player.BufferedSeconds:F2}s "
                + $"({player.BufferedSamples}/{player.BufferCapacitySamples})", bold);
            y += 30;
        }

        if (controller != null)
        {
            // The loop state and the VAD state should agree: the microphone is
            // open ONLY while Listening. If VAD is anything but Gated while the
            // controller says Speaking, the gate has failed and Mel is about to
            // start transcribing herself.
            GUI.Label(new Rect(pad, y, width, 26),
                $"LOOP: {controller.State}", new GUIStyle(bold) { fontSize = 19 });
            y += 30;

            if (detector != null)
            {
                GUI.Label(new Rect(pad, y, width, 24),
                    $"VAD: {detector.State}    utterances: {detector.UtteranceCount}", label);
                y += 26;
            }

            if (microphone != null)
            {
                GUI.Label(new Rect(pad, y, width, 24),
                    $"Mic level: {microphone.CurrentDb:F1} dB    ({microphone.Format})", label);
                y += 30;
            }
        }

        if (GUI.Button(new Rect(pad, y, 160, 32), "Begin scenario"))
        {
            client.Begin();
        }

        if (GUI.Button(new Rect(pad + 170, y, 120, 32), "Reset"))
        {
            client.ResetSession();
        }

        if (GUI.Button(new Rect(pad + 300, y, 140, 32), "Reconnect"))
        {
            // Full sequence, not just Connect() - going straight to the socket
            // would skip the health check and its sample rate comparison.
            client.ConnectWithHealthCheck();
        }

        y += 42;

        GUI.Label(new Rect(pad, y, 120, 24), "Say to Mel:", label);
        typed = GUI.TextField(new Rect(pad + 100, y, 340, 24), typed);

        if (GUI.Button(new Rect(pad + 450, y, 90, 24), "Send"))
        {
            client.SendText(typed);
        }
    }

    /// <summary>
    /// Finds a component in the scene, using whichever API this Unity version
    /// provides. FindObjectOfType was deprecated in Unity 2023/6 in favour of
    /// FindFirstObjectByType, and using the old name produces warnings there
    /// while the new name does not exist on older versions.
    /// </summary>
    private static T FindInScene<T>() where T : Object
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<T>();
#else
        return FindObjectOfType<T>();
#endif
    }
}
