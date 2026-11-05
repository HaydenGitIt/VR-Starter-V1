"""Tests for TTS helpers that need no model."""

import numpy as np

from app.engines import tts


def test_kokoro_trims_silent_ends_but_keeps_padding() -> None:
    """Leading and trailing silence goes; 20 ms either side stays."""
    engine = tts.KokoroTextToSpeech(edge_padding_ms=20)
    speech = np.full(2400, 0.5, dtype=np.float32)
    silence = np.zeros(24000, dtype=np.float32)
    trimmed = engine._trim(np.concatenate([silence, speech, silence]))
    assert trimmed.size == 2400 + 2 * 480


def test_kokoro_leaves_all_silent_clip_alone() -> None:
    """A clip with nothing above the threshold is returned unchanged."""
    silence = np.zeros(1000, dtype=np.float32)
    assert tts.KokoroTextToSpeech()._trim(silence).size == 1000


def test_qwen_picks_the_instruction_for_the_emotion() -> None:
    """Each tag gets its own delivery instruction."""
    engine = tts.QwenTextToSpeech(instructions={"agitated": "fast", "calm": "slow", "": "plain"})
    assert engine._instruction_for("agitated") == "fast"
    assert engine._instruction_for("calm") == "slow"


def test_qwen_falls_back_for_unknown_and_missing_emotions() -> None:
    """An unrecognised tag must not raise mid-scenario; it gets the fallback.

    Mel's model writes these tags, so a typo or a tag nobody configured is
    always possible. Losing her delivery is acceptable; losing her voice is
    not.
    """
    engine = tts.QwenTextToSpeech(instructions={"agitated": "fast", "": "plain"})
    assert engine._instruction_for("furious") == "plain"
    assert engine._instruction_for("") == "plain"


def test_qwen_tolerates_instructions_without_a_fallback() -> None:
    """A config that forgot the "" entry returns no instruction, not an error."""
    engine = tts.QwenTextToSpeech(instructions={"agitated": "fast"})
    assert engine._instruction_for("calm") == ""
