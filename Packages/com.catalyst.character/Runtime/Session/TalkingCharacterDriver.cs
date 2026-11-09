using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace catalyst.xr67
{
    /// <summary>
    /// The one component a scene needs to direct a talking character prefab
    /// (e.g. "Mel Talking") without knowing the stack underneath. Drop it on
    /// any GameObject, drag the character in, and either pick a start mode
    /// or call the public methods from your own scripts / UnityEvents:
    ///
    ///   <see cref="SetInteractive"/> — false: the character idles, looks at
    ///   the user and can speak scripted lines, but does not listen (welcome
    ///   screens, cutscenes). true: she listens and answers through the LLM,
    ///   exactly like the classroom kids.
    ///   <see cref="Say"/> / <see cref="SayAndWait"/> — speak a fixed line
    ///   with the character's ElevenLabs voice, in either mode. Lines are
    ///   remembered and given to the LLM, so she carries on from them.
    ///   <see cref="SetScenarioPrompt"/> — who she is and what the scene is.
    ///
    /// Start mode Demo runs: quiet for a few seconds → the opening line →
    /// full conversation.
    /// </summary>
    [DisallowMultipleComponent]
    public class TalkingCharacterDriver : MonoBehaviour
    {
        public enum StartMode
        {
            /// <summary>Idle and quiet. Call SetInteractive / Say from your own logic.</summary>
            NonInteractive,
            /// <summary>Listening and answering from the first frame.</summary>
            Interactive,
            /// <summary>Quiet for Demo Silence seconds, speaks the Opening Line, then listens and answers.</summary>
            Demo,
        }

        [Header("Character")]
        [Tooltip("The talking character prefab instance in the scene (e.g. Mel Talking). Left empty = the first one found.")]
        [SerializeField] private GameObject character;

        [Header("Scenario")]
        [Tooltip("Who the character is, where she is, who the user is and how she should behave. Empty = keep the prompt saved on the character's Conversation object.")]
        [TextArea(4, 12)]
        [SerializeField] private string scenarioPrompt = "";

        [Header("Start")]
        [SerializeField] private StartMode startMode = StartMode.Demo;

        [Tooltip("Demo: seconds of quiet idle before the opening line.")]
        [SerializeField] private float demoSilenceSeconds = 3f;

        [Tooltip("Demo: the fixed line spoken before the conversation opens up.")]
        [TextArea(2, 4)]
        [SerializeField] private string openingLine = "Hi there! Have you got a minute? I wanted to ask you something.";

        [Header("Events (hook your own logic here)")]
        [Tooltip("The user finished a sentence (the transcript), interactive mode only.")]
        public UnityEvent<string> onUserSpoke = new UnityEvent<string>();

        [Tooltip("The character's LLM answer, once it fully arrived.")]
        public UnityEvent<string> onCharacterReplied = new UnityEvent<string>();

        [Tooltip("A line started with Say / SayAndWait finished playing (or was cut off).")]
        public UnityEvent onLineFinished = new UnityEvent();

        [Tooltip("Demo: the opening line finished and the conversation is open.")]
        public UnityEvent onDemoFinished = new UnityEvent();

        /// <summary>True while the character listens and answers through the LLM.</summary>
        public bool IsInteractive { get; private set; }

        /// <summary>True while any speech plays — scripted lines and LLM replies alike.</summary>
        public bool IsSpeaking => _voice != null && _voice.IsSpeaking;

        /// <summary>True from <see cref="Say"/> until that line finished playing.</summary>
        public bool IsSayingLine => _lineRoutine != null;

        // Longest wait for ElevenLabs to return the first audio of a line.
        private const float LineStartTimeout = 15f;

        private CharacterSession _session;
        private CharacterConversation _conversation;
        private NpcSpeechPlayer _voice;
        private readonly List<string> _spokenLines = new List<string>();
        private System.Func<string> _otherAugmenter;
        private Coroutine _lineRoutine;
        private Coroutine _demoRoutine;

        // ------------------------------------------------------------------ public API

        /// <summary>false = idle and quiet (still looks at the user, can Say lines); true = listens and answers through the LLM.</summary>
        public void SetInteractive(bool interactive)
        {
            if (!Resolve()) return;
            IsInteractive = interactive;
            if (interactive)
            {
                // A scripted line still playing keeps the conversation paused
                // until it ends (see LineRoutine).
                _session.Begin();
                _conversation.Paused = IsSayingLine;
            }
            else
            {
                _session.End();
            }
        }

        /// <summary>Speaks a fixed line with the character's voice (ElevenLabs), in either mode. Replaces a line still playing.</summary>
        public void Say(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !Resolve()) return;
            if (_lineRoutine != null) StopCoroutine(_lineRoutine);
            _lineRoutine = StartCoroutine(LineRoutine(line.Trim()));
        }

        /// <summary>Coroutine form of <see cref="Say"/>: <c>yield return driver.SayAndWait("...");</c> returns once the line finished.</summary>
        public IEnumerator SayAndWait(string line)
        {
            Say(line);
            while (_lineRoutine != null) yield return null;
        }

        /// <summary>Cuts off whatever the character is saying (scripted line or LLM reply).</summary>
        public void StopSpeaking()
        {
            if (!Resolve()) return;
            if (_lineRoutine != null)
            {
                StopCoroutine(_lineRoutine);
                _lineRoutine = null;
                if (IsInteractive) _conversation.Paused = false;
                onLineFinished.Invoke();
            }
            if (IsInteractive) _conversation.Interrupt();
            else _voice.Cancel();
        }

        /// <summary>Replaces who the character is / what the scene is. Takes effect from her next answer.</summary>
        public void SetScenarioPrompt(string prompt)
        {
            scenarioPrompt = prompt;
            if (Resolve()) _conversation.SetSystemPrompt(prompt);
        }

        /// <summary>Forgets everything said so far — the LLM history and the scripted lines.</summary>
        public void ResetConversation()
        {
            _spokenLines.Clear();
            if (Resolve()) _conversation.ResetHistory();
        }

        /// <summary>Runs the demo from the top: quiet → opening line → conversation.</summary>
        public void PlayDemo()
        {
            if (_demoRoutine != null) StopCoroutine(_demoRoutine);
            _demoRoutine = StartCoroutine(DemoRoutine());
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            // Before any Start: the session must not auto-begin, the start
            // mode below decides whether the character listens.
            if (Resolve()) _session.BeginOnEnable = false;
        }

        private void Start()
        {
            if (!Resolve()) return;
            if (!string.IsNullOrWhiteSpace(scenarioPrompt)) _conversation.SetSystemPrompt(scenarioPrompt);

            switch (startMode)
            {
                case StartMode.Interactive: SetInteractive(true); break;
                case StartMode.NonInteractive: SetInteractive(false); break;
                case StartMode.Demo: PlayDemo(); break;
            }
        }

        private void OnEnable()
        {
            if (!Resolve()) return;
            _otherAugmenter = _conversation.ContextAugmenter;
            _conversation.ContextAugmenter = BuildContextAddition;
            _conversation.OnUserUtterance += HandleUserUtterance;
            _conversation.OnReply += HandleReply;
        }

        private void OnDisable()
        {
            if (_conversation == null) return;
            if (_conversation.ContextAugmenter == (System.Func<string>)BuildContextAddition)
                _conversation.ContextAugmenter = _otherAugmenter;
            _conversation.OnUserUtterance -= HandleUserUtterance;
            _conversation.OnReply -= HandleReply;
            // Disabling stopped the coroutines — don't leave the conversation paused.
            if (_lineRoutine != null && IsInteractive) _conversation.Paused = false;
            _lineRoutine = null;
            _demoRoutine = null;
        }

        private bool Resolve()
        {
            if (_session != null && _conversation != null && _voice != null) return true;

            if (character == null)
            {
                // The builder puts Session as a direct child of the prefab root.
                CharacterSession session = FindFirstObjectByType<CharacterSession>(FindObjectsInactive.Include);
                if (session != null) character = session.transform.parent != null ? session.transform.parent.gameObject : session.gameObject;
            }
            if (character != null)
            {
                _session = character.GetComponentInChildren<CharacterSession>(true);
                _conversation = character.GetComponentInChildren<CharacterConversation>(true);
                _voice = character.GetComponentInChildren<NpcSpeechPlayer>(true);
            }
            if (_session != null && _conversation != null && _voice != null) return true;

            Debug.LogError("TalkingCharacterDriver: the character needs a CharacterSession, a CharacterConversation and an NpcSpeechPlayer under it (build it with Tools → Catalyst → Talking Character Builder).", this);
            enabled = false;
            return false;
        }

        // ------------------------------------------------------------------ sequences

        private IEnumerator DemoRoutine()
        {
            SetInteractive(false);
            if (demoSilenceSeconds > 0f) yield return new WaitForSeconds(demoSilenceSeconds);
            yield return SayAndWait(openingLine);
            SetInteractive(true);
            _demoRoutine = null;
            onDemoFinished.Invoke();
        }

        private IEnumerator LineRoutine(string line)
        {
            // Nothing the user says in the meantime is answered, and an LLM
            // reply in flight gives way to the line.
            if (IsInteractive)
            {
                _conversation.Interrupt();
                _conversation.Paused = true;
            }
            else
            {
                _voice.Cancel();
            }

            _spokenLines.Add(line);
            TestFlowLog.Emit("mel_line", $"Character says (scripted line): \"{line}\"", this, "text", line, "interactive", IsInteractive);
            _voice.Speak(line);

            // IsSpeaking comes up with the first synthesized clip, not on Speak.
            float waited = 0f;
            while (!_voice.IsSpeaking && waited < LineStartTimeout)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!_voice.IsSpeaking)
            {
                Debug.LogWarning($"TalkingCharacterDriver: \"{line}\" never started playing — check the ElevenLabs key / voice id.", this);
                TestFlowLog.Emit("mel_line_failed", $"Scripted line never started playing: \"{line}\"", this, "text", line);
            }
            while (_voice.IsSpeaking) yield return null;

            _lineRoutine = null;
            if (IsInteractive) _conversation.Paused = false;
            onLineFinished.Invoke();
        }

        // ------------------------------------------------------------------ conversation hooks

        private void HandleUserUtterance(string text)
        {
            if (text != CharacterConversation.SilenceMarker) onUserSpoke.Invoke(text);
        }

        private void HandleReply(string reply)
        {
            onCharacterReplied.Invoke(reply);
        }

        /// <summary>Tells the LLM which scripted lines the character has already said, so she carries on from them.</summary>
        private string BuildContextAddition()
        {
            string other = null;
            try { other = _otherAugmenter?.Invoke(); }
            catch (System.Exception e) { Debug.LogWarning($"TalkingCharacterDriver: context augmenter threw — {e.Message}", this); }

            if (_spokenLines.Count == 0) return other;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(other)) sb.Append(other.Trim()).Append("\n\n");
            sb.Append("Earlier in this conversation you already said these lines out loud, in this order. Carry on naturally from them and do not repeat them:");
            foreach (string line in _spokenLines) sb.Append("\n- \"").Append(line).Append('"');
            return sb.ToString();
        }
    }
}
