# Catalyst Character

Living-character drivers for a Reallusion Character Creator (CC) export, pulled out of the xr67 classroom project so a second project can use the same stack: lip sync, conversational gaze, procedural idle life (seated or standing), mood-driven expression and speaking loops, a Claude conversation and ElevenLabs speech-to-text and voice.

Everything is plain MonoBehaviours wired by serialized references, scene lookups and C# events. No Robotlegs, no Timeline, no UI.

## Prerequisites in the consuming project

| Need | Why | How |
|---|---|---|
| **UniTask** | every network/speech call | `"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask"` in `Packages/manifest.json` |
| **CC Unity Tools** (soupday `com.soupday.cc3_unity_tools`) and a character imported through it | the importer installs `Assets/Reallusion/CCiC Unity Tools/Runtime` (asmdef `soupday.cc3_unity_tools.project_runtime`) which holds **BoneDriver**. The eye-look and viseme blendshapes only move the eye and jaw bones, and only reach the teeth/tongue/brow meshes, through BoneDriver. The package assembly references that asmdef by name, so the project does not compile until a CC character has been imported once. | Package Manager → add by git URL `https://github.com/soupday/CCiC-Unity-Tools.git#2.2.6`, then import the character |
| **Newtonsoft JSON** (`com.unity.nuget.newtonsoft-json`) | BoneDriver's glossary | declared as a package dependency |
| A **`CharacterServicesConfig`** asset at `Resources/CharacterServicesConfig` | Anthropic + ElevenLabs keys and endpoints. Keys never live in source. | Assets → Create → Catalyst → Character Services Config. Keep real keys out of public repos; point the base URLs at a proxy for a public build. |

