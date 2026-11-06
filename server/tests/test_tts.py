"""Tests for TTS helpers that need no model."""

import sys
import types

import numpy as np
import pytest

from app import config
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


def test_strip_module_prefix_unwraps_dataparallel_names() -> None:
    """Voxi's checkpoint names every parameter "module.<name>".

    Without this, every key is unexpected, Kokoro's loader falls back to
    strict=False, nothing loads, and the model stays randomly initialised
    while still producing audio of a plausible length. Silence that looks
    like success is the worst failure mode there is, so this is tested.
    """
    saved = {
        "module.embeddings.word_embeddings.weight": 1,
        "module.cnn.0.0.bias": 2,
    }
    assert tts._strip_module_prefix(saved) == {
        "embeddings.word_embeddings.weight": 1,
        "cnn.0.0.bias": 2,
    }


def test_strip_module_prefix_leaves_clean_names_alone() -> None:
    """A checkpoint saved without the wrapper passes through unchanged."""
    clean = {"embeddings.weight": 1, "decode.0.conv1.bias": 2}
    assert tts._strip_module_prefix(clean) == clean


def test_strip_module_prefix_only_strips_the_leading_occurrence() -> None:
    """"module" inside a name is part of the name, not a prefix to remove."""
    assert tts._strip_module_prefix({"module.submodule.module.weight": 1}) == {
        "submodule.module.weight": 1}


def test_legacy_weight_norm_renames_both_parametrisation_halves() -> None:
    """Voxi uses torch's newer weight-norm spelling; Kokoro expects the older.

    original0 is the magnitude and original1 the direction, the same two
    tensors weight_g and weight_v hold, so this is purely a rename.
    """
    saved = {
        "F0.0.conv1.parametrizations.weight.original0": 1,
        "F0.0.conv1.parametrizations.weight.original1": 2,
        "F0.0.conv1.bias": 3,
    }
    assert tts._to_legacy_weight_norm(saved) == {
        "F0.0.conv1.weight_g": 1,
        "F0.0.conv1.weight_v": 2,
        "F0.0.conv1.bias": 3,
    }


def test_legacy_weight_norm_leaves_old_style_names_alone() -> None:
    """A checkpoint already in the old spelling passes through unchanged."""
    old = {"F0.0.conv1.weight_g": 1, "F0.0.conv1.weight_v": 2}
    assert tts._to_legacy_weight_norm(old) == old


def test_legacy_weight_norm_only_rewrites_the_suffix() -> None:
    """The rename anchors to the end, so similar text mid-key is untouched."""
    assert tts._to_legacy_weight_norm(
        {"parametrizations.weight.original0.inner.bias": 1}) == {
            "parametrizations.weight.original0.inner.bias": 1}


def test_voxi_picks_a_voicepack_per_emotion_and_falls_back() -> None:
    """Each tag maps to a pack; unknown and empty tags get the fallback.

    Mel's model writes these tags, so an unconfigured one is always
    possible. Losing her delivery for a turn is acceptable; raising
    mid-scenario is not.
    """
    engine = tts.VoxiTextToSpeech(
        voicepacks={"agitated": "bf_angry", "calm": "bf_neutral", "": "bf_neutral"})
    engine._packs = {"bf_angry": "ANGRY", "bf_neutral": "NEUTRAL"}

    assert engine._pack_for("agitated") == "ANGRY"
    assert engine._pack_for("calm") == "NEUTRAL"
    assert engine._pack_for("furious") == "NEUTRAL"
    assert engine._pack_for("") == "NEUTRAL"


def test_voxi_defaults_cover_every_emotion_the_scenario_produces() -> None:
    """The shipped mapping has an entry for each of Mel's states."""
    for tag in ("agitated", "distressed", "softening", "calm", ""):
        assert tag in tts.VOXI_VOICEPACKS, tag
        assert tts.VOXI_VOICEPACKS[tag].startswith("bf_")


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


def test_faster_qwen_behaves_like_qwen_apart_from_loading() -> None:
    """The CUDA-graph engine differs only in how the model is loaded.

    Worth asserting: the two are meant to be a fair A/B, so any divergence
    in instruction handling or trimming would make their measurements
    incomparable without it being obvious why.
    """
    options = {"instructions": {"agitated": "fast", "": "plain"}}
    faster = tts.FasterQwenTextToSpeech(**options)

    assert isinstance(faster, tts.QwenTextToSpeech)
    assert faster._instruction_for("agitated") == "fast"
    assert faster._instruction_for("unknown") == "plain"

    # Compare the class attributes, not a bound method: an unoverridden
    # method is literally the same function object on both classes.
    assert tts.FasterQwenTextToSpeech.synthesise is tts.QwenTextToSpeech.synthesise
    assert tts.FasterQwenTextToSpeech._generate is tts.QwenTextToSpeech._generate


def test_faster_qwen_temperature_reaches_both_halves_of_the_model(
        monkeypatch: pytest.MonkeyPatch) -> None:
    """The call and the predictor's CUDA graph each need it, and the graph first.

    Args:
        monkeypatch: Replaces the faster_qwen3_tts package.
    """
    seen: dict[str, float] = {}

    class Model:
        """Records the temperatures in force at the first synthesis."""

        predictor_graph = types.SimpleNamespace(temperature=0.9)

        def generate_custom_voice(self, temperature: float, **_: object) -> tuple[list, int]:
            """Returns one sample of audio."""
            seen.update(call=temperature, graph=self.predictor_graph.temperature)
            return [np.zeros(1, dtype=np.float32)], 24000

    package = types.SimpleNamespace(
        FasterQwen3TTS=types.SimpleNamespace(from_pretrained=lambda *_, **__: Model()))
    monkeypatch.setitem(sys.modules, "faster_qwen3_tts", package)
    tts.FasterQwenTextToSpeech(temperature=0.4).warm_up()
    assert seen == {"call": 0.4, "graph": 0.4}


def test_faster_qwen_temperature_is_read_from_the_config() -> None:
    """The config value reaches the engine."""
    settings = config.load(["text_to_speech.engine=faster-qwen",
                            "text_to_speech.faster-qwen.temperature=0.4"])
    assert tts.build(settings.text_to_speech)._temperature == 0.4  # pylint: disable=protected-access
