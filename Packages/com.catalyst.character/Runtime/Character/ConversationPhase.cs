namespace catalyst.xr67
{
    /// <summary>
    /// Where a conversational character is emotionally, as the LLM reports it
    /// with every reply. Picks the idle loop and the gesture / reaction pool
    /// (see <see cref="StudentBehaviourProfile.Get(ConversationPhase)"/>).
    /// </summary>
    public enum ConversationPhase
    {
        /// <summary>Relaxed, reassured, neutral stance.</summary>
        Calm,
        /// <summary>Still tense, mild concern, considering and thinking.</summary>
        Transition,
        /// <summary>Restless, fidgeting, confrontational, insistent.</summary>
        Agitated,
    }

    public static class ConversationPhaseUtil
    {
        /// <summary>Parses the LLM's phase string (case-insensitive). False for missing / unknown values.</summary>
        public static bool TryParse(string value, out ConversationPhase phase)
        {
            phase = ConversationPhase.Calm;
            if (string.IsNullOrWhiteSpace(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "calm": phase = ConversationPhase.Calm; return true;
                case "transition": phase = ConversationPhase.Transition; return true;
                case "agitated": phase = ConversationPhase.Agitated; return true;
                default: return false;
            }
        }
    }
}
