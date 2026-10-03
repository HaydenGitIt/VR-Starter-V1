"""Tests for the scenario rules and message order, using fake engines."""

import dataclasses

import numpy as np
import pytest

from app import config, session
from app.engines import dialogue, stt, tts

_FAKE = config.EngineSettings(engine="fake", options={})
_RULES = config.Scenario(
    max_turns=8, strikes_allowed=3, progress_to_resolve=6,
    speak_reply_on_failure=False, speak_reply_on_success=True,
    start_token="[START]",
    opening_line="Eighty dollars.", opening_verdict="neutral", opening_emotion="agitated",
    fallback_line="Sorry, what was that?", fallback_verdict="neutral",
    fallback_emotion="agitated",
    max_retries=1, drop_truncated_fragment=True,
)
_GOOD = ("deescalated", "agitated", "Thank you. That helps.")
_BAD = ("neutral", "agitated", "That isn't what I asked.")


class Scripted(dialogue.Dialogue):
    """Replies from a script. None in the script means a malformed reply."""

    def __init__(self, *script: tuple[str, str, str] | None) -> None:
        """Initialises the script.

        Args:
            *script: (verdict, emotion, text) per call, or None to fail.
        """
        self._script = list(script)

    def generate(self, history: list[dict[str, str]]) -> dialogue.Reply:
        """Returns the next scripted reply, streamed a character at a time."""
        entry = self._script.pop(0)
        if entry is None:
            raise dialogue.MalformedReply("no tags")
        return dialogue.Reply(entry[0], entry[1], (c for c in entry[2]))


def _session(*script: tuple[str, str, str] | None, **rules: object) -> session.ScenarioSession:
    """Creates a session with fake engines and a scripted Mel.

    Args:
        *script: Mel's replies, in order.
        **rules: Overrides for the default scenario rules.

    Returns:
        A new session.
    """
    settings = config.Settings(
        host="127.0.0.1", port=8000,
        capture_sample_rate=16000, playback_sample_rate=24000, sentence_gap_ms=90,
        scenario=dataclasses.replace(_RULES, **rules),
        speech_to_text=_FAKE, dialogue=_FAKE, text_to_speech=_FAKE,
    )
    return session.ScenarioSession(
        settings, stt.FakeSpeechToText(), Scripted(*script), tts.FakeTextToSpeech())


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
    scenario = _session(*[_GOOD] * 6)
    for _ in range(5):
        _say(scenario)
    assert _types(_say(scenario))[:3] == ["tags", "ending", "audio"]


def test_out_of_turns_disengages_after_speaking() -> None:
    """Running out of turns ends after the last line has been spoken.

    Valid rules mean real verdicts always hit a threshold within max_turns,
    so only fallback turns (which score nothing) can run the clock out.
    """
    scenario = _session(_GOOD, _BAD, None, None,
                        max_turns=3, progress_to_resolve=2, strikes_allowed=2)
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
    cut = ("neutral", "agitated", "I read the letter. I've been trying to")
    assert _find(_say(_session(cut)), "reply")["text"] == "I read the letter."


def test_lone_fragment_is_kept() -> None:
    """A short reply with no full stop is better than silence."""
    short = ("neutral", "agitated", "Right")
    assert _find(_say(_session(short)), "reply")["text"] == "Right"


def test_history_keeps_tags() -> None:
    """The model sees its own earlier replies with their tags."""
    scenario = _session(_GOOD)
    list(scenario.handle_command({"type": "begin"}))
    _say(scenario, "I hear you.")
    assert [m["content"] for m in scenario._history] == [
        "[START]",
        "<neutral><agitated>Eighty dollars.",
        "I hear you.",
        "<deescalated><agitated>Thank you. That helps.",
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
    """progress + allowed misses must fit within max_turns."""
    with pytest.raises(ValueError):
        dataclasses.replace(_RULES, progress_to_resolve=7)
