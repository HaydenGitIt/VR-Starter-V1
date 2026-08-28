using UnityEngine;

/// <summary>
/// Draws a live microphone level meter in the Game view, using Unity's
/// immediate-mode GUI. This needs NO Canvas, no UI objects and no setup -
/// just add it to the same GameObject as MicrophoneCapture and press Play.
///
/// This is a TEST TOOL, not part of the finished experience. Immediate-mode
/// GUI does not render in VR and is inefficient, so this gets deleted or
/// disabled once step 1 is verified. It exists purely so we can confirm the
/// microphone works before building anything on top of it.
/// </summary>
[RequireComponent(typeof(MicrophoneCapture))]
public class MicLevelDebugDisplay : MonoBehaviour
{
    [Tooltip("Level in dB that the meter treats as the bottom of its range.")]
    [SerializeField] private float meterFloorDb = -80f;

    [Tooltip("Draw a marker at this dB value. If a PipelineConfig is reachable, the onset " +
             "threshold from that config is used instead, so the meter always shows the " +
             "value the detector is actually using.")]
    [SerializeField] private float candidateThresholdDb = -50f;

    private float MarkerDb =>
        (capture != null && capture.Config != null)
            ? capture.Config.onsetThresholdDb
            : candidateThresholdDb;

    private MicrophoneCapture capture;

    // Optional - if a detector is present we show its state too.
    private VoiceActivityDetector detector;
    private UtteranceRecorder recorder;

    // Smoothed value so the bar does not flicker wildly frame to frame.
    private float displayedLevel;

    private GUIStyle labelStyle;
    private Texture2D barTexture;
    private Texture2D backgroundTexture;
    private Texture2D markerTexture;

    private void Awake()
    {
        capture = GetComponent<MicrophoneCapture>();

        // These are optional. In step 1 they do not exist yet; from step 2
        // onward they do, and we display their state as well.
        detector = GetComponent<VoiceActivityDetector>();
        recorder = GetComponent<UtteranceRecorder>();
    }

    private void Update()
    {
        if (capture == null) return;

        float target = capture.GetNormalisedLevel(meterFloorDb);

        // Rise quickly, fall slowly - the standard behaviour for audio meters,
        // because it makes brief peaks visible rather than flashing past.
        float speed = target > displayedLevel ? 25f : 6f;
        displayedLevel = Mathf.MoveTowards(displayedLevel, target, speed * Time.deltaTime);
    }

    private void OnGUI()
    {
        EnsureResources();

        const float padding = 20f;
        const float width = 420f;
        const float barHeight = 34f;

        float x = padding;
        float y = padding;

        GUI.Label(new Rect(x, y, width, 24f),
            capture.IsCapturing
                ? $"Device: {capture.ActiveDeviceName}"
                : "Device: NOT CAPTURING",
            labelStyle);
        y += 26f;

        GUI.Label(new Rect(x, y, width, 24f),
            $"Sample rate: {capture.SampleRate} Hz", labelStyle);
        y += 26f;

        GUI.Label(new Rect(x, y, width, 24f),
            $"Level: {capture.CurrentDb:F1} dB    Peak RMS: {capture.PeakRms:F4}", labelStyle);
        y += 30f;

        // Meter background
        GUI.DrawTexture(new Rect(x, y, width, barHeight), backgroundTexture);

        // Filled portion
        float fillWidth = Mathf.Clamp01(displayedLevel) * width;
        if (fillWidth > 1f)
        {
            GUI.DrawTexture(new Rect(x, y, fillWidth, barHeight), barTexture);
        }

        // Threshold marker
        float markerPos = Mathf.InverseLerp(meterFloorDb, 0f, MarkerDb);
        GUI.DrawTexture(new Rect(x + (markerPos * width) - 1f, y - 4f, 2f, barHeight + 8f), markerTexture);

        y += barHeight + 8f;

        GUI.Label(new Rect(x, y, width, 24f),
            $"{meterFloorDb:F0} dB {"".PadRight(28)} 0 dB", labelStyle);
        y += 30f;

        GUI.Label(new Rect(x, y, width, 24f),
            $"White marker = {MarkerDb:F0} dB candidate threshold", labelStyle);
        y += 30f;

        if (GUI.Button(new Rect(x, y, 140f, 30f), "Reset Peak"))
        {
            capture.ResetPeak();
        }

        if (detector == null) return;

        y += 42f;

        // Colour-code the state so it is readable at a glance while talking.
        Color stateColor = detector.State switch
        {
            VoiceActivityDetector.VadState.Speaking => new Color(0.4f, 1f, 0.5f),
            VoiceActivityDetector.VadState.Onset    => new Color(1f, 0.9f, 0.4f),
            VoiceActivityDetector.VadState.Trailing => new Color(1f, 0.65f, 0.3f),
            VoiceActivityDetector.VadState.Gated    => new Color(1f, 0.4f, 0.4f),
            _                                       => new Color(0.6f, 0.6f, 0.65f)
        };

        GUIStyle stateStyle = new GUIStyle(labelStyle)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            normal = { textColor = stateColor }
        };

        GUI.Label(new Rect(x, y, width, 28f), $"VAD: {detector.State}", stateStyle);
        y += 30f;

        if (detector.State == VoiceActivityDetector.VadState.Speaking ||
            detector.State == VoiceActivityDetector.VadState.Trailing)
        {
            GUI.Label(new Rect(x, y, width, 24f),
                $"Recording: {detector.CurrentUtteranceLength:F2}s", labelStyle);
        }
        y += 26f;

        GUI.Label(new Rect(x, y, width, 24f),
            $"Utterances captured: {detector.UtteranceCount}", labelStyle);
        y += 30f;

        if (recorder != null && GUI.Button(new Rect(x, y, 200f, 30f), "Open Recordings Folder"))
        {
            recorder.RevealFolder();
        }
    }

    private void EnsureResources()
    {
        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                normal = { textColor = Color.white }
            };
        }

        if (barTexture == null) barTexture = MakeTexture(new Color(0.3f, 0.85f, 0.4f));
        if (backgroundTexture == null) backgroundTexture = MakeTexture(new Color(0.12f, 0.12f, 0.14f, 0.9f));
        if (markerTexture == null) markerTexture = MakeTexture(Color.white);
    }

    private static Texture2D MakeTexture(Color color)
    {
        Texture2D tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }
}
