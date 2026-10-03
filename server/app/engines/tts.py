"""Text-to-speech engines."""

import abc

import numpy as np

from app import config


class TextToSpeech(abc.ABC):
    """Speaks one sentence at a time, so playback can start early."""

    def warm_up(self) -> None:
        """Loads models. Called once before the server reports ready."""

    @abc.abstractmethod
    def synthesise(self, text: str) -> np.ndarray:
        """Speaks one sentence.

        Args:
            text: The sentence to speak.

        Returns:
            Mono float32 audio at the configured playback rate, with no
            leading or trailing silence (the server adds its own gaps).
            Empty if nothing was produced.
        """


class FakeTextToSpeech(TextToSpeech):
    """Beeps instead of speaking. No model needed."""

    def __init__(self, sample_rate: int = 24000) -> None:
        """Initialises the engine.

        Args:
            sample_rate: Rate to generate beeps at.
        """
        self._sample_rate = sample_rate

    def synthesise(self, text: str) -> np.ndarray:
        """See base class."""
        t = np.arange(int(self._sample_rate * 0.3)) / self._sample_rate
        return (0.2 * np.sin(2 * np.pi * 440 * t)).astype(np.float32)


_ENGINES: dict[str, type[TextToSpeech]] = {
    "fake": FakeTextToSpeech,
}


def build(settings: config.EngineSettings) -> TextToSpeech:
    """Creates the engine named in the config.

    Args:
        settings: The text_to_speech config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
