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
