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


class KokoroTextToSpeech(TextToSpeech):
    """Kokoro-82M, ported from the original server. Always outputs 24 kHz.

    Kokoro has no emotion control, so this is the engine most likely to be
    replaced; nothing outside this class knows it exists.
    """

    SAMPLE_RATE = 24000

    def __init__(
        self,
        voice: str = "af_heart",
        speed: float = 1.0,
        device: str | None = None,
        silence_threshold: float = 0.01,
        edge_padding_ms: int = 20,
    ) -> None:
        """Stores settings; the model loads in warm_up().

        Args:
            voice: Kokoro voice. The prefix sets the accent: "af_"/"am_"
                American, "bf_"/"bm_" British.
            speed: Speaking rate; 1.0 is normal.
            device: "cuda", "cpu", or None to use the GPU when available.
            silence_threshold: Amplitude below which audio counts as silence.
            edge_padding_ms: Silence kept at each end so nothing clips.
        """
        self._voice = voice
        self._speed = speed
        self._device = device
        self._silence_threshold = silence_threshold
        self._padding = edge_padding_ms * self.SAMPLE_RATE // 1000
        self._pipeline = None

    def warm_up(self) -> None:
        """See base class."""
        # Imported here so fake-only setups never load PyTorch.
        import kokoro  # pylint: disable=import-outside-toplevel

        # The voice prefix's first letter is Kokoro's language code.
        self._pipeline = kokoro.KPipeline(
            lang_code=self._voice[0], repo_id="hexgrad/Kokoro-82M", device=self._device)
        self.synthesise("Warming up.")

    def synthesise(self, text: str) -> np.ndarray:
        """See base class."""
        pieces = [
            np.asarray(audio.detach().cpu() if hasattr(audio, "detach") else audio,
                       dtype=np.float32)
            for _, _, audio in self._pipeline(text, voice=self._voice, speed=self._speed)
            if audio is not None
        ]
        if not pieces:
            return np.zeros(0, dtype=np.float32)
        return self._trim(np.concatenate(pieces))

    def _trim(self, audio: np.ndarray) -> np.ndarray:
        """Trims Kokoro's leading and trailing silence, keeping a little.

        Kokoro pads every clip, so joined sentences would get double-length
        pauses. The server adds one controlled gap between sentences instead.

        Args:
            audio: One synthesised clip.

        Returns:
            The clip without its silent ends (unchanged if entirely silent).
        """
        loud = np.flatnonzero(np.abs(audio) > self._silence_threshold)
        if loud.size == 0:
            return audio
        return audio[max(0, loud[0] - self._padding):loud[-1] + 1 + self._padding]


_ENGINES: dict[str, type[TextToSpeech]] = {
    "fake": FakeTextToSpeech,
    "kokoro": KokoroTextToSpeech,
}


def build(settings: config.EngineSettings) -> TextToSpeech:
    """Creates the engine named in the config.

    Args:
        settings: The text_to_speech config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
