using UnityEngine;

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
}
