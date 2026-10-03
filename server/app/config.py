"""Loads config.yaml, with config.local.yaml merged over it if present."""

import dataclasses
import pathlib
from typing import Any

import yaml

_SERVER_DIR = pathlib.Path(__file__).resolve().parent.parent
_BASE_FILE = _SERVER_DIR / "config.yaml"
_LOCAL_FILE = _SERVER_DIR / "config.local.yaml"


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
    """

    host: str
    port: int
    capture_sample_rate: int
    playback_sample_rate: int
    max_turns: int
    strikes_allowed: int
    progress_to_resolve: int


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
    )


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
