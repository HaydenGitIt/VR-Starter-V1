"""Speech-to-text engines."""

import abc
import os
import pathlib

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


class WhisperSpeechToText(SpeechToText):
    """faster-whisper, with the settings proven in Assets/Scripts/Test STT."""

    def __init__(
        self,
        model: str = "base.en",
        device: str = "cuda",
        compute_type: str = "float16",
        beam_size: int = 5,
        no_speech_threshold: float = 0.6,
    ) -> None:
        """Stores settings; the model loads in warm_up().

        Args:
            model: Whisper model size, e.g. "base.en" or "small.en".
            device: "cuda" or "cpu".
            compute_type: "float16" on GPU, "int8" on CPU.
            beam_size: Alternatives weighed per step; higher is slower.
            no_speech_threshold: Segments Whisper rates likelier than this
                to be non-speech are dropped. Whisper invents words (often
                "You") for silence and noise; those score ~0.8, speech ~0.
        """
        self._model_name = model
        self._device = device
        self._compute_type = compute_type
        self._beam_size = beam_size
        self._no_speech_threshold = no_speech_threshold
        self._model = None

    def warm_up(self) -> None:
        """See base class."""
        _add_nvidia_dlls_to_path()
        # Imported here so fake-only setups never load CUDA libraries.
        import faster_whisper  # pylint: disable=import-outside-toplevel

        self._model = faster_whisper.WhisperModel(
            self._model_name, device=self._device, compute_type=self._compute_type)
        # The first transcription pays CUDA's start-up cost; pay it now.
        self.transcribe(np.zeros(16000, dtype=np.float32), 16000)

    def transcribe(self, samples: np.ndarray, sample_rate: int) -> str:
        """See base class."""
        if sample_rate != 16000:
            raise ValueError(f"Whisper needs 16000 Hz audio, got {sample_rate} Hz.")
        # Unity's VAD has already trimmed silence, so Whisper's own VAD is off.
        segments, _ = self._model.transcribe(
            samples, beam_size=self._beam_size, vad_filter=False)
        return " ".join(
            segment.text.strip() for segment in segments
            if segment.no_speech_prob <= self._no_speech_threshold)


def _add_nvidia_dlls_to_path() -> None:
    """Lets Windows find CUDA DLLs from the nvidia-*-cu12 pip packages.

    Those packages put DLLs in site-packages/nvidia/*/bin, which is not on
    the DLL search path. Without this, CUDA fails at the first transcription
    with "cublas64_12.dll is not found". Does nothing if they are absent.
    """
    try:
        import nvidia  # pylint: disable=import-outside-toplevel
    except ImportError:
        return
    for package_dir in nvidia.__path__:
        for bin_dir in pathlib.Path(package_dir).glob("*/bin"):
            os.environ["PATH"] = f"{bin_dir}{os.pathsep}{os.environ['PATH']}"


_ENGINES: dict[str, type[SpeechToText]] = {
    "fake": FakeSpeechToText,
    "whisper": WhisperSpeechToText,
}


def build(settings: config.EngineSettings) -> SpeechToText:
    """Creates the engine named in the config.

    Args:
        settings: The speech_to_text config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