Optional: apply the `BoneDriver` performance patch from xr67 (cached camera/frustum planes, skip unchanged source shapes — marked `XR67-PERF` in that project's copy). A CC re-import overwrites the file, so keep the patch handy.

## Building a talking character prefab

**Tools → Catalyst → Talking Character Builder.** Drop in the CC character prefab, pick the pose driver (keep its AnimatorController, or an AnimationSequencePlayer looping one idle clip), the stance, the ElevenLabs voice id and the system prompt, then **Build prefab**. The result is saved next to the character prefab as `<character> Talking.prefab`, with the character nested inside it, every reference wired, and the mic / speech-to-text / conversation / session objects as children. Headless: `-executeMethod catalyst.xr67.EditorTools.TalkingCharacterBuilder.BuildFromCommandLine -characterPrefab Assets/…/Mel.prefab`.

The hierarchy it builds is the contract below.

## Scene contract

One character, one container:

```
Character Container            CharacterReferenceHolder (Resolve From Character Root fills it)
│                              LipSyncAnalyzer   (audioSource = Audio)
│                              VisemeDriver      (writes V_* visemes on CC_Base_Body)
│                              GazeDriver        (target = the XR camera / Camera.main)
│                              NpcSpeechPlayer   (audioSource = Audio, ElevenLabs voice id)
│                              AgitationResponder (optional — CharacterSession adds one)
├── Audio                      AudioSource, no clip, Play On Awake off
└── <CC prefab instance>       Animator (humanoid), BoneDriver on CC_Base_Body
                               AnimationSequencePlayer (standing idle clip; speaking loops come from the mood bands)
                               ProceduralIdleMotion  (settings.stance = Standing for a standing character)
SpeechToText                   MicrophoneRecorder (16 kHz, VAD), ElevenLabsRealtimeSTT
Conversation                   CharacterConversation (system prompt, model, {reply, mood} on)
Session                        CharacterSession (finds everything above; Begin on enable)
```

**Random idle variety:** instead of (not alongside) the AnimationSequencePlayer, put a `StudentAmbientPlayer` next to ProceduralIdleMotion and give it a `StudentBehaviourProfile` asset (Assets → Create → xr67 → Student Behaviour Profile): per mode a base idle loop plus a weighted pool of one-shot clips played at random intervals with random speeds, crossfaded. Its per-mode idle settings override ProceduralIdleMotion's local ones. Tick **Play In Edit Mode** on it (and keep Preview In Edit Mode on the idle layer) to watch loops, one-shots and the procedural idle in the Scene view. It drives the Animator through its own PlayableGraph, so the AnimatorController is bypassed while it runs, and mood speaking loops (AgitationResponder) stay off because those need the sequence player's override layer.

Rules that must stay true:

- Exactly **one pose driver** on the Animator: AnimationSequencePlayer or RuntimeClipLoop.
- Exactly **one blink writer**: ProceduralIdleMotion takes it whenever its blinking is enabled; GazeDriver blinks only otherwise.
- **Execution order** (LateUpdate): VisemeDriver −100 → ProceduralIdleMotion −12 → BonePoseSmoother −11 → GazeDriver −10 → BoneDriver 0 → ProceduralIdleMotion's FaceWriter +5. Do not change the `DefaultExecutionOrder` attributes.
- ProceduralIdleMotion's face layer, blinks and the mood expression all stop when its body idle is disabled or the rig is not humanoid — keep `settings.enabled` on.
- The CC character needs the standard shapes: `V_*` visemes, `Eye_L/R_Look_*`, `Eye_Blink_L/R`, and the ExPlus brow/cheek/mouth/nose shapes listed in `ProceduralIdleMotion.FaceShapeNames`.

## Driving the character from your own scene logic

Put a **`TalkingCharacterDriver`** on any GameObject in the scene (not inside the character) and drag the talking character prefab instance into **Character**. That is the only component a scene needs to touch.

| Field / call | What it does |
|---|---|
| **Scenario Prompt** | Who the character is, where she is, who the user is, how she behaves. Empty = the prompt saved on the prefab's `Conversation` object. `SetScenarioPrompt(text)` at runtime. |
| **Start Mode** | `NonInteractive` (idle, quiet), `Interactive` (listening from frame one) or `Demo`. |
| `Demo` | **Demo Silence Seconds** of quiet idle → **Opening Line** → conversation open (`onDemoFinished`). `PlayDemo()` restarts it. |
| `SetInteractive(false)` | She idles, blinks, looks at the user and can speak lines, but the mic is off and nothing is sent to the LLM. Switching off mid-reply cuts the reply. |
| `SetInteractive(true)` | She listens and answers through Claude and ElevenLabs, the same way the classroom kids do. |
| `Say(line)` / `yield return SayAndWait(line)` | Speaks a fixed line with her ElevenLabs voice, in either mode. While it plays nothing the user says is answered. The line is passed to the LLM afterwards so she carries on from it instead of repeating it. |
| `StopSpeaking()`, `ResetConversation()` | Cut her off; forget the history and the scripted lines. |
| Events | `onUserSpoke(string)`, `onCharacterReplied(string)`, `onLineFinished`, `onDemoFinished`: hook these up in the inspector or from code. |

The driver switches the prefab's `CharacterSession` auto-begin off in its Awake, so with a driver in the scene the start mode alone decides whether she listens.

## Runtime flow

`MicrophoneRecorder` (VAD) → `ElevenLabsRealtimeSTT` (Scribe WebSocket) → `CharacterConversation` (Claude, streamed `{reply, mood}` JSON) → `NpcSpeechPlayer` (sentence-by-sentence ElevenLabs TTS, loudness-levelled, queued in order) → the character's AudioSource → `LipSyncAnalyzer` → `VisemeDriver`. `GazeDriver` reads the analyzer's loudness for its speaking rhythm. `CharacterSession` forwards the mood to `AgitationResponder`, which blends the face through ProceduralIdleMotion's held-expression layer and plays a speaking loop over the ambient sequence.

Turn-taking: an utterance that arrives while a reply is in flight is dropped, the mic is muted while the character speaks (NpcSpeechPlayer registers its AudioSource with the recorder), and an optional silence nudge lets the character speak up after N quiet seconds.

## Standing characters

`ProceduralIdleMotion` was written for seated characters (weight shifts roll the pelvis). With `IdleMotionSettings.stance = Standing` the weight shift becomes a sideways pelvis travel (`shiftLateralCentimetres`) plus a reduced roll (`standingRollScale`), and both legs are solved back with a two-bone IK so the feet stay where the animation put them. Tune the two values on the character; the seated path is unchanged.

## What is deliberately not here

Scenario scoring, intro timelines and reaction beats, the classroom student behaviours, telemetry upload, on-device Whisper, and any UI. Those stay in the xr67 project, which consumes this package as an embedded package (`Packages/com.catalyst.character`).
