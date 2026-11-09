using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace catalyst.xr67
{
    /// <summary>
    /// Voice-activity-gated microphone capture: listens continuously and cuts
    /// the stream into utterances. Speech above the RMS threshold starts a
    /// capture (including a short pre-roll so word onsets are not clipped);
    /// sustained silence ends it and OnUtteranceCaptured fires with mono PCM
    /// at 16 kHz — what whisper and the streaming transcriber both expect.
    ///
    /// The device is opened at a rate it actually supports (many USB mics are
    /// 44.1/48 kHz only and hand Unity a clip of digital silence when asked
    /// for 16 kHz), then downmixed to mono and resampled to 16 kHz here, so
    /// everything downstream sees one fixed format whatever the hardware is.
    ///
    /// Assign the character's voice AudioSource to suppressWhilePlaying so
    /// the NPC does not transcribe its own speech from the headset speakers.
    /// </summary>
    public class MicrophoneRecorder : MonoBehaviour
    {
        /// <summary>Capture rate. Fixed: whisper's native rate, and the block/clip alignment depends on it.</summary>
        public const int SampleRate = 16000;

        private const int BlockSize = 320;              // 20 ms at 16 kHz — one resampled device chunk
        private const float BlockSeconds = BlockSize / (float)SampleRate;
        private const int ClipSeconds = 32;
        private const int BlocksPerSecond = SampleRate / BlockSize;   // 50 — device chunks are 20 ms too

        [Header("Capture")]
        [Tooltip("Microphone device name. Default device when empty.")]
        public string device = "";

        [Tooltip("Start listening automatically when enabled.")]
        public bool listenOnEnable = true;

        [Header("Voice activity detection")]
        [Tooltip("RMS a 20 ms block must exceed to count as speech (capture start).")]
        public float speechThreshold = 0.02f;

        [Tooltip("Lower RMS bar used while a capture is running — hysteresis so quiet stretches of ongoing speech (soft syllables, trailing off) don't count as silence and cut the speaker mid-sentence. Keep well below speechThreshold but above the room's noise floor.")]
        public float keepSpeechThreshold = 0.008f;

        [Tooltip("Log each capture end with its reason and RMS stats — evidence for tuning the thresholds per microphone.")]
        public bool verboseLogging = true;

        [Tooltip("Consecutive voiced blocks (20 ms each) needed to trigger a capture — filters clicks and thumps.")]
        public int speechConfirmBlocks = 3;

        [Tooltip("Seconds of audio kept from before the trigger.")]
        public float preRoll = 0.35f;

        [Tooltip("Seconds of continuous silence that end the utterance. Keep generous for conversational use — a speaker pausing to think mid-sentence must not be cut off; every extra second here also delays the reply by that much.")]
        public float silenceToEnd = 1.5f;

        [Tooltip("Silence window used instead of silenceToEnd while HoldUtteranceOpen is set (speech looks mid-thought — trailing 'um', 'and', comma...). Lets hesitation pauses run long without delaying the reply after complete sentences.")]
        public float silenceToEndExtended = 3f;

        /// <summary>
        /// Set by the transcriber while the speech-so-far looks unfinished —
        /// the silence window stretches to <see cref="silenceToEndExtended"/>
        /// until it is cleared. Reset automatically when a capture ends.
        /// </summary>
        public bool HoldUtteranceOpen { get; set; }

        /// <summary>True while barge-in detection is armed — see <see cref="BeginBargeInDetection"/>.</summary>
        public bool IsBargeInArmed => _bargeIn;

        [Tooltip("Utterances shorter than this (excluding pre-roll) are discarded as noise.")]
        public float minUtterance = 0.4f;

        [Tooltip("Hard cap per utterance — delivered even if the speaker keeps going. Must stay below the 32 s clip ring (incl. preRoll). A slow coaching sentence with thinking pauses can pass 15 s, so keep this generous.")]
        public float maxUtterance = 25f;

        [Header("Suppression")]
        [Tooltip("While any of these plays (e.g. the character's voice), the mic is ignored and any capture in progress is dropped.")]
        public AudioSource[] suppressWhilePlaying;

        public bool IsListening { get; private set; }
        public bool IsCapturing { get; private set; }

        /// <summary>Diagnostics of the capture that just ended — valid inside an OnCaptureEnded handler (ConversationStatsLogic reads them).</summary>
        public float LastCaptureSpeechSeconds { get; private set; }   // excluding pre-roll and the silence window
        public float LastCaptureSilenceWindow { get; private set; }   // the window that ended it; 0 when the length cap did
        public bool LastCaptureHeldOpen { get; private set; }
        public bool LastCaptureEndedBySilence { get; private set; }

        public event Action OnSpeechStarted;

        /// <summary>A finished utterance: mono PCM in -1..1 at <see cref="SampleRate"/>.</summary>
        public event Action<float[]> OnUtteranceCaptured;

        /// <summary>
        /// Streaming tap: fired while a capture is in progress with each chunk
        /// of mono PCM as it is read (the first invocation carries the
        /// pre-roll). Lets streaming transcribers upload audio mid-utterance.
        /// The array is a reused scratch buffer — consume it synchronously,
        /// never keep a reference past the callback.
        /// </summary>
        public event Action<float[]> OnCaptureBlock;

        /// <summary>
        /// Fired whenever a capture ends, whatever the reason. True: it met
        /// minUtterance and OnUtteranceCaptured follows. False: discarded
        /// (noise, suppression, or listening stopped).
        /// </summary>
        public event Action<bool> OnCaptureEnded;

        private AudioClip _clip;
        private int _readPos;                    // in device frames
        private readonly float[] _block = new float[BlockSize];

        // Device-side capture format; the raw chunk is downmixed and resampled
        // into _block, so nothing downstream sees anything but 16 kHz mono.
        private int _deviceRate = SampleRate;
        private int _channels = 1;
        private int _rawFrames = BlockSize;
        private float[] _rawBuffer;

        // Devices often report "any rate supported" and then deliver digital
        // silence at 16 kHz anyway; if that happens, reopen once at 48 kHz.
        private int _forcedRate;
        private bool _rateFallbackTried;
        private bool _needsRateFallback;
        private int _zeroBlocks;

        // Pre-roll ring of preallocated blocks, reused forever — the previous
        // Queue<float[]> cloned a fresh 2 KB array every 32 ms while listening.
        private float[][] _preRollRing;
        private int _preRollHead;   // next slot to write
        private int _preRollCount;  // filled slots
        private int _preRollBlockCount;
        private int _voicedStreak;

        private readonly List<float> _capture = new List<float>();
        private float _silenceRun;

        // Per-capture diagnostics for the verbose end-of-capture log.
        private float _rmsMin, _rmsMax;
        private int _voicedBlocks, _totalBlocks;

        // Listening heartbeat (verbose only): proves per interval whether the
        // mic delivers samples, how loud they are, and whether suppression is
        // eating them — distinguishes a dead device from a threshold problem.
        private float _nextDiagAt;
        private int _diagBlocks;
        private float _diagMaxRms;
        private int _diagMaxStreak;

        // Barge-in: raised start bar + suppression bypass, see BeginBargeInDetection.
        private bool _bargeIn;
        private float _bargeInThreshold;
        private int _bargeInConfirmBlocks;

        private void OnEnable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
                Permission.RequestUserPermission(Permission.Microphone);
#endif
            _preRollBlockCount = Mathf.Max(1, Mathf.CeilToInt(preRoll / BlockSeconds));
            if (_preRollRing == null || _preRollRing.Length != _preRollBlockCount)
            {
                _preRollRing = new float[_preRollBlockCount][];
                for (int i = 0; i < _preRollBlockCount; i++)
                    _preRollRing[i] = new float[BlockSize];
            }
            if (listenOnEnable) StartListening();
        }

        private void OnDisable()
        {
            StopListening();
        }

        public void StartListening()
        {
            if (IsListening) return;

            if (!isActiveAndEnabled)
                Debug.LogWarning("MicrophoneRecorder: StartListening called while the component is disabled — audio is only processed in Update, so nothing will come through until it is enabled.", this);

            if (Microphone.devices.Length == 0)
            {
                Debug.LogWarning("MicrophoneRecorder: no microphone device available (missing permission?).", this);
                return;
            }

            // Many USB mics run at 44.1/48 kHz only and answer a 16 kHz request
            // with a clip of digital silence instead of an error (the Quest's
            // own mic does support 16 kHz, so this bites in the editor) — open
            // at a rate the device reports and resample in Update().
            Microphone.GetDeviceCaps(device, out int minFreq, out int maxFreq);
            _deviceRate = _forcedRate > 0 ? _forcedRate
                : minFreq == 0 && maxFreq == 0 ? SampleRate
                : Mathf.Clamp(SampleRate, minFreq, maxFreq);
            if (_deviceRate % BlocksPerSecond != 0)
            {
                // Chunking assumes whole 20 ms reads; no real device lands here.
                Debug.LogWarning($"MicrophoneRecorder: device rate {_deviceRate} Hz is not a multiple of {BlocksPerSecond} — falling back to {SampleRate} Hz.", this);
                _deviceRate = SampleRate;
            }

            _clip = Microphone.Start(device, true, ClipSeconds, _deviceRate);
            if (_clip == null)
            {
                Debug.LogWarning("MicrophoneRecorder: Microphone.Start failed.", this);
                return;
            }

            _channels = Mathf.Max(1, _clip.channels);
            _rawFrames = _deviceRate / BlocksPerSecond;
            if (_rawBuffer == null || _rawBuffer.Length != _rawFrames * _channels)
                _rawBuffer = new float[_rawFrames * _channels];

            if (verboseLogging)
                Debug.Log($"MicrophoneRecorder: listening on '{(string.IsNullOrEmpty(device) ? Microphone.devices[0] + "' (default)" : device + "'")} at {_deviceRate} Hz x{_channels}{(_deviceRate == SampleRate && _channels == 1 ? "" : $", converted to {SampleRate} Hz mono")} (device caps {minFreq}-{maxFreq}) — available: '{string.Join("', '", Microphone.devices)}'.", this);

            _readPos = 0;
            ResetVad();
            IsListening = true;
        }

        public void StopListening()
        {
            if (!IsListening) return;
            Microphone.End(device);
            _clip = null;
            IsListening = false;
            ResetVad();
            _bargeIn = false;
        }

        /// <summary>
        /// Listen for someone talking OVER a sound this recorder would normally
        /// suppress. The intro timeline plays its voice clips through the very
        /// AudioSource that sits in <see cref="suppressWhilePlaying"/>, so
        /// without this nothing is heard for as long as a character speaks —
        /// exactly when the teacher would cut in.
        ///
        /// While armed, suppression is ignored and starting a capture needs
        /// <paramref name="threshold"/> / <paramref name="confirmBlocks"/>
        /// instead of speechThreshold / speechConfirmBlocks. The bar is raised
        /// because the mic also hears the character's own voice coming back
        /// through the headset speakers: only someone talking INTO the mic
        /// should get through. Keeping a capture going still uses the normal
        /// hysteresis, so the teacher is never cut off mid-sentence.
        ///
        /// Arming drops any capture in progress (its pre-roll was gathered
        /// under suppression and is not ours). Disarm with
        /// <see cref="EndBargeInDetection"/> once the suppressed sound has
        /// actually stopped — disarming while it still plays would suppress,
        /// and so discard, the utterance that triggered the barge-in.
        /// </summary>
        public void BeginBargeInDetection(float threshold, int confirmBlocks)
        {
            ResetVad();
            _bargeIn = true;
            _bargeInThreshold = Mathf.Max(0f, threshold);
            _bargeInConfirmBlocks = Mathf.Max(1, confirmBlocks);
            if (verboseLogging)
                Debug.Log($"MicrophoneRecorder: barge-in armed — start threshold {_bargeInThreshold:F3} (normal {speechThreshold:F3}), {_bargeInConfirmBlocks} confirm blocks, suppression ignored.", this);
        }

        /// <summary>Back to the normal thresholds and to honouring suppressWhilePlaying.</summary>
        public void EndBargeInDetection()
        {
            if (!_bargeIn) return;
            _bargeIn = false;
            if (verboseLogging)
                Debug.Log("MicrophoneRecorder: barge-in disarmed.", this);
        }

        private void ResetVad()
        {
            if (IsCapturing) OnCaptureEnded?.Invoke(false); // aborted (suppression / stop)
            IsCapturing = false;
            _capture.Clear();
            _preRollHead = 0;
            _preRollCount = 0;
            _voicedStreak = 0;
            _silenceRun = 0f;
            _zeroBlocks = 0;
            HoldUtteranceOpen = false;
        }

        private bool Suppressed
        {
            get
            {
                // Barge-in: hearing someone talk over the suppressed sound is
                // the entire point, so the guard is off while it is armed.
                if (_bargeIn || suppressWhilePlaying == null) return false;
                foreach (AudioSource source in suppressWhilePlaying)
                    if (source != null && source.isPlaying)
                        return true;
                return false;
            }
        }

        private void Update()
        {
            if (!IsListening || _clip == null) return;

            bool suppressed = Suppressed;
            if (suppressed && (IsCapturing || _voicedStreak > 0 || _preRollCount > 0))
                ResetVad();

            int writePos = Microphone.GetPosition(device);
            int clipFrames = _clip.samples;

            // Consume whole 20 ms chunks behind the write head. The clip is a
            // whole number of seconds and the chunk divides a second exactly,
            // so a read never straddles the ring's wrap.
            while (true)
            {
                int available = writePos - _readPos;
                if (available < 0) available += clipFrames;
                if (available < _rawFrames) break;

                _clip.GetData(_rawBuffer, _readPos);
                _readPos = (_readPos + _rawFrames) % clipFrames;
                _diagBlocks++;

                ResampleToBlock();

                if (!suppressed)
                    ProcessBlock(_block);

                if (_needsRateFallback) break;   // the clip is about to be replaced
            }

            if (_needsRateFallback)
            {
                _needsRateFallback = false;
                _rateFallbackTried = true;
                _forcedRate = 48000;
                Debug.LogWarning($"MicrophoneRecorder: {_deviceRate} Hz delivered 2 s of digital silence — reopening '{(string.IsNullOrEmpty(device) ? "default device" : device)}' at {_forcedRate} Hz. If it stays silent, the mic is muted at OS or hardware level.", this);
                StopListening();
                StartListening();
                return;
            }

            if (verboseLogging && Time.unscaledTime >= _nextDiagAt)
            {
                // 0 blocks = the device delivers nothing (editor mic lost,
                // Windows privacy, wrong default device). Blocks with low max
                // RMS = gain/threshold. suppressed=True = an AudioSource in
                // suppressWhilePlaying never stops.
                // The streak is what actually decides: every block of the run
                // has to clear the bar, so "max RMS is above the threshold" and
                // "a capture started" are very different things.
                Debug.Log($"MicrophoneRecorder: diag — {_diagBlocks} blocks in last 2s (max RMS {_diagMaxRms:F4}, start threshold {(_bargeIn ? _bargeInThreshold : speechThreshold):F3}{(_bargeIn ? " barge-in" : "")}, longest voiced streak {_diagMaxStreak}/{(_bargeIn ? _bargeInConfirmBlocks : speechConfirmBlocks)}), micPos {writePos}, suppressed={suppressed}, capturing={IsCapturing}.", this);
                _nextDiagAt = Time.unscaledTime + 2f;
                _diagBlocks = 0;
                _diagMaxRms = 0f;
                _diagMaxStreak = 0;
            }
        }

        /// <summary>
        /// Turns the raw device chunk into the fixed 16 kHz mono block: channels
        /// averaged, samples linearly interpolated. Both sides are 20 ms, so the
        /// ratio is exact and no phase is carried between chunks.
        /// </summary>
        private void ResampleToBlock()
        {
            if (_deviceRate == SampleRate && _channels == 1)
            {
                Array.Copy(_rawBuffer, _block, BlockSize);
                return;
            }

            float ratio = _rawFrames / (float)BlockSize;
            for (int j = 0; j < BlockSize; j++)
            {
                float pos = j * ratio;
                int i0 = (int)pos;
                // Holding the last frame at the chunk edge costs at most one
                // sample per 20 ms, and only when downsampling is not needed.
                int i1 = Mathf.Min(i0 + 1, _rawFrames - 1);
                _block[j] = Mathf.Lerp(FrameToMono(i0), FrameToMono(i1), pos - i0);
            }
        }

        private float FrameToMono(int frame)
        {
            if (_channels == 1) return _rawBuffer[frame];

            int i = frame * _channels;
            float sum = 0f;
            for (int c = 0; c < _channels; c++)
                sum += _rawBuffer[i + c];
            return sum / _channels;
        }

        private void ProcessBlock(float[] block)
        {
            float sum = 0f;
            for (int i = 0; i < block.Length; i++)
                sum += block[i] * block[i];
            float rms = Mathf.Sqrt(sum / block.Length);
            _diagMaxRms = Mathf.Max(_diagMaxRms, rms);

            // Exactly zero across a whole block is digital silence, not a quiet
            // room — the device is muted or is refusing the rate we opened it
            // at. Reopen once at 48 kHz before concluding the mic is dead.
            if (rms > 0f) _zeroBlocks = 0;
            else if (++_zeroBlocks >= BlocksPerSecond * 2 && !_rateFallbackTried && _deviceRate != 48000)
                _needsRateFallback = true;   // acted on in Update, once this read loop is done
            // Hysteresis: a capture needs the full threshold to START, but only
            // the lower bar to KEEP GOING — otherwise quiet stretches of live
            // speech read as silence and end the utterance mid-sentence.
            // Barge-in raises the bar to START a capture (the mic also hears
            // the character's own voice) but never the bar to KEEP one going.
            float startThreshold = _bargeIn ? _bargeInThreshold : speechThreshold;
            bool voiced = rms >= (IsCapturing ? Mathf.Min(keepSpeechThreshold, startThreshold) : startThreshold);

            if (!IsCapturing)
            {
                // Keep a rolling pre-roll so the utterance includes its onset —
                // copied into the preallocated ring, oldest slot overwritten.
                Array.Copy(block, _preRollRing[_preRollHead], BlockSize);
                _preRollHead = (_preRollHead + 1) % _preRollRing.Length;
                if (_preRollCount < _preRollRing.Length) _preRollCount++;

                _voicedStreak = voiced ? _voicedStreak + 1 : 0;
                _diagMaxStreak = Mathf.Max(_diagMaxStreak, _voicedStreak);
                if (_voicedStreak >= (_bargeIn ? _bargeInConfirmBlocks : speechConfirmBlocks))
                {
                    IsCapturing = true;
                    _silenceRun = 0f;
                    _rmsMin = float.MaxValue;
                    _rmsMax = 0f;
                    _voicedBlocks = 0;
                    _totalBlocks = 0;
                    _capture.Clear();
                    int ringLength = _preRollRing.Length;
                    int oldest = (_preRollHead - _preRollCount + ringLength) % ringLength;
                    for (int i = 0; i < _preRollCount; i++)
                        _capture.AddRange(_preRollRing[(oldest + i) % ringLength]);
                    _preRollHead = 0;
                    _preRollCount = 0;
                    _voicedStreak = 0;
                    OnSpeechStarted?.Invoke();
                    OnCaptureBlock?.Invoke(_capture.ToArray()); // pre-roll as the first chunk
                }
                return;
            }

            _capture.AddRange(block);
            OnCaptureBlock?.Invoke(block); // reused scratch buffer — see event doc
            _silenceRun = voiced ? 0f : _silenceRun + BlockSeconds;

            _rmsMin = Mathf.Min(_rmsMin, rms);
            _rmsMax = Mathf.Max(_rmsMax, rms);
            _totalBlocks++;
            if (voiced) _voicedBlocks++;

            float window = HoldUtteranceOpen && silenceToEndExtended > silenceToEnd ? silenceToEndExtended : silenceToEnd;
            bool ended = _silenceRun >= window;
            bool overrun = _capture.Count >= maxUtterance * SampleRate;
            if (!ended && !overrun) return;

            float[] utterance = _capture.ToArray();
            IsCapturing = false; // before ResetVad so it doesn't double-fire OnCaptureEnded

            if (verboseLogging)
                Debug.Log($"MicrophoneRecorder: capture ended by {(ended ? $"silence ({window:F1}s window{(HoldUtteranceOpen ? ", held open" : "")})" : $"OVERRUN (maxUtterance {maxUtterance}s hit mid-speech)")} after {utterance.Length / (float)SampleRate:F1}s — voiced {_voicedBlocks}/{_totalBlocks} blocks, RMS {_rmsMin:F4}..{_rmsMax:F4}, thresholds start {speechThreshold:F3} / keep {Mathf.Min(keepSpeechThreshold, speechThreshold):F3}.", this);

            float speechSeconds = utterance.Length / (float)SampleRate - preRoll - (ended ? window : 0f);
            bool kept = speechSeconds >= minUtterance;
            // Snapshot before ResetVad() below clears HoldUtteranceOpen.
            LastCaptureSpeechSeconds = speechSeconds;
            LastCaptureSilenceWindow = ended ? window : 0f;
            LastCaptureHeldOpen = HoldUtteranceOpen;
            LastCaptureEndedBySilence = ended;
            OnCaptureEnded?.Invoke(kept);
            ResetVad();

            if (kept)
                OnUtteranceCaptured?.Invoke(utterance);
        }
    }
}
