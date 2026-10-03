"""Dialogue engines: write Mel's line and judge the trainee's."""

import abc
import dataclasses
import re
from collections.abc import Generator

from app import config


class MalformedReply(Exception):
    """The model did not produce valid tags, so the reply cannot be trusted."""


@dataclasses.dataclass(frozen=True)
class Reply:
    """One of Mel's replies, with tags known before any text exists.

    Attributes:
        verdict: "deescalated" (they reached her) or "neutral" (they did not).
        emotion: "agitated", "distressed", "softening" or "calm".
        text: Mel's spoken line, streamed in pieces as it is generated.
            Close it to stop generation early.
    """

    verdict: str
    emotion: str
    text: Generator[str, None, None]


class Dialogue(abc.ABC):
    """Generates Mel's reply to the conversation so far."""

    def warm_up(self) -> None:
        """Loads models. Called once before the server reports ready."""

    @abc.abstractmethod
    def generate(self, history: list[dict[str, str]]) -> Reply:
        """Starts Mel's reply to the trainee's latest line.

        Args:
            history: Chat messages, oldest first, each with "role" ("user"
                for the trainee, "assistant" for Mel) and "content". Mel's
                messages include their tags, e.g. "<neutral><agitated>...".

        Returns:
            The reply, once its tags are known.

        Raises:
            MalformedReply: If valid tags do not arrive.
        """


class FakeDialogue(Dialogue):
    """Rewards lines containing an empathetic phrase. No model needed."""

    _PHRASES = ("sorry", "hear you", "listening")

    def generate(self, history: list[dict[str, str]]) -> Reply:
        """See base class."""
        line = history[-1]["content"]
        if any(phrase in line.lower() for phrase in self._PHRASES):
            return Reply("deescalated", "softening", _words("Okay. Thank you for saying that."))
        return Reply("neutral", "agitated", _words(f'"{line}" That doesn\'t help.'))


def _words(text: str) -> Generator[str, None, None]:
    """Streams text a word at a time, the way a model would.

    Args:
        text: The full line.

    Yields:
        The line in word-sized pieces.
    """
    yield from re.findall(r"\S+\s*", text)


_ENGINES: dict[str, type[Dialogue]] = {
    "fake": FakeDialogue,
}


def build(settings: config.EngineSettings) -> Dialogue:
    """Creates the engine named in the config.

    Args:
        settings: The dialogue config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
