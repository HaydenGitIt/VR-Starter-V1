"""Loads config.yaml, with config.local.yaml merged over it if present."""

import dataclasses
import pathlib
from typing import Any

import yaml

_SERVER_DIR = pathlib.Path(__file__).resolve().parent.parent
_BASE_FILE = _SERVER_DIR / "config.yaml"
_LOCAL_FILE = _SERVER_DIR / "config.local.yaml"


@dataclasses.dataclass(frozen=True)
class EngineSettings:
    """One engine config section.

    Attributes:
        engine: Registry name of the engine, e.g. "fake".
        options: The rest of the section, passed to the engine's constructor.
    """

    engine: str
    options: dict[str, Any]


@dataclasses.dataclass(frozen=True)
class Settings:
    """Typed view of the config file.

    Attributes:
        host: Address the server binds to.
        port: Port the server listens on.
        capture_sample_rate: Rate of incoming trainee audio, in Hz.
        playback_sample_rate: Rate of outgoing synthesised audio, in Hz.
        max_turns: Trainee turns before the scenario ends.
        strikes_allowed: Turns that fail to reach her before she leaves.
        progress_to_resolve: Turns that reach her before it resolves.
        opening_line: What the parent says when the scenario begins.
        speech_to_text: Which STT engine to use.
        dialogue: Which dialogue engine to use.
        text_to_speech: Which TTS engine to use.
    """

    host: str
    port: int
    capture_sample_rate: int
    playback_sample_rate: int
    max_turns: int
    strikes_allowed: int
    progress_to_resolve: int
    opening_line: str
    speech_to_text: EngineSettings
    dialogue: EngineSettings
    text_to_speech: EngineSettings


def load() -> Settings:
    """Reads the config files into a Settings object.

    Returns:
        The merged settings.
    """
    raw = _read(_BASE_FILE)
    if _LOCAL_FILE.exists():
        raw = _merge(raw, _read(_LOCAL_FILE))

    return Settings(
        host=raw["server"]["host"],
        port=raw["server"]["port"],
        capture_sample_rate=raw["audio"]["capture_sample_rate"],
        playback_sample_rate=raw["audio"]["playback_sample_rate"],
        max_turns=raw["scenario"]["max_turns"],
        strikes_allowed=raw["scenario"]["strikes_allowed"],
        progress_to_resolve=raw["scenario"]["progress_to_resolve"],
        opening_line=raw["scenario"]["opening_line"],
        speech_to_text=_engine(raw["speech_to_text"]),
        dialogue=_engine(raw["dialogue"]),
        text_to_speech=_engine(raw["text_to_speech"]),
    )


def _engine(section: dict[str, Any]) -> EngineSettings:
    """Splits an engine section into its name and constructor options.

    Args:
        section: A config section with an "engine" key.

    Returns:
        The engine settings.
    """
    options = dict(section)
    return EngineSettings(engine=options.pop("engine"), options=options)


def _read(path: pathlib.Path) -> dict[str, Any]:
    """Parses one YAML file.

    Args:
        path: File to read.

    Returns:
        The file contents, or an empty dict if the file is empty.
    """
    with path.open(encoding="utf-8") as f:
        return yaml.safe_load(f) or {}


def _merge(base: dict[str, Any], override: dict[str, Any]) -> dict[str, Any]:
    """Recursively merges override into base without modifying either.

    Args:
        base: The default values.
        override: Values that replace those in base, section by section.

    Returns:
        A new dict with override's values taking precedence.
    """
    merged = dict(base)
    for key, value in override.items():
        if isinstance(value, dict) and isinstance(merged.get(key), dict):
            merged[key] = _merge(merged[key], value)
        else:
            merged[key] = value
    return merged
