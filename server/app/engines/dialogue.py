"""Dialogue engines: write Mel's line and judge the trainee's."""

import abc
import dataclasses
import pathlib
import re
from collections.abc import Generator, Iterator

import ollama

from app import config

_SERVER_DIR = pathlib.Path(__file__).resolve().parents[2]


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


class OllamaDialogue(Dialogue):
    """A local Ollama model that writes its own tags, ported from the original.

    The prompt makes every reply start "<verdict><emotion>", so the tags
    can be read from the first few tokens of the stream.
    """

    _TAGS = re.compile(
        r"^\s*<(deescalated|neutral)>\s*<(agitated|distressed|softening|calm)>",
        re.IGNORECASE)

    def __init__(
        self,
        model: str = "llama3:8b",
        prompt: str = "prompts/parent_system_prompt.txt",
        temperature: float = 0.8,
        max_tokens: int = 55,
        tag_deadline_chars: int = 60,
        host: str | None = None,
    ) -> None:
        """Reads the prompt; the model loads in warm_up().

        Args:
            model: Ollama model tag, e.g. "llama3:8b" or "qwen2.5:3b".
            prompt: System prompt file, relative to the server folder.
            temperature: Higher is more varied but follows the format less.
            max_tokens: Hard cap on reply length (and so on latency).
            tag_deadline_chars: If this much arrives with no valid tags,
                the reply is rejected rather than salvaged.
            host: Ollama URL, or None for the default (localhost:11434).
        """
        self._model = model
        self._prompt = (_SERVER_DIR / prompt).read_text(encoding="utf-8")
        self._options = {"temperature": temperature, "num_predict": max_tokens}
        self._tag_deadline_chars = tag_deadline_chars
        self._client = ollama.Client(host)

    def warm_up(self) -> None:
        """Loads the model and caches the system prompt.

        Sending the real system prompt lets Ollama keep it processed, so
        the first trainee turn is as fast as the rest (measured 1.9s to
        tags without this, 0.2s with it).
        """
        self._client.chat(
            model=self._model,
            messages=[{"role": "system", "content": self._prompt},
                      {"role": "user", "content": "Hello."}],
            options={**self._options, "num_predict": 1})

    def generate(self, history: list[dict[str, str]]) -> Reply:
        """See base class."""
        stream = self._client.chat(
            model=self._model,
            messages=[{"role": "system", "content": self._prompt}, *history],
            options=self._options,
            stream=True)
        raw = ""
        for chunk in stream:
            raw += chunk.message.content
            if match := self._TAGS.match(raw):
                return Reply(match.group(1).lower(), match.group(2).lower(),
                             _rest_of_stream(raw[match.end():], stream))
            if len(raw) > self._tag_deadline_chars:
                break
        stream.close()
        raise MalformedReply(f"No tags in output: {raw!r}")


def _rest_of_stream(
    first: str, stream: Iterator[ollama.ChatResponse]
) -> Generator[str, None, None]:
    """Streams the dialogue that follows the tags.

    Args:
        first: Dialogue that arrived in the same chunk as the tags.
        stream: The rest of the Ollama response.

    Yields:
        Pieces of Mel's line. Closing this closes the response, which
        stops Ollama generating.
    """
    try:
        if first:
            yield first
        for chunk in stream:
            if chunk.message.content:
                yield chunk.message.content
    finally:
        stream.close()


_ENGINES: dict[str, type[Dialogue]] = {
    "fake": FakeDialogue,
    "ollama": OllamaDialogue,
}


def build(settings: config.EngineSettings) -> Dialogue:
    """Creates the engine named in the config.

    Args:
        settings: The dialogue config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
