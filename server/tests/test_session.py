"""Tests for the scenario rules and message order, using fake engines."""

import numpy as np

from app import config, session
from app.engines import dialogue, stt, tts

_FAKE = config.EngineSettings(engine="fake", options={})
_GOOD = "I'm sorry, I understand."
_BAD = "Please calm down."


def _settings(max_turns: int = 8) -> config.Settings:
    """Builds settings independent of config files on disk.

    Args:
        max_turns: Turn limit for the scenario.

    Returns:
        Settings using fake engines and the default rules.
    """
    return config.Settings(
        host="127.0.0.1", port=8000,
        capture_sample_rate=16000, playback_sample_rate=24000,
        max_turns=max_turns, strikes_allowed=3, progress_to_resolve=4,
        opening_line="Hello.",
        speech_to_text=_FAKE, dialogue=_FAKE, text_to_speech=_FAKE,
    )


def _session(
    max_turns: int = 8, dialogue_engine: dialogue.Dialogue | None = None
) -> session.ScenarioSession:
    """Creates a session with fake engines.

    Args:
        max_turns: Turn limit for the scenario.
        dialogue_engine: Overrides the fake dialogue engine if given.

    Returns:
        A new session.
    """
    return session.ScenarioSession(
        _settings(max_turns),
        stt.FakeSpeechToText(),
        dialogue_engine or dialogue.FakeDialogue(),
        tts.FakeTextToSpeech(),
    )


def _types(messages: list[session.Message]) -> list[str]:
    """Summarises messages, with each run of audio frames shown as "audio".

    The number of frames per line depends on the TTS engine, so runs are
    collapsed to keep the tests about order rather than sentence counts.

    Args:
        messages: Handler output.

    Returns:
        The message types in order.
    """
    types = ["audio" if isinstance(m, bytes) else m["type"] for m in messages]
    return [t for i, t in enumerate(types) if t != "audio" or types[i - 1:i] != ["audio"]]


def _say(scenario: session.ScenarioSession, line: str) -> list[session.Message]:
    """Sends a typed line.

    Args:
        scenario: The session.
        line: The trainee's line.

    Returns:
        Everything the session sent back.
    """
    return list(scenario.handle_command({"type": "text", "content": line}))


def test_begin_speaks_opening_line() -> None:
    """The opening line is sent as text, then audio, then turn_end."""
    messages = list(_session().handle_command({"type": "begin"}))
    assert _types(messages) == ["reply", "audio", "turn_end"]
    assert messages[-1]["ended"] is False


def test_turn_sends_tags_before_reply_and_audio() -> None:
    """A normal turn follows the documented order."""
    scenario = _session()
    assert _types(_say(scenario, _BAD)) == [
        "tags", "reply", "turn_complete", "audio", "turn_end"]


def test_third_strike_disengages_without_audio() -> None:
    """Three neutral turns end the scenario, and the last turn is silent."""
    scenario = _session()
    _say(scenario, _BAD)
    _say(scenario, _BAD)
    messages = _say(scenario, _BAD)
    assert _types(messages) == ["tags", "reply", "turn_complete", "ending", "turn_end"]
    assert messages[3]["outcome"] == "disengaged"
    assert messages[-1]["ended"] is True


def test_enough_progress_resolves() -> None:
    """Four deescalated turns resolve the scenario."""
    scenario = _session()
    for _ in range(3):
        _say(scenario, _GOOD)
    assert _say(scenario, _GOOD)[3] == {"type": "ending", "outcome": "resolved"}


def test_running_out_of_turns_disengages() -> None:
    """Hitting max_turns with neither threshold reached disengages."""
    scenario = _session(max_turns=2)
    _say(scenario, _GOOD)
    assert _say(scenario, _BAD)[3] == {"type": "ending", "outcome": "disengaged"}


def test_unknown_verdict_is_not_a_strike() -> None:
    """Unparseable verdicts never count against the trainee."""

    class Unparseable(dialogue.Dialogue):
        def reply(self, history: list[dict[str, str]]) -> dialogue.Reply:
            """Always returns an unknown verdict."""
            return dialogue.Reply("unknown", "agitated", "Hm.")

    scenario = _session(dialogue_engine=Unparseable())
    for _ in range(5):
        assert "ending" not in _types(_say(scenario, _BAD))


def test_silent_audio_is_discarded() -> None:
    """Silence uses no turn."""
    silence = np.zeros(16000, dtype="<f4").tobytes()
    messages = list(_session().handle_audio(silence))
    assert _types(messages) == ["discarded"]


def test_audio_turn_sends_transcript_first() -> None:
    """Audio turns report what was heard before the reply."""
    tone = np.full(16000, 0.5, dtype="<f4").tobytes()
    assert _types(list(_session().handle_audio(tone)))[:2] == ["transcript", "tags"]


def test_reset_clears_counters() -> None:
    """After reset, previous strikes no longer count."""
    scenario = _session()
    _say(scenario, _BAD)
    _say(scenario, _BAD)
    assert list(scenario.handle_command({"type": "reset"})) == [
        {"type": "reset_complete"}]
    assert "ending" not in _types(_say(scenario, _BAD))
