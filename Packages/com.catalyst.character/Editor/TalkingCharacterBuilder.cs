using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace catalyst.xr67.EditorTools
{
    /// <summary>
    /// Builds a ready-to-talk character prefab around a Character Creator
    /// prefab: the container with CharacterReferenceHolder, LipSyncAnalyzer,
    /// VisemeDriver, GazeDriver, NpcSpeechPlayer and AgitationResponder; the
    /// voice AudioSource; ProceduralIdleMotion (and optionally an
    /// AnimationSequencePlayer) on the character; a SpeechToText object with
    /// MicrophoneRecorder + ElevenLabsRealtimeSTT; and a Conversation object
    /// with CharacterConversation + CharacterSession. Every reference is
    /// wired explicitly, the character prefab stays a nested prefab, and the
    /// result is saved as its own prefab next to the character.
    ///
    /// Tools &gt; Catalyst &gt; Talking Character Builder, or headless:
    /// <c>-executeMethod catalyst.xr67.EditorTools.TalkingCharacterBuilder.BuildFromCommandLine
    /// -characterPrefab Assets/.../Mel.prefab [-prefabName "Mel Talking"] [-voiceId id]
    /// [-prompt "..."] [-stance Standing|Seated] [-poseDriver KeepAnimatorController|SequencePlayer]
    /// [-idleClip Assets/.../clip.fbx]</c>.
    /// </summary>
    public class TalkingCharacterBuilder : EditorWindow
    {
        public enum PoseDriver
        {
            /// <summary>Leave the character's AnimatorController in charge of the pose (its idle state plays; mood speaking loops are unavailable).</summary>
            KeepAnimatorController,

            /// <summary>Add an AnimationSequencePlayer looping one idle clip; mood speaking loops can play over it.</summary>
            SequencePlayer,
        }

        [Serializable]
        public class Options
        {
            public GameObject characterPrefab;
            public string prefabName = "";
            public PoseDriver poseDriver = PoseDriver.KeepAnimatorController;
            public AnimationClip idleClip;
            public IdleStance stance = IdleStance.Standing;
            public string voiceId = "21m00Tcm4TlvDq8ikWAM";
            public string ttsModelId = "eleven_flash_v2_5";
            [TextArea(3, 8)]
            public string systemPrompt = "You are a friendly person standing in front of the user in VR, having a relaxed chat with them. Stay in character and keep every answer short.";
            public bool includeSpeechInput = true;
            public Transform gazeTarget;
            public bool keepInOpenScene = true;
        }

        [SerializeField] private Options options = new Options();
        private Vector2 scroll;

        [MenuItem("Tools/Catalyst/Talking Character Builder")]
        private static void Open()
        {
            GetWindow<TalkingCharacterBuilder>("Talking Character");
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Character", EditorStyles.boldLabel);
            options.characterPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("CC character prefab", "A Character Creator export imported through CC Unity Tools (BoneDriver on CC_Base_Body)."), options.characterPrefab, typeof(GameObject), false);
            options.prefabName = EditorGUILayout.TextField(new GUIContent("Prefab name", "Empty = \"<character> Talking\". Saved next to the character prefab."), options.prefabName);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Body", EditorStyles.boldLabel);
            options.poseDriver = (PoseDriver)EditorGUILayout.EnumPopup(new GUIContent("Pose driver", "Keep the AnimatorController (its idle state plays, mood speaking loops are off) or add an AnimationSequencePlayer looping one idle clip."), options.poseDriver);
            if (options.poseDriver == PoseDriver.SequencePlayer)
                options.idleClip = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent("Idle clip", "Loop-enabled idle. Empty = the default state of the character's controller."), options.idleClip, typeof(AnimationClip), false);
            options.stance = (IdleStance)EditorGUILayout.EnumPopup(new GUIContent("Stance", "Standing: weight shifts travel the pelvis sideways and the feet stay planted."), options.stance);
            options.gazeTarget = (Transform)EditorGUILayout.ObjectField(new GUIContent("Gaze target", "Empty = Camera.main at runtime (the XR camera)."), options.gazeTarget, typeof(Transform), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Voice and conversation", EditorStyles.boldLabel);
            options.voiceId = EditorGUILayout.TextField(new GUIContent("ElevenLabs voice id"), options.voiceId);
            options.ttsModelId = EditorGUILayout.TextField(new GUIContent("TTS model"), options.ttsModelId);
            EditorGUILayout.LabelField("System prompt");
            options.systemPrompt = EditorGUILayout.TextArea(options.systemPrompt, GUILayout.MinHeight(70));
            options.includeSpeechInput = EditorGUILayout.Toggle(new GUIContent("Include mic + speech-to-text", "Adds a SpeechToText child with MicrophoneRecorder and ElevenLabsRealtimeSTT. Off when the scene already has one."), options.includeSpeechInput);
            options.keepInOpenScene = EditorGUILayout.Toggle(new GUIContent("Keep instance in open scene", "Leave the built object in the current scene, linked to the saved prefab."), options.keepInOpenScene);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Needs a CharacterServicesConfig asset at Resources/CharacterServicesConfig (keys + endpoints) in this project.", MessageType.Info);
            using (new EditorGUI.DisabledScope(options.characterPrefab == null))
            {
                if (GUILayout.Button("Build prefab", GUILayout.Height(28)))
                {
                    try
                    {
                        GameObject prefab = Build(options);
                        EditorGUIUtility.PingObject(prefab);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"TalkingCharacterBuilder: {e.Message}");
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ build

        /// <summary>Builds the hierarchy, saves it as a prefab next to the character prefab, returns the prefab asset.</summary>
        public static GameObject Build(Options o)
        {
            if (o == null || o.characterPrefab == null)
                throw new ArgumentException("No character prefab given.");

            string characterPath = AssetDatabase.GetAssetPath(o.characterPrefab);
            if (string.IsNullOrEmpty(characterPath))
                throw new ArgumentException("The character must be a prefab asset, not a scene object.");

            string name = string.IsNullOrWhiteSpace(o.prefabName) ? o.characterPrefab.name + " Talking" : o.prefabName.Trim();
            string folder = Path.GetDirectoryName(characterPath)?.Replace('\\', '/') ?? "Assets";
            string savePath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.prefab");

            GameObject root = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(root, "Build Talking Character");

            // The character stays a nested prefab so a re-export of the CC
            // character flows through.
            var character = (GameObject)PrefabUtility.InstantiatePrefab(o.characterPrefab);
            character.name = o.characterPrefab.name;
            character.transform.SetParent(root.transform, false);

            // Voice output — the one AudioSource every driver reads.
            var audioGo = new GameObject("Audio");
            audioGo.transform.SetParent(root.transform, false);
            AudioSource audio = audioGo.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.loop = false;
            audio.spatialBlend = 1f;
            audio.minDistance = 1f;
            audio.maxDistance = 25f;

            // Registry.
            var holder = root.AddComponent<CharacterReferenceHolder>();
            holder.characterRoot = character;
            holder.voiceSource = audio;
            holder.ResolveReferences();
            if (holder.headBone != null)
                audioGo.transform.position = holder.headBone.position;
            if (holder.bodyMesh == null)
                Debug.LogWarning("TalkingCharacterBuilder: no CC_Base_Body under the character — lip sync, gaze and expression need the CC body mesh.", root);
            if (!HasComponentNamed(character, "BoneDriver"))
                Debug.LogWarning("TalkingCharacterBuilder: the character carries no Reallusion BoneDriver — the eyes and jaw will not follow the blendshapes. Import the character through CC Unity Tools.", root);

            // Face + gaze + voice on the container.
            var analyzer = root.AddComponent<LipSyncAnalyzer>();
            SetRef(analyzer, "audioSource", audio);

            var visemes = root.AddComponent<VisemeDriver>();
            visemes.character = holder;
            visemes.analyzer = analyzer;
            visemes.bodyMeshOverride = holder.bodyMesh;

            var gaze = root.AddComponent<GazeDriver>();
            gaze.character = holder;
            gaze.analyzer = analyzer;
            gaze.bodyMeshOverride = holder.bodyMesh;
            gaze.target = o.gazeTarget;

            var speech = root.AddComponent<NpcSpeechPlayer>();
            SetRef(speech, "audioSource", audio);
            SetString(speech, "voiceId", o.voiceId);
            SetString(speech, "ttsModelId", o.ttsModelId);

            // Body layers live next to the Animator.
            GameObject animatorGo = holder.animator != null ? holder.animator.gameObject : character;
            var idle = animatorGo.AddComponent<ProceduralIdleMotion>();
            idle.settings.stance = o.stance;

            AnimationSequencePlayer sequence = null;
            if (o.poseDriver == PoseDriver.SequencePlayer)
            {
                sequence = animatorGo.AddComponent<AnimationSequencePlayer>();
                AnimationClip clip = o.idleClip != null ? o.idleClip : DefaultClip(holder.animator);
                if (clip != null)
                {
                    sequence.steps.Add(new AnimationSequencePlayer.Step { clip = clip, hold = clip.length, blendIn = 0.4f, speed = 1f });
                    if (!clip.isLooping)
                        Debug.LogWarning($"TalkingCharacterBuilder: idle clip '{clip.name}' is not loop-enabled — the cycle will pop at the seam.", root);
                }
                else
                {
                    Debug.LogWarning("TalkingCharacterBuilder: no idle clip found — assign one on the AnimationSequencePlayer.", root);
                }
            }

            var responder = root.AddComponent<AgitationResponder>();
            SetRef(responder, "character", holder);
            SetRef(responder, "sequencePlayer", sequence);
            SetRef(responder, "idleMotion", idle);
            SetRef(responder, "speechPlayer", speech);

            // Speech input.
            MicrophoneRecorder mic = null;
            ElevenLabsRealtimeSTT stt = null;
            if (o.includeSpeechInput)
            {
                var sttGo = new GameObject("SpeechToText");
                sttGo.transform.SetParent(root.transform, false);
                mic = sttGo.AddComponent<MicrophoneRecorder>();
                mic.suppressWhilePlaying = new[] { audio };
                stt = sttGo.AddComponent<ElevenLabsRealtimeSTT>();
                SetRef(stt, "microphone", mic);
            }

            // Conversation + session.
            var convGo = new GameObject("Conversation");
            convGo.transform.SetParent(root.transform, false);
            var conversation = convGo.AddComponent<CharacterConversation>();
            SetRef(conversation, "realtimeSpeechToText", stt);
            SetRef(conversation, "microphone", mic);
            SetRef(conversation, "speechPlayer", speech);
            SetString(conversation, "systemPrompt", o.systemPrompt);

            var session = convGo.AddComponent<CharacterSession>();
            SetRef(session, "character", holder);
            SetRef(session, "conversation", conversation);
            SetRef(session, "microphone", mic);

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, savePath, InteractionMode.UserAction);
            if (!o.keepInOpenScene)
                Undo.DestroyObjectImmediate(root);
            else
                Selection.activeGameObject = root;

            Debug.Log($"TalkingCharacterBuilder: saved '{savePath}' — {(sequence != null ? "AnimationSequencePlayer" : "AnimatorController")} drives the pose, stance {o.stance}, speech input {(o.includeSpeechInput ? "included" : "left to the scene")}.", prefab);
            return prefab;
        }

        // ------------------------------------------------------------------ headless

        /// <summary>
        /// Batch-mode entry: <c>-characterPrefab</c> (asset path, required),
        /// <c>-prefabName</c>, <c>-voiceId</c>, <c>-ttsModel</c>, <c>-prompt</c>,
        /// <c>-stance</c>, <c>-poseDriver</c>, <c>-idleClip</c> (asset path),
        /// <c>-noSpeechInput</c>.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string key)
            {
                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }
            bool Flag(string key) => Array.Exists(args, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

            var o = new Options
            {
                characterPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Arg("-characterPrefab") ?? ""),
                prefabName = Arg("-prefabName") ?? "",
                includeSpeechInput = !Flag("-noSpeechInput"),
                keepInOpenScene = false,
            };
            if (o.characterPrefab == null)
                throw new ArgumentException("-characterPrefab <asset path> is required.");
            if (Arg("-voiceId") != null) o.voiceId = Arg("-voiceId");
            if (Arg("-ttsModel") != null) o.ttsModelId = Arg("-ttsModel");
            if (Arg("-prompt") != null) o.systemPrompt = Arg("-prompt");
            if (Arg("-stance") != null && Enum.TryParse(Arg("-stance"), true, out IdleStance stance)) o.stance = stance;
            if (Arg("-poseDriver") != null && Enum.TryParse(Arg("-poseDriver"), true, out PoseDriver driver)) o.poseDriver = driver;
            if (Arg("-idleClip") != null) o.idleClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(Arg("-idleClip"));

            Build(o);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Default-state clip of layer 0 of the Animator's controller, else its first clip.</summary>
        private static AnimationClip DefaultClip(Animator animator)
        {
            if (animator == null) return null;
            RuntimeAnimatorController runtime = animator.runtimeAnimatorController;
            if (runtime is AnimatorOverrideController overrideController)
                runtime = overrideController.runtimeAnimatorController;
            if (runtime is UnityEditor.Animations.AnimatorController controller
                && controller.layers.Length > 0
                && controller.layers[0].stateMachine.defaultState != null
                && controller.layers[0].stateMachine.defaultState.motion is AnimationClip defaultClip)
                return defaultClip;
            AnimationClip[] clips = runtime != null ? runtime.animationClips : null;
            return clips != null && clips.Length > 0 ? clips[0] : null;
        }

        private static bool HasComponentNamed(GameObject root, string typeName)
        {
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == typeName) return true;
            return false;
        }

        private static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"TalkingCharacterBuilder: {target.GetType().Name} has no serialized field '{field}'."); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetString(Object target, string field, string value)
        {
            var so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"TalkingCharacterBuilder: {target.GetType().Name} has no serialized field '{field}'."); return; }
            p.stringValue = value ?? "";
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
