using System;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Real-time lip-sync analysis for an AudioSource. Every frame it reads
    /// the audio that is currently playing (prerecorded or generated — it taps
    /// the live output, not the clip asset), runs an FFT via
    /// AudioSource.GetSpectrumData and estimates which mouth pose (viseme)
    /// matches the sound:
    ///  - vowels Ah/Eh/EE/Ih/Er/Oh/UW from the two dominant formant regions (F1/F2),
    ///  - sibilants split by noise centroid: SZ (s, z) vs ChJ (sh, ch, j),
    ///  - FV (f, v — and th, which sounds alike) from weak broadband frication,
    ///  - MBP (closed lips: m, and voiced b/p closures) from low-frequency
    ///    murmur dominance,
    ///  - Silence (mouth closed) from RMS gating.
    ///
    /// Usage: add this next to the NPC's AudioSource, then drive the mouth by
    /// either reading <see cref="GetWeight"/> per viseme each frame (smooth
    /// blendshape animation), or <see cref="CurrentViseme"/> /
    /// <see cref="OnVisemeChanged"/> (discrete pose/sprite swaps), optionally
    /// scaled by <see cref="MouthOpen"/> for jaw openness.
    ///
    /// Suggested mapping onto the CC4 "1-to-1" V_* viseme blendshapes (what
    /// the Character Creator body meshes in this project carry) — weights are
    /// starting points, expressed in Unity's 0..100 blendshape scale:
    ///   Ah  → V_Open 100
    ///   Eh  → V_Open 55, V_Wide 45
    ///   EE  → V_Wide 100
    ///   Ih  → V_Wide 55, V_Open 25
    ///   Er  → V_Tight 60, V_Open 30
    ///   Oh  → V_Tight_O 100
    ///   UW  → V_Tight_O 70, V_Tight 30
    ///   SZ  → V_Wide 55, V_Lip_Open 35 (teeth nearly together)
    ///   ChJ → V_Affricate 90
    ///   FV  → V_Dental_Lip 90
    ///   MBP → V_Explosive 100
    ///   Silence → everything 0
    /// Tongue-only visemes (V_Tongue_*) are not recoverable from audio and are
    /// intentionally out of scope.
    ///
    /// Classification uses energy ratios and centroids, so it is independent
    /// of playback volume; only the silence gate compares against absolute
    /// level — tune silenceThreshold if the AudioSource volume is far from 1.
    /// </summary>
    public class LipSyncAnalyzer : MonoBehaviour
    {
        public enum Viseme
        {
            Silence = 0, // mouth closed / at rest

            // Vowels — classified by formant match, they blend into each other.
            Ah = 1,      // open jaw        (father)
            Eh = 2,      // mid-open        (bed, bad)
            EE = 3,      // wide, spread    (see)
            Ih = 4,      // relaxed wide    (bit)
            Er = 5,      // central, tense  (bird)
            Oh = 6,      // round           (thought)
            UW = 7,      // puckered        (boot)

            // Consonants — winner-take-all within their detector.
            SZ = 8,      // sharp sibilant  (s, z — teeth together)
            ChJ = 9,     // hushed sibilant (sh, ch, j)
            FV = 10,     // lip under teeth (f, v; th sounds alike)
            MBP = 11,    // lips closed     (m, b, p)
        }

        public const int VisemeCount = 12;

        private const int FirstVowel = (int)Viseme.Ah;
        private const int LastVowel = (int)Viseme.UW;

        public enum FftSize
        {
            _512 = 512,
            _1024 = 1024,
            _2048 = 2048,
        }

        [Header("Input")]
        [Tooltip("Source to analyze. Grabbed from this GameObject when left empty.")]
        [SerializeField] private AudioSource audioSource;

        [Header("Analysis")]
        [Tooltip("FFT resolution — larger is more precise in frequency but reacts slower. Fixed after Awake.")]
        [SerializeField] private FftSize fftSize = FftSize._1024;

        [Tooltip("RMS below this counts as silence (mouth closed).")]
        [SerializeField] private float silenceThreshold = 0.01f;

        [Tooltip("RMS at which the mouth counts as fully open (MouthOpen = 1). Ignored while Auto Level is on.")]
        [SerializeField] private float loudnessCeiling = 0.2f;

        [Tooltip("Track the voice's recent peak level and normalize MouthOpen against it. Only stressed syllables reach full openness, independent of clip volume.")]
        [SerializeField] private bool autoLevel = true;

        [Tooltip("How fast the tracked peak forgets, per second. Lower = steadier, higher = adapts quicker to volume changes.")]
        [SerializeField] private float autoLevelDecay = 0.35f;

        [Tooltip("Share of spectral energy above 3 kHz that flags a strong sibilant (s/z/sh/ch).")]
        [Range(0f, 1f)]
        [SerializeField] private float sibilanceThreshold = 0.35f;

        [Tooltip("Share of spectral energy above 3 kHz that flags weak frication (f/v/th). Must be below the sibilance threshold.")]
        [Range(0f, 1f)]
        [SerializeField] private float fricationThreshold = 0.18f;

        [Tooltip("Share of spectral energy below 500 Hz that flags a nasal/bilabial murmur (m/b/p — lips closed).")]
        [Range(0f, 1f)]
        [SerializeField] private float murmurThreshold = 0.72f;

        [Header("Smoothing")]
        [Tooltip("Response rate when a viseme weight rises. Roughly 1/value seconds to mostly reach the target — 17 ≈ 60 ms.")]
        [SerializeField] private float attackRate = 17f;

        [Tooltip("Response rate when a viseme weight falls. Keep below the attack rate so the mouth eases out of shapes — 10 ≈ 100 ms.")]
        [SerializeField] private float releaseRate = 10f;

        [Tooltip("Response rate for the loudness envelope rising. Much faster than the visemes so MouthOpen tracks each syllable — 40 ≈ 25 ms.")]
        [SerializeField] private float loudnessAttackRate = 40f;

        [Tooltip("Response rate for the loudness envelope falling. Fast enough to dip in the gaps between syllables, closing the mouth — 14 ≈ 70 ms.")]
        [SerializeField] private float loudnessReleaseRate = 14f;

        [Tooltip("Minimum time (s) the dominant viseme is held before it may change — kills flicker.")]
        [SerializeField] private float minHoldTime = 0.06f;

        /// <summary>Dominant mouth pose right now (smoothed and flicker-filtered).</summary>
        public Viseme CurrentViseme { get; private set; } = Viseme.Silence;

        /// <summary>Fired whenever <see cref="CurrentViseme"/> changes.</summary>
        public event Action<Viseme> OnVisemeChanged;

        /// <summary>Overall jaw openness 0..1, from smoothed loudness.</summary>
        public float MouthOpen { get; private set; }

        /// <summary>Smoothed RMS of the playing audio.</summary>
        public float Loudness { get; private set; }

        [Header("Debug (runtime)")]
        [Tooltip("Live loudness while a clip plays — read-only, for tuning the thresholds.")]
        [SerializeField] private float debugLoudness;

        [Tooltip("Live MouthOpen while a clip plays — if this sits near 1 constantly, openness has no dynamic range.")]
        [SerializeField] private float debugMouthOpen;

        private float _peakLoudness;

        // Vowel formant prototypes (Hz), gender-averaged; indices match Viseme.
        //                                                    Ah     Eh     EE     Ih     Er     Oh     UW
        private static readonly float[] PrototypeF1 = { 0f,   780f,  580f,  300f,  430f,  500f,  500f,  320f,  0f, 0f, 0f, 0f };
        private static readonly float[] PrototypeF2 = { 0f,   1300f, 1850f, 2300f, 2050f, 1400f, 1000f, 900f,  0f, 0f, 0f, 0f };

        // Sibilant noise-centroid prototypes (Hz): s/z hiss sits high,
        // sh/ch/j lower and broader.
        private const float SibilantCentroidSZ = 6500f;
        private const float SibilantCentroidChJ = 4200f;

        private readonly float[] _weights = new float[VisemeCount];
        private readonly float[] _targetWeights = new float[VisemeCount];
        private float[] _spectrum;
        private float[] _wave;
        private float _holdTimer;

        /// <summary>Smoothed 0..1 blend weight of one viseme — weights of the active group sum to ~1 while speaking.</summary>
        public float GetWeight(Viseme viseme)
        {
            return _weights[(int)viseme];
        }

        private void Awake()
        {
            if (audioSource == null)
                audioSource = GetComponent<AudioSource>();

            if (audioSource == null)
                Debug.LogError("LipSyncAnalyzer: no AudioSource assigned or found on the GameObject.");

            _spectrum = new float[(int)fftSize];
            _wave = new float[256];
            _weights[(int)Viseme.Silence] = 1f;
        }

        private void Update()
        {
            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.GetOutputData(_wave, 0);
                float rms = ComputeRms(_wave);
                Loudness += (rms - Loudness) * SmoothingStep(rms > Loudness ? loudnessAttackRate : loudnessReleaseRate);

                // Normalize openness against the voice's own recent peaks so a
                // loud clip doesn't peg MouthOpen at 1 for the whole sentence.
                _peakLoudness = Mathf.Max(rms, _peakLoudness * (1f - autoLevelDecay * Time.deltaTime));
                float ceiling = autoLevel
                    ? Mathf.Max(_peakLoudness * 0.85f, silenceThreshold * 3f)
                    : loudnessCeiling;
                MouthOpen = Mathf.InverseLerp(silenceThreshold, ceiling, Loudness);

                debugLoudness = Loudness;
                debugMouthOpen = MouthOpen;

                audioSource.GetSpectrumData(_spectrum, 0, FFTWindow.BlackmanHarris);
                Classify(rms);
            }
            else
            {
                Loudness = 0f;
                MouthOpen = 0f;
                Array.Clear(_targetWeights, 0, VisemeCount);
                _targetWeights[(int)Viseme.Silence] = 1f;
            }

            SmoothAndPickDominant();
        }

        private static float ComputeRms(float[] samples)
        {
            float sum = 0f;
            for (int i = 0; i < samples.Length; i++)
                sum += samples[i] * samples[i];
            return Mathf.Sqrt(sum / samples.Length);
        }

        private void Classify(float rms)
        {
            Array.Clear(_targetWeights, 0, VisemeCount);

            if (rms < silenceThreshold)
            {
                _targetWeights[(int)Viseme.Silence] = 1f;
                return;
            }

            float binWidth = AudioSettings.outputSampleRate * 0.5f / _spectrum.Length;

            float total = 0f;
            float low = 0f;   // murmur band — voiced energy with the mouth closed
            float high = 0f;  // frication band
            for (int i = 1; i < _spectrum.Length; i++)
            {
                float freq = i * binWidth;
                total += _spectrum[i];
                if (freq <= 500f)
                    low += _spectrum[i];
                else if (freq >= 3000f && freq <= 10000f)
                    high += _spectrum[i];
            }

            if (total <= 0f)
            {
                _targetWeights[(int)Viseme.Silence] = 1f;
                return;
            }

            float fricationShare = high / total;

            // Strong sibilance: split s/z from sh/ch/j by where the hiss sits.
            if (fricationShare >= sibilanceThreshold)
            {
                float centroid = BandCentroid(3000f, 10000f, binWidth);
                float sz = Gaussian(centroid, SibilantCentroidSZ, 1600f);
                float chj = Gaussian(centroid, SibilantCentroidChJ, 1200f);
                float sum = sz + chj;
                if (sum > 0f)
                {
                    _targetWeights[(int)Viseme.SZ] = sz / sum;
                    _targetWeights[(int)Viseme.ChJ] = chj / sum;
                }
                else
                {
                    _targetWeights[(int)Viseme.SZ] = 1f;
                }
                return;
            }

            // Nasal/bilabial murmur: nearly all energy below 500 Hz and no
            // frication means the lips are closed (m, or a voiced b/p closure).
            if (low / total >= murmurThreshold && fricationShare < 0.1f)
            {
                _targetWeights[(int)Viseme.MBP] = 1f;
                return;
            }

            // Weak but present frication with no sibilant peak: f/v (or th,
            // which is acoustically close enough to share the mouth shape).
            if (fricationShare >= fricationThreshold)
            {
                _targetWeights[(int)Viseme.FV] = 1f;
                return;
            }

            // Estimate the two formants as energy centroids of their typical
            // ranges (the bands overlap on purpose: Oh/UW put F2 below 1 kHz).
            float f1 = BandCentroid(150f, 1000f, binWidth);
            float f2 = BandCentroid(800f, 3000f, binWidth);

            // Soft inverse-distance match against the vowel prototypes so
            // neighbouring mouth shapes blend instead of snapping.
            float vowelSum = 0f;
            for (int v = FirstVowel; v <= LastVowel; v++)
            {
                float d1 = (f1 - PrototypeF1[v]) / 700f;
                float d2 = (f2 - PrototypeF2[v]) / 1500f;
                float w = Mathf.Exp(-(d1 * d1 + d2 * d2) * 4f);
                _targetWeights[v] = w;
                vowelSum += w;
            }

            for (int v = FirstVowel; v <= LastVowel; v++)
                _targetWeights[v] /= vowelSum;
        }

        private static float Gaussian(float x, float center, float width)
        {
            float d = (x - center) / width;
            return Mathf.Exp(-d * d);
        }

        private float BandCentroid(float fromHz, float toHz, float binWidth)
        {
            int from = Mathf.Max(1, Mathf.FloorToInt(fromHz / binWidth));
            int to = Mathf.Min(_spectrum.Length - 1, Mathf.CeilToInt(toHz / binWidth));

            float energy = 0f;
            float weighted = 0f;
            for (int i = from; i <= to; i++)
            {
                energy += _spectrum[i];
                weighted += _spectrum[i] * i * binWidth;
            }

            return energy > 0f ? weighted / energy : 0f;
        }

        /// <summary>Frame-rate independent exponential smoothing factor for the given response rate.</summary>
        private static float SmoothingStep(float rate)
        {
            return 1f - Mathf.Exp(-rate * Time.deltaTime);
        }

        private void SmoothAndPickDominant()
        {
            int best = 0;
            float bestWeight = 0f;

            for (int v = 0; v < VisemeCount; v++)
            {
                float target = _targetWeights[v];
                // Exponential ease-out: quick to react, soft as it settles —
                // reads far more organic than a linear ramp.
                _weights[v] += (target - _weights[v]) * SmoothingStep(target > _weights[v] ? attackRate : releaseRate);

                if (_weights[v] > bestWeight)
                {
                    bestWeight = _weights[v];
                    best = v;
                }
            }

            _holdTimer += Time.deltaTime;
            if (best != (int)CurrentViseme && _holdTimer >= minHoldTime)
            {
                CurrentViseme = (Viseme)best;
                _holdTimer = 0f;
                OnVisemeChanged?.Invoke(CurrentViseme);
            }
        }
    }
}
