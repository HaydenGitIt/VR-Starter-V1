using System.Collections.Generic;
using UnityEngine;

namespace catalyst.xr67
{
    /// <summary>
    /// One-component bring-up of a talking character: on <see cref="Begin"/>
    /// (automatically on enable by default) it opens the microphone, switches
    /// the <see cref="CharacterConversation"/> on, and gives the character an
    /// <see cref="AgitationResponder"/> fed from the conversation's mood so
    /// the face and the speaking loops follow the replies. <see cref="End"/>
    /// takes it all down again. Drop it anywhere in the scene; every
    /// reference is found when left empty.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterSession : MonoBehaviour
    {
        [Header("References (found in the scene when left empty)")]
        [Tooltip("The character being talked to — the mood responder is added next to this.")]
        [SerializeField] private CharacterReferenceHolder character;

        [SerializeField] private CharacterConversation conversation;

        [SerializeField] private MicrophoneRecorder microphone;

        [Header("Session")]
        [Tooltip("Begin the session as soon as this component is enabled.")]
        [SerializeField] private bool beginOnEnable = true;

        [Header("Mood")]
        [Tooltip("Blend the character's face between a slight smile and a frown from the reported mood (needs ProceduralIdleMotion on the character).")]
        [SerializeField] private bool driveFaceFromMood = true;

        [Tooltip("Speaking loops per mood band, played over the ambient sequence while a reply is spoken. Empty = the ambient sequence keeps playing.")]
        [SerializeField] private List<AgitationResponse> speakingMoods = new List<AgitationResponse>();

        [Tooltip("List the character's phase one-shots (profile Calm / Transition / Agitated sets, Offer To Llm on) in the LLM's reply format, so the LLM picks the one-shot that fits each reply.")]
        [SerializeField] private bool offerGesturesToLlm = true;

        public bool IsLive { get; private set; }

        /// <summary>
        /// Auto-begin on enable. The first auto-begin waits for Start, so a
        /// driver (see <see cref="TalkingCharacterDriver"/>) can switch this
        /// off in its Awake and keep the character quiet from frame one.
        /// </summary>
        public bool BeginOnEnable
        {
            get => beginOnEnable;
            set => beginOnEnable = value;
        }

        private AgitationResponder _responder;
        private bool _started;

        private void OnEnable()
        {
            if (beginOnEnable && _started) Begin();
        }

        private void Start()
        {
            _started = true;
            if (beginOnEnable && isActiveAndEnabled) Begin();
        }

        private void OnDisable()
        {
            if (IsLive) End();
        }

        /// <summary>Mic on, conversation on, mood responder armed.</summary>
        public void Begin()
        {
            if (IsLive) return;
            Resolve();
            if (conversation == null)
            {
                Debug.LogError("CharacterSession: no CharacterConversation in the scene — nothing to run.", this);
                return;
            }
            IsLive = true;

            _responder = ResolveResponder(create: driveFaceFromMood || HasLoops(speakingMoods));
            if (_responder != null)
                _responder.BeginScenario(speakingMoods, driveFaceFromMood);

            // The character's one-shots go into the LLM's reply format, so it
            // can pick the one that fits each reply.
            if (offerGesturesToLlm && _responder != null)
                conversation.SetGestureOptions(_responder.LlmGestureOptions());

            conversation.OnUserUtterance += OnUserUtterance;
            conversation.OnMoodAssessed += OnMoodAssessed;
            conversation.OnPhaseAssessed += OnPhaseAssessed;
            conversation.OnGestureChosen += OnGestureChosen;
            conversation.enabled = true;

            if (microphone != null)
            {
                microphone.enabled = true;
                microphone.StartListening();
            }
        }

        /// <summary>
        /// Mic off, conversation off (its speech stops), face released. Also
        /// works before any Begin — the mic listens and the conversation runs
        /// on their own from load, so this is how a character is kept quiet.
        /// </summary>
        public void End()
        {
            Resolve();
            IsLive = false;

            if (microphone != null) microphone.StopListening();
            if (conversation != null)
            {
                conversation.OnUserUtterance -= OnUserUtterance;
                conversation.OnMoodAssessed -= OnMoodAssessed;
                conversation.OnPhaseAssessed -= OnPhaseAssessed;
                conversation.OnGestureChosen -= OnGestureChosen;
                conversation.enabled = false;
            }
            if (_responder != null) _responder.EndScenario();
            _responder = null;
        }

        private void Resolve()
        {
            if (character == null) character = FindFirstObjectByType<CharacterReferenceHolder>(FindObjectsInactive.Include);
            if (conversation == null) conversation = FindFirstObjectByType<CharacterConversation>(FindObjectsInactive.Include);
            if (microphone == null) microphone = FindFirstObjectByType<MicrophoneRecorder>(FindObjectsInactive.Include);
        }

        private AgitationResponder ResolveResponder(bool create)
        {
            if (character == null) return null;
            AgitationResponder responder = character.GetComponentInChildren<AgitationResponder>(true);
            if (responder == null && create)
                responder = character.gameObject.AddComponent<AgitationResponder>();
            return responder;
        }

        private static bool HasLoops(List<AgitationResponse> moods)
        {
            if (moods == null) return false;
            foreach (AgitationResponse band in moods)
                if (band != null && band.HasLoops) return true;
            return false;
        }

        private void OnUserUtterance(string _)
        {
            if (_responder != null) _responder.ReplyPending();
        }

        private void OnMoodAssessed(int mood)
        {
            if (_responder != null) _responder.SetAgitation(mood);
        }

        private void OnPhaseAssessed(ConversationPhase phase)
        {
            if (_responder != null) _responder.SetPhase(phase);
        }

        private void OnGestureChosen(string gesture, int fit)
        {
            if (_responder != null) _responder.PlayChosenGesture(gesture, fit);
        }
    }
}
