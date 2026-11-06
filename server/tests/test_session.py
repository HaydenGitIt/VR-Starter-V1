"""Tests for the scenario rules and message order, using fake engines."""

import dataclasses

import numpy as np
import pytest

from app import config, session
from app.engines import dialogue, director, stt, tts

_FAKE = config.EngineSettings(engine="fake", options={})
_RULES = config.Scenario(
    max_turns=8, strikes_allowed=3,
    levels=["agitated", "agitated", "distressed", "distressed", "softening", "calm"],
    speak_reply_on_failure=False, speak_reply_on_success=True,
    start_token="[START]",
    opening_line="Eighty dollars.", opening_verdict="neutral",
    fallback_line="Sorry, what was that?", fallback_verdict="neutral",
    max_retries=1, drop_truncated_fragment=True,
)
_GOOD = ("deescalated", "Thank you. That helps.")
_BAD = ("neutral", "That isn't what I asked.")


class Scripted(dialogue.Dialogue):
    """Replies from a script. None in the script means a malformed reply."""

    def __init__(self, *script: tuple[str, str] | None) -> None:
        """Initialises the script.

        Args:
            *script: (verdict, text) per call, or None to fail.
        """
        self._script = list(script)

    def generate(self, history: list[dict[str, str]], state_note: str = "",
                 judgement: director.Judgement | None = None) -> dialogue.Reply:
        """Returns the next scripted reply, streamed a character at a time."""
        entry = self._script.pop(0)
        if entry is None:
            raise dialogue.MalformedReply("no tags")
        return dialogue.Reply(entry[0], (c for c in entry[1]))


def _session(*script: tuple[str, str] | None, judge: director.Director | None = None,
             chunk_sentences: bool = True, **rules: object) -> session.ScenarioSession:
    """Creates a session with fake engines and a scripted Mel.

    Args:
        *script: Mel's replies, in order.
        judge: The director, or None for Mel's own tags to decide.
        chunk_sentences: Whether to speak each sentence as it completes.
        **rules: Overrides for the default scenario rules.

    Returns:
        A new session.
    """
    settings = config.Settings(
        host="127.0.0.1", port=8000,
        capture_sample_rate=16000, playback_sample_rate=24000, sentence_gap_ms=90,
        scenario=dataclasses.replace(_RULES, **rules),
        speech_to_text=_FAKE, dialogue=_FAKE, text_to_speech=_FAKE,
        chunk_sentences=chunk_sentences,
    )
    return session.ScenarioSession(
        settings, stt.FakeSpeechToText(), Scripted(*script), tts.FakeTextToSpeech(),
        director_engine=judge)


def _types(messages: list[session.Message]) -> list[str]:
    """Summarises messages, with each run of audio frames shown as "audio".

    Args:
        messages: Handler output.

    Returns:
        The message types in order.
    """
    types = ["audio" if isinstance(m, bytes) else m["type"] for m in messages]
    return [t for i, t in enumerate(types) if t != "audio" or types[i - 1:i] != ["audio"]]


def _say(scenario: session.ScenarioSession, line: str = "Hello.") -> list[session.Message]:
    """Sends a typed line.

    Args:
        scenario: The session.
        line: The trainee's line.

    Returns:
        Everything the session sent back.
    """
    return list(scenario.handle_command({"type": "text", "content": line}))


def _find(messages: list[session.Message], kind: str) -> dict:
    """Returns the first message of a type.

    Args:
        messages: Handler output.
        kind: The message type wanted.

    Returns:
        The message.
    """
    return next(m for m in messages if isinstance(m, dict) and m["type"] == kind)


def test_begin_speaks_fixed_opening() -> None:
    """The opening sends its tags first and uses no turn."""
    scenario = _session()
    messages = list(scenario.handle_command({"type": "begin"}))
    assert _types(messages) == ["tags", "audio", "reply", "turn_end"]
    assert _find(messages, "reply")["text"] == "Eighty dollars."
    assert _find(list(scenario.handle_command({"type": "summary"})), "summary")["turns"] == 0


def test_turn_order_and_sentence_gaps() -> None:
    """Tags come first; each sentence is its own frame with a gap between."""
    messages = _say(_session(_GOOD))
    assert _types(messages) == ["tags", "audio", "reply", "turn_complete", "turn_end"]
    frames = [m for m in messages if isinstance(m, bytes)]
    assert len(frames) == 3  # sentence, gap, sentence
    assert not any(frames[1])  # the gap is silence
    assert _find(messages, "turn_complete")["emotion"] == "agitated"


