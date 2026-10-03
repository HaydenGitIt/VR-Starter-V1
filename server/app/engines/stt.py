"""Speech-to-text engines."""

import abc

import numpy as np

from app import config


class SpeechToText(abc.ABC):
    """Turns one complete utterance into text."""

    def warm_up(self) -> None:
        """Loads models. Called once before the server reports ready."""

    @abc.abstractmethod
    def transcribe(self, samples: np.ndarray, sample_rate: int) -> str:
        """Transcribes an utterance.

        Args:
            samples: Mono float32 audio in the range [-1, 1].
            sample_rate: Samples per second.

        Returns:
            What was said, or "" if nothing intelligible was heard.
        """


class FakeSpeechToText(SpeechToText):
    """Pretends to hear something whenever the audio is not silent."""

    def transcribe(self, samples: np.ndarray, sample_rate: int) -> str:
        """See base class."""
        if samples.size == 0 or np.abs(samples).max() < 0.01:
            return ""
        return f"(fake transcript of {samples.size / sample_rate:.1f}s of audio)"


_ENGINES: dict[str, type[SpeechToText]] = {
    "fake": FakeSpeechToText,
}


def build(settings: config.EngineSettings) -> SpeechToText:
    """Creates the engine named in the config.

    Args:
        settings: The speech_to_text config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
