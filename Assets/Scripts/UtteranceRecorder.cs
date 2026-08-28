using System;
using System.IO;
using UnityEngine;

/// <summary>
/// TEST SCAFFOLDING for step 2.
///
/// Saves every utterance the detector produces as a WAV file, so you can
/// listen back and check that the boundaries are right - nothing clipped off
/// the front, nothing cut short mid-sentence, no stray fragments.
///
/// This exists so we verify segmentation BEFORE Whisper is involved. If a
/// transcript later comes back wrong, we want to already know whether the
/// audio going in was good. Delete or disable this once step 2 is verified.
/// </summary>
[RequireComponent(typeof(VoiceActivityDetector))]
public class UtteranceRecorder : MonoBehaviour
{
    [Tooltip("Subfolder name inside the persistent data path.")]
    [SerializeField] private string folderName = "CapturedUtterances";

    [Tooltip("Log the full folder path on startup, so you can find the files.")]
    [SerializeField] private bool logFolderOnStart = true;

    private VoiceActivityDetector detector;
    private string outputDirectory;

    private void Awake()
    {
        detector = GetComponent<VoiceActivityDetector>();

        // persistentDataPath is a writable location that exists on every
        // platform, unlike the project folder which does not exist in a build.
        outputDirectory = Path.Combine(Application.persistentDataPath, folderName);
    }

    private void OnEnable()
    {
        detector.OnUtteranceCaptured += HandleUtterance;
    }

    private void OnDisable()
    {
        detector.OnUtteranceCaptured -= HandleUtterance;
    }

    private void Start()
    {
        if (logFolderOnStart)
        {
            Debug.Log($"[UtteranceRecorder] Saving utterances to:\n{outputDirectory}");
        }
    }

    private void HandleUtterance(float[] samples, AudioFormat format)
    {
        string timestamp = DateTime.Now.ToString("HHmmss");
        string fileName = $"utterance_{detector.UtteranceCount:D3}_{timestamp}.wav";

        string path = WavUtility.SaveToFile(samples, samples.Length, format, outputDirectory, fileName);

        if (path != null)
        {
            float seconds = samples.Length / (float)format.sampleRate;
            Debug.Log($"[UtteranceRecorder] Saved {seconds:F2}s to {fileName}");
        }
    }

    /// <summary>Opens the output folder in the OS file browser. Editor only.</summary>
    public void RevealFolder()
    {
#if UNITY_EDITOR
        Directory.CreateDirectory(outputDirectory);
        UnityEditor.EditorUtility.RevealInFinder(outputDirectory);
#endif
    }

    public string OutputDirectory => outputDirectory;
}