def test_whole_reply_is_spoken_in_one_go_without_chunking() -> None:
    """One synthesis for the reply, so no frames or gaps between its sentences."""
    messages = _say(_session(_GOOD, chunk_sentences=False))
    assert len([m for m in messages if isinstance(m, bytes)]) == 1
    assert _find(messages, "reply")["text"] == "Thank you. That helps."


def test_third_strike_cuts_before_any_audio() -> None:
    """The disengaging turn declares the ending and speaks nothing."""
    scenario = _session(_BAD, _BAD, _BAD)
    _say(scenario)
    _say(scenario)
    messages = _say(scenario)
    assert _types(messages) == ["tags", "ending", "reply", "turn_complete", "turn_end"]
    assert _find(messages, "ending")["outcome"] == "disengaged"
    assert _find(messages, "turn_end")["ended"] is True


def test_resolving_line_is_spoken() -> None:
    """On success Mel finishes her line before the ending Timeline."""
    scenario = _session(*[_GOOD] * 5)
    for _ in range(4):
        _say(scenario)
    assert _types(_say(scenario))[:3] == ["tags", "ending", "audio"]


def test_out_of_turns_disengages_after_speaking() -> None:
    """Running out of turns ends after the last line has been spoken.

    Valid rules mean real verdicts always hit a threshold within max_turns,
    so only fallback turns (which score nothing) can run the clock out.
    """
    scenario = _session(_GOOD, _BAD, None, None,
                        max_turns=3, levels=["agitated", "softening", "calm"],
                        strikes_allowed=2)
    _say(scenario)
    _say(scenario)
    messages = _say(scenario)
    assert _types(messages) == [
        "tags", "audio", "reply", "turn_complete", "ending", "turn_end"]
    assert _find(messages, "ending")["outcome"] == "disengaged"


def test_malformed_reply_is_retried() -> None:
    """One malformed generation is retried silently."""
    messages = _say(_session(None, _GOOD))
    assert _find(messages, "turn_complete")["verdict"] == "deescalated"


def test_repeated_malformed_replies_use_fallback_without_a_strike() -> None:
    """After the retries, Mel speaks the fallback; it uses a turn, not a strike."""
    scenario = _session(None, None)
    messages = _say(scenario)
    assert _types(messages) == ["tags", "audio", "reply", "turn_complete", "turn_end"]
    assert _find(messages, "reply")["text"] == "Sorry, what was that?"
    summary = _find(list(scenario.handle_command({"type": "summary"})), "summary")
    assert (summary["turns"], summary["strikes"], summary["unparsed"]) == (1, 0, 1)
    assert summary["log"][0]["verdict"] == "unparsed"


def test_truncated_fragment_is_dropped() -> None:
    """A sentence cut off by the token cap is neither spoken nor logged."""
    cut = ("neutral", "I read the letter. I've been trying to")
    assert _find(_say(_session(cut)), "reply")["text"] == "I read the letter."


def test_lone_fragment_is_kept() -> None:
    """A short reply with no full stop is better than silence."""
    short = ("neutral", "Right")
    assert _find(_say(_session(short)), "reply")["text"] == "Right"


def test_history_keeps_her_verdict_tags_when_she_judges() -> None:
    """The model sees its own earlier replies with their tag."""
    scenario = _session(_GOOD)
    list(scenario.handle_command({"type": "begin"}))
    _say(scenario, "I hear you.")
    assert [m["content"] for m in scenario._history] == [
        "[START]",
        "<neutral>Eighty dollars.",
        "I hear you.",
        "<deescalated>Thank you. That helps.",
    ]


def test_silent_audio_is_discarded() -> None:
    """Silence uses no turn and never reaches the model."""
    silence = np.zeros(16000, dtype="<f4").tobytes()
    assert _types(list(_session().handle_audio(silence))) == ["discarded"]


def test_audio_turn_sends_transcript_first() -> None:
    """Audio turns report what was heard before the reply."""
    tone = np.full(16000, 0.5, dtype="<f4").tobytes()
    assert _types(list(_session(_GOOD).handle_audio(tone)))[:2] == ["transcript", "tags"]


def test_reset_clears_counters() -> None:
    """After reset, previous strikes no longer count."""
    scenario = _session(_BAD, _BAD, _BAD)
    _say(scenario)
    _say(scenario)
    assert list(scenario.handle_command({"type": "reset"})) == [{"type": "reset_complete"}]
    assert "ending" not in _types(_say(scenario))


def test_unwinnable_rules_are_rejected() -> None:
    """Steps to resolve + allowed misses must fit within max_turns."""
    with pytest.raises(ValueError):
        dataclasses.replace(_RULES, levels=["agitated"] * 7 + ["calm"])


