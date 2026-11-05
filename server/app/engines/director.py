"""Director engines: decide whether the trainee's latest line reached Mel.

With a director, Mel's dialogue model no longer judges the trainee itself: the director
decides the verdict, the session scores it, and the dialogue model is told the verdict and
only acts it out. "none" keeps the original design, where Mel's own tags decide.

The questions (prompts/director.yaml) and the state built here are exactly what datagen
exports for training, so a trained model sees in the game what it saw in training.
"""

import abc
import dataclasses
import json
import pathlib
import re
from typing import Any

import yaml

from app import config

_SERVER_DIR = pathlib.Path(__file__).resolve().parents[2]
_TAGS = re.compile(r"^\s*<[^>]*>\s*<[^>]*>")
_START_TOKEN = re.compile(r"^\[[A-Z_]+\]$")  # e.g. [SCENARIO_START], the scenario's first message

# The questions Laya is asked and the notes Mel gets, editable in prompts/director.yaml.
_PROMPTS = yaml.safe_load((_SERVER_DIR / "prompts" / "director.yaml").read_text(encoding="utf-8"))
QUESTIONS: dict[str, Any] = _PROMPTS["questions"]
NOTES: dict[str, str] = _PROMPTS["notes_to_mel"]


@dataclasses.dataclass(frozen=True)
class Judgement:
    """The director's decision on the trainee's latest line.

    Attributes:
        verdict: "deescalated" (it reached her) or "neutral" (it did not).
        probability: The director's P(the line reached her).
        note: One line telling Mel's dialogue model how to react.
    """

    verdict: str
    probability: float
    note: str


def state(parent_said: str, staff_previously_said: str, staff_now_says: str) -> dict[str, str]:
    """Builds what the director reads: Mel's last line and the staff member's last two.

    Args:
        parent_said: Mel's last spoken line, without tags.
        staff_previously_said: The staff member's line before this one, or "".
        staff_now_says: The line being judged, as transcribed.

    Returns:
        The director's input.
    """
    return {"parent_said": parent_said, "staff_previously_said": staff_previously_said,
            "staff_now_says": staff_now_says}


def state_from_history(history: list[dict[str, str]]) -> dict[str, str]:
    """Builds the director's input from the session's history.

    Args:
        history: Chat messages ending with the trainee's latest line. Mel's are tagged, and
            the first user message is the scenario start token.

    Returns:
        The director's input, as state() builds it.
    """
    mel = [m["content"] for m in history if m["role"] == "assistant"]
    staff = [m["content"] for m in history
             if m["role"] == "user" and not _START_TOKEN.match(m["content"])]
    return state(_TAGS.sub("", mel[-1]) if mel else "",
                 staff[-2] if len(staff) > 1 else "", staff[-1])


def judgement(verdict: str, probability: float) -> Judgement:
    """Wraps a verdict with its note for Mel.

    Args:
        verdict: "deescalated" or "neutral".
        probability: P(the line reached her).

    Returns:
        The judgement.
    """
    return Judgement(verdict, probability, NOTES[verdict])


class Director(abc.ABC):
    """Decides whether the trainee's latest line reached Mel."""

    def warm_up(self) -> None:
        """Loads models. Called once before the server reports ready."""

    @abc.abstractmethod
    def judge(self, history: list[dict[str, str]]) -> Judgement | None:
        """Judges the trainee's latest line.

        Args:
            history: The conversation so far; the last message is the trainee's.

        Returns:
            The judgement, or None to let Mel's dialogue model judge from her own tags.
        """


class NoDirector(Director):
    """The original design: Mel's own tags decide."""

    def judge(self, history: list[dict[str, str]]) -> Judgement | None:
        """See base class."""
        return None


class FakeDirector(Director):
    """Rewards lines containing an empathetic phrase. No model needed."""

    _PHRASES = ("sorry", "hear you", "listening", "frustrat")

    def judge(self, history: list[dict[str, str]]) -> Judgement | None:
        """See base class."""
        reached = any(p in history[-1]["content"].lower() for p in self._PHRASES)
        return judgement("deescalated" if reached else "neutral", 1.0 if reached else 0.0)


class LayaDirector(Director):
    """A Laya checkpoint fine-tuned with `python -m datagen.train_director`."""

    def __init__(self, model: str = "models/director", device: str | None = None,
                 threshold: float = 0.5, repeated_threshold: float = 0.5) -> None:
        """Stores settings; the model loads in warm_up().

        Args:
            model: Checkpoint folder, relative to the server folder (or a Laya model name).
            device: "cuda", "cpu", or None to use the GPU when available.
            threshold: P(reached her) needed for "deescalated".
            repeated_threshold: P(repeated) at or above which the line is "neutral" anyway.
        """
        path = _SERVER_DIR / model
        self._model = str(path) if path.exists() else model
        self._device = device
        self._threshold = threshold
        self._repeated_threshold = repeated_threshold
        self._agent: Any = None

    def warm_up(self) -> None:
        """See base class."""
        import laya  # pylint: disable=import-outside-toplevel  (only when this engine is used)

        trained_on = pathlib.Path(self._model) / "questions.json"
        if trained_on.exists() and json.loads(trained_on.read_text(encoding="utf-8")) != QUESTIONS:
            print("WARNING: the director was trained on different questions than "
                  "prompts/director.yaml has now, so its verdicts are unreliable. Retrain it: "
                  "python -m datagen.export, then python -m datagen.train_director.", flush=True)
        self._agent = laya.load(self._model, device=self._device)
        self._agent.predict(state("Hello.", "", "Hello."), QUESTIONS)  # first call is slow

    def judge(self, history: list[dict[str, str]]) -> Judgement | None:
        """See base class."""
        return self.judge_state(state_from_history(history))

    def judge_state(self, director_state: dict[str, str]) -> Judgement:
        """Judges a line from the director's input directly, e.g. an exported row's state.

        Args:
            director_state: As state() builds it.

        Returns:
            The judgement.
        """
        answers = self._agent.predict(director_state, QUESTIONS)["answers"]
        p = answers["reached_her"]["noul"]
        reached = p >= self._threshold and answers["repeated"]["noul"] < self._repeated_threshold
        return judgement("deescalated" if reached else "neutral", p)


_ENGINES: dict[str, type[Director]] = {
    "none": NoDirector,
    "fake": FakeDirector,
    "laya": LayaDirector,
}


def build(settings: config.EngineSettings) -> Director:
    """Creates the engine named in the config.

    Args:
        settings: The director config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
