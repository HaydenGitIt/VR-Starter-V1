"""Tests for config loading and command-line overrides."""

import pytest

from app import config


def test_override_replaces_a_nested_value() -> None:
    """--set reaches into sections and parses the value as YAML."""
    settings = config.load(["dialogue.ollama.model=tiny:1b", "scenario.max_turns=10"])
    assert settings.dialogue.options["model"] == "tiny:1b"
    assert settings.scenario.max_turns == 10


def test_switching_engine_uses_only_its_own_options() -> None:
    """A different engine never receives another engine's options."""
    settings = config.load(["dialogue.engine=fake"])
    assert (settings.dialogue.engine, settings.dialogue.options) == ("fake", {})


def test_override_needs_an_equals_sign() -> None:
    """A malformed override is rejected rather than ignored."""
    with pytest.raises(ValueError):
        config.load(["dialogue.model"])