def test_audio_frame_never_starts_with_a_brace() -> None:
    """Unity would drop such a frame as JSON; the nudge is inaudible."""
    sample = np.frombuffer(bytes([0x7B, 0x00, 0x00, 0x3F]), dtype="<f4")  # ~0.5
    frame = session.audio_frame(sample)
    assert frame[:1] != b"{"
    assert abs(np.frombuffer(frame, dtype="<f4")[0] - sample[0]) < 1e-6


class Fixed(director.Director):
    """A director that always gives the same verdict."""

    def __init__(self, verdict: str) -> None:
        """Initialises the director.

        Args:
            verdict: The verdict for every line.
        """
        self._verdict = verdict

    def judge(self, history: list[dict[str, str]]) -> director.Judgement:
        """See base class."""
        return director.judgement(self._verdict, 0.9 if self._verdict == "deescalated" else 0.1)


def test_director_verdict_overrides_mels_tags() -> None:
    """With a director, its verdict scores the turn, whatever tag Mel's model wrote."""
    scenario = _session(_GOOD, judge=Fixed("neutral"))
    messages = _say(scenario)
    assert _find(messages, "tags")["verdict"] == "neutral"
    summary = _find(list(scenario.handle_command({"type": "summary"})), "summary")
    assert (summary["strikes"], summary["progress"]) == (1, 0)
    assert summary["log"][0]["director_p"] == 0.1


def test_director_third_strike_never_calls_mels_model() -> None:
    """A strike the director decides ends the scenario before any model is called."""
    scenario = _session(_BAD, _BAD, judge=Fixed("neutral"))  # a third call would fail
    _say(scenario)
    _say(scenario)
    messages = _say(scenario)
    assert _types(messages) == ["tags", "ending", "reply", "turn_complete", "turn_end"]
    assert _find(messages, "ending")["outcome"] == "disengaged"
    assert _find(messages, "reply")["text"] == ""


def test_director_note_reaches_mels_model_but_not_history() -> None:
    """Mel's model is told the verdict; the history keeps only what was said."""
    seen: list[director.Judgement | None] = []

    class Recording(Scripted):
        """Records the judgement each call receives."""

        def generate(self, history: list[dict[str, str]], state_note: str = "",
                     judgement: director.Judgement | None = None) -> dialogue.Reply:
            """See base class."""
            seen.append(judgement)
            return super().generate(history, state_note, judgement)

    scenario = _session(judge=Fixed("deescalated"))
    scenario._dialogue = Recording(_GOOD)  # pylint: disable=protected-access
    list(scenario.handle_command({"type": "begin"}))
    _say(scenario, "I hear you.")
    assert seen[0] and seen[0].verdict == "deescalated"
    history = scenario._history  # pylint: disable=protected-access
    assert all(seen[0].note not in m["content"] for m in history)


def test_emotion_comes_from_the_levels_not_the_model() -> None:
    """Two deescalations reach the second emotion in the levels, in either judging mode."""
    for scenario in (_session(_GOOD, _GOOD), _session(_GOOD, _GOOD, judge=Fixed("deescalated"))):
        emotions = [_find(_say(scenario), "turn_complete")["emotion"] for _ in range(2)]
        assert emotions == ["agitated", "distressed"]


def test_turn_complete_reports_the_tracker() -> None:
    """Clients show the tracker's numbers rather than counting verdicts themselves."""
    done = _find(_say(_session(_BAD)), "turn_complete")
    assert (done["level"], done["strikes"], done["progress"]) == (5, 1, 0)


def test_directed_history_has_no_tags() -> None:
    """With a director her model writes only words, so that is what it sees."""
    scenario = _session(_GOOD, judge=Fixed("deescalated"))
    list(scenario.handle_command({"type": "begin"}))
    _say(scenario, "I hear you.")
    history = scenario._history  # pylint: disable=protected-access
    assert [m["content"] for m in history if m["role"] == "assistant"] == [
        "Eighty dollars.", "Thank you. That helps."]


def test_mel_is_told_how_she_feels_after_the_directors_verdict() -> None:
    """The note matches the level the verdict left her on."""
    notes: list[str] = []

    class Recording(Scripted):
        """Records the state note each call receives."""

        def generate(self, history: list[dict[str, str]], state_note: str = "",
                     judgement: director.Judgement | None = None) -> dialogue.Reply:
            """See base class."""
            notes.append(state_note)
            return super().generate(history, state_note, judgement)

    scenario = _session(judge=Fixed("deescalated"))
    scenario._dialogue = Recording(_GOOD, _GOOD)  # pylint: disable=protected-access
    _say(scenario)
    _say(scenario)
    assert notes == [director.STATE_NOTES["agitated"], director.STATE_NOTES["distressed"]]
