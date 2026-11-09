using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Glue between the microphone and the speech-to-text service: initializes
    /// the model, feeds every captured utterance through transcription and
    /// raises OnTranscribed with the text. Subscribe from the conversation
    /// logic (e.g. hand the text to ILLMService) — or just watch the console
    /// with logTranscriptions on to smoke-test the chain.
    /// </summary>
    public class SpeechToTextPipeline : MonoBehaviour
    {
        [Tooltip("Utterance source. Found on this GameObject when left empty.")]
        [SerializeField] private MicrophoneRecorder microphone;

        [Tooltip("Transcription backend — any component implementing ISpeechToTextService (WhisperSpeechToTextService, ElevenLabsSpeechToTextService). Found on this GameObject when left empty.")]
        [SerializeField] private MonoBehaviour speechToText;

        [Tooltip("Log every transcription with timing — handy while testing.")]
        [SerializeField] private bool logTranscriptions = true;

        /// <summary>Fired on the main thread with the transcribed text of each utterance.</summary>
        public event Action<string> OnTranscribed;

        public bool IsBusy { get; private set; }

        private ISpeechToTextService _service;

        private void Awake()
        {
            if (microphone == null) microphone = GetComponent<MicrophoneRecorder>();

            _service = speechToText as ISpeechToTextService;
            if (speechToText != null && _service == null)
                Debug.LogError($"SpeechToTextPipeline: {speechToText.GetType().Name} does not implement ISpeechToTextService.", this);
            if (_service == null)
                _service = GetComponent<ISpeechToTextService>();

            if (microphone == null || _service == null)
            {
                Debug.LogError("SpeechToTextPipeline: missing MicrophoneRecorder or speech-to-text service — disabling.", this);
                enabled = false;
            }
        }

        private void OnEnable()
        {
            microphone.OnUtteranceCaptured += HandleUtterance;
            InitializeService().Forget();
        }

        private void OnDisable()
        {
            microphone.OnUtteranceCaptured -= HandleUtterance;
        }

        private async UniTaskVoid InitializeService()
        {
            bool ok = await _service.Initialize(destroyCancellationToken);
            if (!ok)
                Debug.LogError("SpeechToTextPipeline: speech-to-text service failed to initialize.", this);
            else if (logTranscriptions)
                Debug.Log("SpeechToTextPipeline: speech-to-text ready, listening.", this);
        }

        private void HandleUtterance(float[] samples)
        {
            if (IsBusy)
            {
                if (logTranscriptions)
                    Debug.Log("SpeechToTextPipeline: still transcribing previous utterance — dropped one.", this);
                return;
            }
            Transcribe(samples).Forget();
        }

        private async UniTaskVoid Transcribe(float[] samples)
        {
            IsBusy = true;
            float started = Time.realtimeSinceStartup;
            try
            {
                string text = await _service.Transcribe(samples, MicrophoneRecorder.SampleRate, destroyCancellationToken);
                if (logTranscriptions)
                {
                    float seconds = Time.realtimeSinceStartup - started;
                    Debug.Log($"SpeechToTextPipeline: \"{text}\" ({samples.Length / (float)MicrophoneRecorder.SampleRate:0.0}s audio, {seconds:0.00}s transcription)", this);
                }
                if (!string.IsNullOrEmpty(text))
                    OnTranscribed?.Invoke(text);
            }
            catch (Exception e)
            {
                Debug.LogError($"SpeechToTextPipeline: transcription failed — {e.Message}", this);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
