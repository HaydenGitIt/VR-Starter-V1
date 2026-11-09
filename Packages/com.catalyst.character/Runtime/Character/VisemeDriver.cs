using System;
using System.Collections.Generic;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// Bridges LipSyncAnalyzer output onto a Character Creator rig. Every
    /// frame the analyzer's per-viseme weights are mixed through an editable
    /// viseme → blendshape table and written to the body mesh only —
    /// the Reallusion BoneDriver on CC_Base_Body propagates the weights to
    /// the other renderers (teeth, eyes, brows) and articulates the jaw and
    /// teeth bones, and WrinkleManager reads the same weights for skin
    /// wrinkles. Nothing else needs to be driven.
    ///
    /// Wiring: add next to the CharacterReferenceHolder (or anywhere under
    /// the character root); the analyzer is found through the holder's
    /// voiceSource when not assigned. The default mapping table targets the
    /// CC4 "1-to-1" V_* viseme profile.
    ///
    /// Runs in LateUpdate with a negative execution order: after the Animator
    /// has evaluated but before BoneDriver reads the body weights. Keep the
    /// viseme channels out of the AnimatorController's clips or they will
    /// overwrite speech every frame.
    /// </summary>
    [DefaultExecutionOrder(-100)] // before BoneDriver's LateUpdate (order 0)
    public class VisemeDriver : MonoBehaviour
    {
        [Serializable]
        public class ShapeInfluence
        {
            [Tooltip("Blendshape on the body mesh, e.g. V_Open.")]
            public string blendShape;

            [Tooltip("How strongly this viseme pulls the shape, 0..1.")]
            [Range(0f, 1f)] public float weight = 1f;
        }

        [Serializable]
        public class VisemeMapping
        {
            public LipSyncAnalyzer.Viseme viseme;
            public ShapeInfluence[] shapes;
        }

        [Header("References")]
        [Tooltip("Character to animate. Found on this GameObject or its parents when left empty.")]
        public CharacterReferenceHolder character;

        [Tooltip("Analyzer to read visemes from. Found via the character's voiceSource (or this hierarchy) when left empty.")]
        public LipSyncAnalyzer analyzer;

        [Tooltip("Overrides the character's body mesh when assigned.")]
        public SkinnedMeshRenderer bodyMeshOverride;

        [Header("Tuning")]
        [Tooltip("Master scale for all speech shapes. For more punch beyond ~1.2, prefer BoneDriver's amplify/visemePower.")]
        [Range(0f, 2f)] public float intensity = 1f;

        [Tooltip("How much loudness scales the mouth: 0 = full articulation regardless of volume, 1 = fully proportional to MouthOpen. High values are what let the mouth close between syllables.")]
        [Range(0f, 1f)] public float loudnessInfluence = 0.85f;

        [Tooltip("Response curve for loudness → openness: 1 = linear, higher keeps the mouth mostly closed except on stressed syllables.")]
        [Range(0.5f, 3f)] public float opennessExponent = 1.9f;

        [Tooltip("Ceiling for any single speech blendshape (0..100). Normal speech rarely needs the full 100.")]
        [Range(0f, 100f)] public float maxShapeWeight = 80f;

        [Tooltip("Extra Jaw_Open driven by loudness (MouthOpen), on top of what the viseme shapes already do. 0 disables.")]
        [Range(0f, 1f)] public float jawOpenBoost = 0.1f;

        [Tooltip("Per-viseme blendshape influences. Defaults target the CC4 V_* viseme profile.")]
        public VisemeMapping[] mappings = DefaultCC4Mappings();

        [Header("Animation mouth gate")]
        [Tooltip("While the character speaks, fade out the mouth / jaw / tongue blendshapes the body animation writes (idle clips carry facial curves), so only lip sync and the mood expression shape the mouth.")]
        public bool suppressAnimationMouthWhileSpeaking = true;

        [Tooltip("Seconds for the animation's mouth to fade out when speech starts.")]
        [Min(0f)] public float gateFadeIn = 0.12f;

        [Tooltip("Seconds for the animation's mouth to fade back in after speech ends.")]
        [Min(0f)] public float gateFadeOut = 0.25f;

        [Tooltip("Body-mesh blendshapes starting with any of these are faded out while speaking. The viseme shapes this driver writes itself are never touched.")]
        public string[] suppressPrefixes = { "Mouth_", "Jaw_", "Tongue_", "Lip_", "Cheek_Puff", "Cheek_Suck" };

        [Tooltip("Exact blendshape names to fade out as well, beyond the prefixes.")]
        public string[] extraSuppressed = Array.Empty<string>();

        [Tooltip("Exact blendshape names to leave alone even if a prefix matches.")]
        public string[] neverSuppress = Array.Empty<string>();

        [Tooltip("Speech playback that defines 'speaking'. Found on the character when empty; without one, the voice AudioSource playing is used.")]
        public NpcSpeechPlayer speechPlayer;

        [Tooltip("Facial life layer — receives the gate so its idle mouth life stands down too. Found on the character when empty.")]
        public ProceduralIdleMotion idleMotion;

        /// <summary>0 = animation owns its mouth shapes, 1 = fully suppressed (speaking).</summary>
        public float SpeechGate => _gate;

        private int[] _suppressedIndices = Array.Empty<int>();
        private float _gate;
        private AudioSource _voice;

        private struct Influence
        {
            public LipSyncAnalyzer.Viseme viseme;
            public int slot;      // index into _shapeIndices/_accum
            public float weight;
        }

        private SkinnedMeshRenderer _bodyMesh;
        private Influence[] _influences;
        private int[] _shapeIndices; // distinct blendshape indices on the body mesh
        private float[] _accum;      // per-shape mixed weight, 0..1 scale
        private int _jawIndex = -1;
        private bool _wroteZeros;    // silence already flushed as zeros; skip writes

        private void Awake()
        {
            if (character == null) character = GetComponentInParent<CharacterReferenceHolder>();
            if (analyzer == null && character != null && character.voiceSource != null)
                analyzer = character.voiceSource.GetComponent<LipSyncAnalyzer>();
            if (analyzer == null) analyzer = GetComponentInChildren<LipSyncAnalyzer>();

            _bodyMesh = bodyMeshOverride;
            if (_bodyMesh == null && character != null)
            {
                if (character.bodyMesh == null) character.ResolveReferences();
                _bodyMesh = character.bodyMesh;
            }

            if (_bodyMesh == null || analyzer == null)
            {
                Debug.LogError($"VisemeDriver: missing {(_bodyMesh == null ? "body mesh" : "LipSyncAnalyzer")} — disabling.", this);
                enabled = false;
                return;
            }

            BuildInfluenceTable();
            ResolveGateReferences();
            BuildSuppressionTable();
        }

        private void ResolveGateReferences()
        {
            GameObject holder = character != null ? character.gameObject : gameObject;
            GameObject root = character != null ? character.Root : gameObject;
            if (speechPlayer == null) speechPlayer = holder.GetComponentInChildren<NpcSpeechPlayer>(true);
            if (speechPlayer == null) speechPlayer = root.GetComponentInChildren<NpcSpeechPlayer>(true);
            if (idleMotion == null) idleMotion = root.GetComponentInChildren<ProceduralIdleMotion>(true);
            if (idleMotion == null) idleMotion = holder.GetComponentInChildren<ProceduralIdleMotion>(true);
            _voice = character != null ? character.voiceSource : null;
        }

        private void BuildSuppressionTable()
        {
            Mesh mesh = _bodyMesh.sharedMesh;
            var own = new HashSet<int>(_shapeIndices);
            var never = new HashSet<string>(neverSuppress ?? Array.Empty<string>());
            var extra = new HashSet<string>(extraSuppressed ?? Array.Empty<string>());
            var result = new List<int>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                if (own.Contains(i)) continue;
                string shape = mesh.GetBlendShapeName(i);
                if (never.Contains(shape)) continue;
                bool match = extra.Contains(shape);
                if (!match && suppressPrefixes != null)
                {
                    foreach (string prefix in suppressPrefixes)
                    {
                        if (!string.IsNullOrEmpty(prefix) && shape.StartsWith(prefix, StringComparison.Ordinal)) { match = true; break; }
                    }
                }
                if (match) result.Add(i);
            }
            _suppressedIndices = result.ToArray();
        }

        private bool IsSpeaking()
        {
            if (speechPlayer != null) return speechPlayer.IsSpeaking;
            return _voice != null && _voice.isPlaying;
        }

        /// <summary>
        /// Advances the speech gate and fades the animation-authored mouth
        /// shapes by it. Runs after the pose was evaluated this frame (the
        /// clip re-writes these shapes every frame, so scaling the current
        /// value never compounds) and before BoneDriver turns them into the
        /// jaw bone.
        /// </summary>
        private void ApplyMouthGate()
        {
            bool speaking = suppressAnimationMouthWhileSpeaking && IsSpeaking();
            float fade = speaking ? gateFadeIn : gateFadeOut;
            float step = fade > 0f ? Time.deltaTime / fade : 1f;
            _gate = Mathf.MoveTowards(_gate, speaking ? 1f : 0f, step);

            if (idleMotion != null) idleMotion.SpeechMouthGate = _gate;
            if (_gate <= 0f) return;

            float keep = 1f - Mathf.SmoothStep(0f, 1f, _gate);
            foreach (int index in _suppressedIndices)
            {
                float w = _bodyMesh.GetBlendShapeWeight(index);
                if (w != 0f) _bodyMesh.SetBlendShapeWeight(index, w * keep);
            }
        }

        private void BuildInfluenceTable()
        {
            Mesh mesh = _bodyMesh.sharedMesh;
            var slotByShapeIndex = new Dictionary<int, int>();
            var shapeIndices = new List<int>();
            var influences = new List<Influence>();
            var missing = new List<string>();

            foreach (VisemeMapping mapping in mappings)
            {
                if (mapping.shapes == null) continue;
                foreach (ShapeInfluence shape in mapping.shapes)
                {
                    int shapeIndex = mesh.GetBlendShapeIndex(shape.blendShape);
                    if (shapeIndex < 0)
                    {
                        if (!missing.Contains(shape.blendShape)) missing.Add(shape.blendShape);
                        continue;
                    }

                    if (!slotByShapeIndex.TryGetValue(shapeIndex, out int slot))
                    {
                        slot = shapeIndices.Count;
                        slotByShapeIndex.Add(shapeIndex, slot);
                        shapeIndices.Add(shapeIndex);
                    }

                    influences.Add(new Influence { viseme = mapping.viseme, slot = slot, weight = shape.weight });
                }
            }

            _influences = influences.ToArray();
            _shapeIndices = shapeIndices.ToArray();
            _accum = new float[_shapeIndices.Length];
            _jawIndex = mesh.GetBlendShapeIndex("Jaw_Open");

            if (missing.Count > 0)
                Debug.LogWarning($"VisemeDriver: blendshapes not found on '{_bodyMesh.name}': {string.Join(", ", missing)} — those influences are skipped.", this);
        }

        private void LateUpdate()
        {
            if (_influences == null) return;

            // First, every frame — including the silent gaps between words,
            // which return early below.
            ApplyMouthGate();

            Array.Clear(_accum, 0, _accum.Length);
            foreach (Influence influence in _influences)
                _accum[influence.slot] += analyzer.GetWeight(influence.viseme) * influence.weight;

            // Quiet speech articulates less than loud speech, with a curve so
            // only stressed syllables reach full openness.
            float open = Mathf.Pow(Mathf.Clamp01(analyzer.MouthOpen), opennessExponent);
            float articulation = intensity * Mathf.Lerp(1f, open, loudnessInfluence);

            // Nothing audible: write zeros once, then stop touching the mesh so the
            // renderer isn't re-dirtied every frame of silence.
            const float silenceEpsilon = 1e-4f;
            bool silent = open <= silenceEpsilon;
            if (silent)
            {
                for (int i = 0; i < _accum.Length; i++)
                {
                    if (_accum[i] > silenceEpsilon) { silent = false; break; }
                }
            }
            if (silent)
            {
                if (_wroteZeros) return;
                _wroteZeros = true;
                foreach (int shapeIndex in _shapeIndices)
                    _bodyMesh.SetBlendShapeWeight(shapeIndex, 0f);
                if (_jawIndex >= 0)
                    _bodyMesh.SetBlendShapeWeight(_jawIndex, 0f);
                return;
            }
            _wroteZeros = false;

            for (int i = 0; i < _shapeIndices.Length; i++)
                _bodyMesh.SetBlendShapeWeight(_shapeIndices[i], Mathf.Min(_accum[i] * articulation * 100f, maxShapeWeight));

            if (_jawIndex >= 0 && jawOpenBoost > 0f)
            {
                // No jaw boost while the lips should be sealed (m/b/p).
                float jaw = open * jawOpenBoost * intensity * (1f - analyzer.GetWeight(LipSyncAnalyzer.Viseme.MBP));
                _bodyMesh.SetBlendShapeWeight(_jawIndex, Mathf.Clamp01(jaw) * 100f);
            }
        }

        private void OnDisable()
        {
            _gate = 0f;
            if (idleMotion != null) idleMotion.SpeechMouthGate = 0f;

            // Leave the mouth closed rather than frozen mid-viseme.
            if (_shapeIndices == null || _bodyMesh == null) return;
            foreach (int shapeIndex in _shapeIndices)
                _bodyMesh.SetBlendShapeWeight(shapeIndex, 0f);
            if (_jawIndex >= 0)
                _bodyMesh.SetBlendShapeWeight(_jawIndex, 0f);
        }

        [ContextMenu("Reset Mapping To CC4 Defaults")]
        private void ResetMappingToDefaults()
        {
            mappings = DefaultCC4Mappings();
        }

        private static VisemeMapping[] DefaultCC4Mappings()
        {
            return new[]
            {
                Map(LipSyncAnalyzer.Viseme.Ah, ("V_Open", 1f)),
                Map(LipSyncAnalyzer.Viseme.Eh, ("V_Open", 0.55f), ("V_Wide", 0.45f)),
                Map(LipSyncAnalyzer.Viseme.EE, ("V_Wide", 1f)),
                Map(LipSyncAnalyzer.Viseme.Ih, ("V_Wide", 0.55f), ("V_Open", 0.25f)),
                Map(LipSyncAnalyzer.Viseme.Er, ("V_Tight", 0.6f), ("V_Open", 0.3f)),
                Map(LipSyncAnalyzer.Viseme.Oh, ("V_Tight_O", 1f)),
                Map(LipSyncAnalyzer.Viseme.UW, ("V_Tight_O", 0.7f), ("V_Tight", 0.3f)),
                Map(LipSyncAnalyzer.Viseme.SZ, ("V_Wide", 0.55f), ("V_Lip_Open", 0.35f)),
                Map(LipSyncAnalyzer.Viseme.ChJ, ("V_Affricate", 0.9f)),
                Map(LipSyncAnalyzer.Viseme.FV, ("V_Dental_Lip", 0.9f)),
                Map(LipSyncAnalyzer.Viseme.MBP, ("V_Explosive", 1f)),
            };
        }

        private static VisemeMapping Map(LipSyncAnalyzer.Viseme viseme, params (string shape, float weight)[] shapes)
        {
            var mapping = new VisemeMapping { viseme = viseme, shapes = new ShapeInfluence[shapes.Length] };
            for (int i = 0; i < shapes.Length; i++)
                mapping.shapes[i] = new ShapeInfluence { blendShape = shapes[i].shape, weight = shapes[i].weight };
            return mapping;
        }
    }
}
