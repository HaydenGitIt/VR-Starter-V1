using System.Text;

namespace catalyst.xr67
{
    /// <summary>
    /// Incrementally pulls the "reply" string value out of a streamed
    /// structured-output JSON response, so TTS can start speaking before the
    /// full JSON has arrived. Feed each raw text delta in; back comes the
    /// unescaped piece of the reply it contained — often empty, since the
    /// JSON preamble, the string's closing quote and every later field
    /// produce no prose. Everything after the reply value is swallowed;
    /// the caller parses the complete JSON at end of stream for the rest.
    ///
    /// One instance per response — state is not resettable.
    /// </summary>
    public class LlmStreamReplyExtractor
    {
        private const string Key = "\"reply\"";

        private enum State
        {
            SeekingKey,    // scanning for the literal "reply" key
            SeekingColon,  // key seen — skipping to the colon
            SeekingQuote,  // colon seen — skipping to the value's opening quote
            InString,      // inside the reply value, emitting prose
            Done,          // reply closed — swallow the rest of the stream
        }

        private State _state = State.SeekingKey;
        private int _keyMatch;
        private bool _escaping;
        private int _unicodeRemaining;
        private int _unicodeValue;
        private readonly StringBuilder _fragment = new StringBuilder();

        /// <summary>True once the whole reply value has been emitted.</summary>
        public bool ReplyComplete => _state == State.Done;

        /// <summary>
        /// Consumes one stream delta and returns the prose it contributed to
        /// the reply (possibly the empty string).
        /// </summary>
        public string Feed(string delta)
        {
            if (string.IsNullOrEmpty(delta) || _state == State.Done)
                return string.Empty;

            _fragment.Length = 0;
            foreach (char c in delta)
            {
                switch (_state)
                {
                    case State.SeekingKey:
                        if (c == Key[_keyMatch])
                        {
                            if (++_keyMatch == Key.Length) _state = State.SeekingColon;
                        }
                        else
                        {
                            _keyMatch = c == Key[0] ? 1 : 0;
                        }
                        break;

                    case State.SeekingColon:
                        // Only whitespace can sit between key and colon.
                        if (c == ':') _state = State.SeekingQuote;
                        break;

                    case State.SeekingQuote:
                        if (c == '"') _state = State.InString;
                        break;

                    case State.InString:
                        ConsumeStringChar(c);
                        break;
                }

                if (_state == State.Done)
                    break;
            }
            return _fragment.ToString();
        }

        private void ConsumeStringChar(char c)
        {
            if (_unicodeRemaining > 0)
            {
                _unicodeValue = (_unicodeValue << 4) | HexValue(c);
                if (--_unicodeRemaining == 0) _fragment.Append((char)_unicodeValue);
                return;
            }

            if (_escaping)
            {
                _escaping = false;
                switch (c)
                {
                    case 'n': _fragment.Append('\n'); break;
                    case 't': _fragment.Append('\t'); break;
                    case 'r': _fragment.Append('\r'); break;
                    case 'b': _fragment.Append('\b'); break;
                    case 'f': _fragment.Append('\f'); break;
                    case 'u': _unicodeRemaining = 4; _unicodeValue = 0; break;
                    default: _fragment.Append(c); break; // \" \\ \/ pass through
                }
                return;
            }

            if (c == '\\') { _escaping = true; return; }
            if (c == '"') { _state = State.Done; return; }
            _fragment.Append(c);
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }
    }
}
