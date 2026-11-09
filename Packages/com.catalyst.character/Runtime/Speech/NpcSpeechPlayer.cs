using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Turns a stream of NPC reply text into speech on the character's
    /// AudioSource. Feed it LLM text as it arrives (FeedText per delta, then
    /// EndOfText — or Speak for a whole string at once): complete sentences
    /// are cut off and sent to ElevenLabs TTS immediately, so sentence 2 is
    /// synthesizing while sentence 1 already plays, and finished clips play
    /// strictly in order on the target AudioSource.
    ///
    /// Add to the character next to its voice AudioSource — the same source
    /// the LipSyncAnalyzer taps, so the mouth animates from this speech for
    /// free. The source is also registered in the MicrophoneRecorder's
    /// suppressWhilePlaying list automatically so the NPC does not transcribe
    /// itself through the mic.
    /// </summary>
    public class NpcSpeechPlayer : MonoBehaviour
    {
        [Tooltip("Character voice output. Found on this GameObject when left empty.")]
        [SerializeField] private AudioSource audioSource;

        [Header("ElevenLabs voice")]
        [Tooltip("Voice id — browse at https://elevenlabs.io/app/voice-library.")]
        [SerializeField] private string voiceId = "21m00Tcm4TlvDq8ikWAM";

        [Tooltip("eleven_flash_v2_5 keeps the conversation snappy; eleven_multilingual_v2 sounds richer but adds latency.")]
        [SerializeField] private string ttsModelId = "eleven_flash_v2_5";

        [Range(0f, 1f)]
        [SerializeField] private float stability = 0.5f;

        [Range(0f, 1f)]
        [SerializeField] private float similarityBoost = 0.75f;

        [Header("Behaviour")]
        [Tooltip("Add the voice AudioSource to the MicrophoneRecorder's suppress list on Awake.")]
        [SerializeField] private bool registerForMicSuppression = true;

        [Tooltip("Text pieces shorter than this are merged with the following sentence before synthesis — avoids tiny choppy clips.")]
        [SerializeField] private int minSentenceChars = 12;

        [Header("Loudness")]
        [Tooltip("Level each synthesized sentence to the target below before it plays. Every sentence is its own ElevenLabs clip, and hesitant lines (\"Yeah... I don't know\") come back far quieter than plain ones — measured on Sam's voice: -34 vs -21 dBFS, against -18 for his recorded intro lines.")]
        [SerializeField] private bool levelLoudness = true;

        [Tooltip("Gated RMS (dBFS) to level to — match the character's recorded intro lines as Unity plays them (Normalize on). Sam's median -18, Abby's -13.")]
        [SerializeField] private float targetLoudnessDb = -18f;

        [Tooltip("Most a quiet clip is turned up (dB); peaks beyond full scale are soft-limited.")]
        [SerializeField] private float maxBoostDb = 18f;

        [Tooltip("Most a loud clip is turned down (dB). 0 = only ever boost, so shouting keeps its punch.")]
        [SerializeField] private float maxCutDb = 0f;

        [SerializeField] private bool verboseLogging = false;

        /// <summary>True from the first clip starting until the queue drains (or Cancel).</summary>
        public bool IsSpeaking { get; private set; }

        public event Action OnSpeechStarted;
        public event Action OnSpeechFinished;

        /// <summary>A sentence was handed to TTS (chars). Timing hook.</summary>
        public event Action<int> OnSentenceQueued;

        /// <summary>A sentence's clip finished downloading (clip seconds). Timing hook.</summary>
        public event Action<float> OnClipReady;

        /// <summary>A clip started playing (clip seconds, its loudness as synthesized in dBFS, the level gain applied in dB). Timing hook.</summary>
        public event Action<float, float, float> OnClipStarted;

        /// <summary>True while the OnSpeechFinished being raised comes from Cancel (barge-in / scenario end) rather than the queue draining.</summary>
        public bool LastSpeechCancelled { get; private set; }

        private ITTSService _tts;
        private bool _authFailed;
        private readonly StringBuilder _textBuffer = new StringBuilder();
        private readonly Queue<UniTask<SpeechClip>> _clipQueue = new Queue<UniTask<SpeechClip>>();
        private CancellationTokenSource _speechCts;
        private bool _playbackRunning;
        private AudioClip _currentClip;

        private static readonly char[] SentenceTerminators = { '.', '!', '?', '…', '\n' };

        /// <summary>A synthesized sentence, with what levelling did to it (for the stats).</summary>
        private readonly struct SpeechClip
        {
            public readonly AudioClip clip;
            public readonly float loudnessDb;
            public readonly float gainDb;

            public SpeechClip(AudioClip clip, float loudnessDb, float gainDb)
            {
                this.clip = clip;
                this.loudnessDb = loudnessDb;
                this.gainDb = gainDb;
            }
        }

        private void Awake()
        {
            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                Debug.LogError("NpcSpeechPlayer: no AudioSource assigned or found — disabling.", this);
                enabled = false;
                return;
            }

            if (registerForMicSuppression)
            {
                MicrophoneRecorder mic = FindFirstObjectByType<MicrophoneRecorder>();
                if (mic != null)
                {
                    var list = new List<AudioSource>(mic.suppressWhilePlaying ?? Array.Empty<AudioSource>());
                    if (!list.Contains(audioSource))
                    {
                        list.Add(audioSource);
                        mic.suppressWhilePlaying = list.ToArray();
                    }
                }
            }
        }

        private void Start()
        {
            InitTts().Forget();
        }

        private async UniTaskVoid InitTts()
        {
            _tts = new ElevenLabsTTSService(voiceId, ttsModelId, stability, similarityBoost);
            bool ok = await _tts.Authorize(destroyCancellationToken);
            _authFailed = !ok;
            if (!ok)
                Debug.LogError("NpcSpeechPlayer: ElevenLabs TTS authorization failed — the NPC will stay silent.", this);
            else if (verboseLogging)
                Debug.Log("NpcSpeechPlayer: TTS ready.", this);
        }

        // ------------------------------------------------------------------ text input

        /// <summary>Stream text fragments in; complete sentences are spoken as they form.</summary>
        public void FeedText(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            _textBuffer.Append(delta);
            FlushSentences(flushAll: false);
        }

        /// <summary>Call when the text stream is complete to speak the remainder.</summary>
        public void EndOfText()
        {
            FlushSentences(flushAll: true);
        }

        /// <summary>Convenience for non-streamed text: speaks the whole string (sentence by sentence).</summary>
        public void Speak(string text)
        {
            FeedText(text);
            EndOfText();
        }

        /// <summary>Barge-in: stop playback, drop queued sentences and cancel in-flight synthesis.</summary>
        public void Cancel()
        {
            _speechCts?.Cancel();
            _speechCts?.Dispose();
            _speechCts = null;

            while (_clipQueue.Count > 0)
                DiscardClip(_clipQueue.Dequeue()).Forget();
            _textBuffer.Clear();

            if (audioSource != null) audioSource.Stop();
            if (_currentClip != null)
            {
                Destroy(_currentClip);
                _currentClip = null;
            }

            if (IsSpeaking)
            {
                IsSpeaking = false;
                LastSpeechCancelled = true;
                OnSpeechFinished?.Invoke();
            }
        }

        private void OnDisable()
        {
            Cancel();
        }

        // ------------------------------------------------------------------ sentence chunking

        private void FlushSentences(bool flushAll)
        {
            // Scan the StringBuilder in place — materializing the whole buffer as
            // a string on every streamed delta was O(n²) garbage over a reply.
            int length = _textBuffer.Length;
            int segmentStart = 0;

            for (int i = 0; i < length - 1; i++)
            {
                bool boundary = Array.IndexOf(SentenceTerminators, _textBuffer[i]) >= 0 && char.IsWhiteSpace(_textBuffer[i + 1]);
                if (!boundary) continue;
                if (i - segmentStart + 1 < minSentenceChars) continue; // too short — merge with the next sentence

                EnqueueSentence(_textBuffer.ToString(segmentStart, i - segmentStart + 1));
                segmentStart = i + 1;
            }

            if (flushAll)
            {
                if (segmentStart < length)
                    EnqueueSentence(_textBuffer.ToString(segmentStart, length - segmentStart));
                _textBuffer.Clear();
            }
            else if (segmentStart > 0)
            {
                _textBuffer.Remove(0, segmentStart);
            }
        }

        private void EnqueueSentence(string sentence)
        {
            sentence = sentence.Trim();
            if (sentence.Length == 0) return;
            OnSentenceQueued?.Invoke(sentence.Length);

            _speechCts ??= new CancellationTokenSource();
            CancellationToken ct = _speechCts.Token;

            // Kick off synthesis right away; playback order is the queue order.
            _clipQueue.Enqueue(SynthesizeSentence(sentence, ct));

            if (!_playbackRunning)
                PlaybackLoop(ct).Forget();
        }

        // ------------------------------------------------------------------ synthesis + playback

        private async UniTask<SpeechClip> SynthesizeSentence(string sentence, CancellationToken ct)
        {
            // TTS may still be authorizing when the first sentence arrives.
            while (_tts == null || (!_tts.IsAuthorized && !_authFailed))
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            if (_authFailed)
                throw new TTSServiceException("TTS not authorized.");

            if (verboseLogging)
                Debug.Log($"NpcSpeechPlayer: synthesizing \"{sentence}\"", this);
            AudioClip clip = await _tts.Synthesize(sentence, ct);
            float loudnessDb = float.NaN, gainDb = 0f;
            if (clip != null && levelLoudness)
                LevelLoudness(clip, out loudnessDb, out gainDb);
            if (verboseLogging && clip != null)
                Debug.Log($"NpcSpeechPlayer: \"{sentence}\" at {loudnessDb:0.0} dBFS, gain {gainDb:+0.0;-0.0} dB", this);
            OnClipReady?.Invoke(clip != null ? clip.length : 0f);
            return new SpeechClip(clip, loudnessDb, gainDb);
        }

        // ------------------------------------------------------------------ loudness

        private const float LevelBlockSeconds = 0.05f;
        private const float LevelAbsoluteGateDb = -50f;   // blocks below this are silence
        private const float LevelRelativeGateDb = 20f;    // ...and so are blocks this far under the rest
        private const float LimiterKnee = 0.8f;           // soft limit only the top of the range

        /// <summary>
        /// Scales the clip in place so its gated RMS lands on targetLoudnessDb
        /// (within maxBoostDb / maxCutDb). Peak normalisation would not help:
        /// a mumbled line with one sharp consonant already peaks high and stays
        /// quiet. Any peak the boost pushes past the knee is soft-limited, so
        /// at most the odd plosive gets rounded off instead of clipping.
        /// </summary>
        private void LevelLoudness(AudioClip clip, out float loudnessDb, out float gainDb)
        {
            loudnessDb = float.NaN;
            gainDb = 0f;
            var samples = new float[clip.samples * clip.channels];
            if (!clip.GetData(samples, 0))
                return;

            loudnessDb = GatedRmsDb(samples, Mathf.Max(1, Mathf.RoundToInt(clip.frequency * clip.channels * LevelBlockSeconds)));
            if (float.IsNaN(loudnessDb))
                return;   // nothing above the silence gate
            gainDb = Mathf.Clamp(targetLoudnessDb - loudnessDb, -Mathf.Max(0f, maxCutDb), Mathf.Max(0f, maxBoostDb));
            if (Mathf.Abs(gainDb) < 0.1f)
            {
                gainDb = 0f;
                return;
            }

            float gain = Mathf.Pow(10f, gainDb / 20f);
            for (int i = 0; i < samples.Length; i++)
            {
                float x = samples[i] * gain;
                float a = Mathf.Abs(x);
                if (a > LimiterKnee)
                {
                    float over = (a - LimiterKnee) / (1f - LimiterKnee);
                    x = Mathf.Sign(x) * (LimiterKnee + (1f - LimiterKnee) * (float)Math.Tanh(over));
                }
                samples[i] = x;
            }
            clip.SetData(samples, 0);
        }

        /// <summary>Mean power of the non-silent blocks, in dBFS; NaN when every block is silence.</summary>
        private static float GatedRmsDb(float[] samples, int blockSize)
        {
            int blocks = samples.Length / blockSize;
            var power = new double[blocks];
            double sum = 0d;
            int kept = 0;
            double absGate = Math.Pow(10d, LevelAbsoluteGateDb / 10d);
            for (int b = 0; b < blocks; b++)
            {
                double p = 0d;
                for (int i = b * blockSize, end = i + blockSize; i < end; i++)
                    p += (double)samples[i] * samples[i];
                p /= blockSize;
                power[b] = p;
                if (p > absGate) { sum += p; kept++; }
            }
            if (kept == 0)
                return float.NaN;

            double relGate = sum / kept * Math.Pow(10d, -LevelRelativeGateDb / 10d);
            sum = 0d;
            kept = 0;
            for (int b = 0; b < blocks; b++)
            {
                if (power[b] <= absGate || power[b] <= relGate) continue;
                sum += power[b];
                kept++;
            }
            return kept == 0 ? float.NaN : (float)(10d * Math.Log10(sum / kept));
        }

        private async UniTaskVoid PlaybackLoop(CancellationToken ct)
        {
            _playbackRunning = true;
            try
            {
                while (_clipQueue.Count > 0 && !ct.IsCancellationRequested)
                {
                    SpeechClip speech;
                    try
                    {
                        speech = await _clipQueue.Dequeue();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"NpcSpeechPlayer: synthesis failed — {e.Message}", this);
                        continue; // skip this sentence, keep speaking the rest
                    }
                    AudioClip clip = speech.clip;
                    if (clip == null || ct.IsCancellationRequested)
                        continue;

                    if (!IsSpeaking)
                    {
                        IsSpeaking = true;
                        OnSpeechStarted?.Invoke();
                    }

                    _currentClip = clip;
                    audioSource.clip = clip;
                    audioSource.Play();
                    OnClipStarted?.Invoke(clip.length, speech.loudnessDb, speech.gainDb);
                    await UniTask.WaitWhile(() => audioSource != null && audioSource.isPlaying, PlayerLoopTiming.Update, ct);

                    _currentClip = null;
                    Destroy(clip);
                }
            }
            catch (OperationCanceledException) { /* barge-in */ }
            finally
            {
                _playbackRunning = false;
                if (IsSpeaking && _clipQueue.Count == 0)
                {
                    IsSpeaking = false;
                    LastSpeechCancelled = false;
                    OnSpeechFinished?.Invoke();
                }
            }
        }

        private static async UniTaskVoid DiscardClip(UniTask<SpeechClip> task)
        {
            try
            {
                SpeechClip speech = await task;
                if (speech.clip != null) Destroy(speech.clip);
            }
            catch (Exception) { /* cancelled or failed — nothing to free */ }
        }
    }
}
