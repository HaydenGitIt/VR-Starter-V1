"""Dialogue engines: write the parent's line and judge the trainee's."""

import abc
import dataclasses

from app import config


@dataclasses.dataclass(frozen=True)
class Reply:
    """One parent response.

    Attributes:
        verdict: "deescalated", "neutral", or "unknown" if it could not be
            parsed (an unknown verdict is never counted against the trainee).
        emotion: "agitated", "distressed", "softening" or "calm".
        text: The parent's spoken line.
    """

    verdict: str
    emotion: str
    text: str


class Dialogue(abc.ABC):
    """Generates the parent's reply to the conversation so far."""

    def warm_up(self) -> None:
        """Loads models. Called once before the server reports ready."""

    @abc.abstractmethod
    def reply(self, history: list[dict[str, str]]) -> Reply:
        """Responds to the trainee's latest line.

        Args:
            history: Chat messages, oldest first, each with "role" ("user"
                for the trainee, "assistant" for the parent) and "content".
                The last message is the trainee's.

        Returns:
            The parent's reply.
        """


class FakeDialogue(Dialogue):
    """Rewards lines containing an empathetic keyword. No model needed."""

    _KEYWORDS = ("sorry", "understand", "hear you")

    def reply(self, history: list[dict[str, str]]) -> Reply:
        """See base class."""
        line = history[-1]["content"]
        if any(word in line.lower() for word in self._KEYWORDS):
            return Reply("deescalated", "softening", "Okay. Thank you for saying that.")
        return Reply("neutral", "agitated", f'"{line}" That doesn\'t help.')


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
