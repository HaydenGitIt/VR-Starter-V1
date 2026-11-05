"""Text-to-speech engines."""

import abc

import numpy as np

from app import config


class TextToSpeech(abc.ABC):
    """Speaks one sentence at a time, so playback can start early."""

    def warm_up(self) -> None:
        """Loads models. Called once before the server reports ready."""

    @abc.abstractmethod
    def synthesise(self, text: str, emotion: str = "") -> np.ndarray:
        """Speaks one sentence.

        Args:
            text: The sentence to speak.
            emotion: Mel's current emotion tag, e.g. "agitated". Engines
                that cannot vary their delivery ignore it. Empty when the
                caller has none, which must never be an error.

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

    def synthesise(self, text: str, emotion: str = "") -> np.ndarray:
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
            lang_code=self._voice[0], repo_id=_KOKORO_REPO, device=self._device)
        self.synthesise("Warming up.")

    def synthesise(self, text: str, emotion: str = "") -> np.ndarray:
        """See base class. Kokoro has no delivery control, so emotion is ignored."""
        return self._run_pipeline(text, self._voice)

    def _run_pipeline(self, text: str, voice: object) -> np.ndarray:
        """Speaks one sentence through the Kokoro pipeline.

        Args:
            text: The sentence to speak.
            voice: What to pass as the pipeline's `voice`. Stock Kokoro
                names a built-in voice with a string; a fine-tune can hand
                over a loaded style tensor instead.

        Returns:
            The audio, trimmed, or empty if the pipeline produced nothing.
        """
        pieces = [
            np.asarray(audio.detach().cpu() if hasattr(audio, "detach") else audio,
                       dtype=np.float32)
            for _, _, audio in self._pipeline(text, voice=voice, speed=self._speed)
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
        return trim_silence(audio, self._silence_threshold, self._padding)


class VoxiTextToSpeech(KokoroTextToSpeech):
    """Voxi, an expressive Kokoro fine-tune with one voicepack per emotion.

    THE REASON FOR THIS ENGINE:
    Qwen3-TTS sounds far better and varies its delivery, but runs at ~2x
    real time here, which is the wrong side of the line where streaming
    synthesis can keep up with playback. Voxi runs at Kokoro's speed,
    because it IS Kokoro - the same 82M architecture, fine-tuned on an
    expressive corpus, with a style tensor per emotion.

    WHAT YOU GIVE UP, WHICH IS NOT SMALL:
    Eight fixed emotions instead of free-form instructions, trained on
    about 1.9 hours of one British-English speaker. Mel's four states do
    not map cleanly onto those eight - see VOXI_VOICEPACKS below - and
    the fine-tune publishes no evaluation. Treat the mapping as a first
    guess to be fixed by ear, which is why it lives in config.yaml.
    """

    def __init__(
        self,
        repo_id: str = "IOTEverythin/voxi-tts",
        weights_file: str = "kokoro_voxi_v1.pth",
        config_file: str = "config.json",
        lang_code: str = "b",
        voicepacks: dict[str, str] | None = None,
        speed: float = 1.0,
        device: str | None = None,
        silence_threshold: float = 0.01,
        edge_padding_ms: int = 20,
    ) -> None:
        """Stores settings; weights and voicepacks download in warm_up().

        Args:
            repo_id: Hugging Face repo holding the fine-tune. The model
                card and its own usage snippet disagree on this - the page
                is IOTEverythin/voxi-tts, the code says Joshuant/voxi-tts -
                so if the download 404s, try the other one.
            weights_file: Kokoro-format weights inside that repo.
            config_file: Kokoro config inside that repo.
            lang_code: Kokoro language code. "b" is British English, which
                is what this fine-tune was trained on; its voicepacks are
                all named bf_*. Note the stock engine uses an American
                voice, so switching engines also switches accent.
            voicepacks: Emotion tag -> voicepack name, without the path or
                extension. Missing tags fall back to the "" entry.
            speed: Speaking rate; 1.0 is normal.
            device: "cuda", "cpu", or None to use the GPU when available.
            silence_threshold: Amplitude below which audio counts as silence.
            edge_padding_ms: Silence kept at each end so nothing clips.
        """
        super().__init__(
            speed=speed,
            device=device,
            silence_threshold=silence_threshold,
            edge_padding_ms=edge_padding_ms)

        self._repo_id = repo_id
        self._weights_file = weights_file
        self._config_file = config_file
        self._lang_code = lang_code
        self._voicepacks = dict(voicepacks or VOXI_VOICEPACKS)

        # Emotion tag -> the loaded style tensor, filled in warm_up().
        self._packs: dict[str, object] = {}

    def warm_up(self) -> None:
        """See base class. Downloads the fine-tune and every voicepack."""
        # Imported here so fake-only setups never load PyTorch.
        import torch  # pylint: disable=import-outside-toplevel
        import kokoro  # pylint: disable=import-outside-toplevel
        from huggingface_hub import hf_hub_download  # pylint: disable=import-outside-toplevel

        config_path = hf_hub_download(self._repo_id, self._config_file)
        weights_path = hf_hub_download(self._repo_id, self._weights_file)

        # The architecture is stock Kokoro; only the weights are Voxi's,
        # which is why repo_id here still points at the base model.
        model = kokoro.KModel(
            repo_id=_KOKORO_REPO, config=config_path, model=weights_path).eval()

        # device is passed only when set, so the default path is exactly
        # the call the model card documents. KPipeline takes a device, and
        # it takes a model, but nothing says it takes both - and a model
        # built here already knows where it lives.
        options: dict[str, object] = {
            "lang_code": self._lang_code, "repo_id": _KOKORO_REPO, "model": model}
        if self._device is not None:
            options["device"] = self._device

        self._pipeline = kokoro.KPipeline(**options)

        # Load every pack up front. Fetching one mid-scenario would stall
        # the turn on a network call the moment Mel's mood changed.
        for name in sorted(set(self._voicepacks.values())):
            path = hf_hub_download(self._repo_id, f"voices/{name}.pt")
            self._packs[name] = torch.load(path, map_location="cpu", weights_only=True)

        self.synthesise("Warming up.")

    def synthesise(self, text: str, emotion: str = "") -> np.ndarray:
        """See base class."""
        return self._run_pipeline(text, self._pack_for(emotion))

    def _pack_for(self, emotion: str) -> object:
        """Picks the style tensor for an emotion tag.

        Args:
            emotion: Mel's current emotion tag, possibly empty or unknown.

        Returns:
            The loaded voicepack for that tag, or the fallback. Never
            raises: Mel's model writes these tags, so an unconfigured one
            is always possible, and losing her delivery for a turn is far
            better than losing her voice.
        """
        name = self._voicepacks.get(emotion) or self._voicepacks.get("")
        return self._packs.get(name)


class QwenTextToSpeech(TextToSpeech):
    """Qwen3-TTS CustomVoice: a preset speaker, steered per sentence.

    THE REASON FOR THIS ENGINE:
    Kokoro's delivery is flat. In a de-escalation trainer the parent's
    emotional temperature is the signal the trainee is learning to read, and
    a calm-sounding agitated parent removes it. CustomVoice takes a
    free-form English instruction describing how a line should be delivered,
    and because Mel's model emits her emotion tag BEFORE any dialogue, the
    right instruction is always known by the time a sentence is synthesised.

    Like Kokoro it runs in-process: no separate server and no container.
    """

    def __init__(
        self,
        model: str = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice",
        speaker: str = "Vivian",
        language: str = "English",
        device: str = "cuda:0",
        dtype: str = "bfloat16",
        instructions: dict[str, str] | None = None,
        expected_sample_rate: int = 24000,
        silence_threshold: float = 0.01,
        edge_padding_ms: int = 20,
    ) -> None:
        """Stores settings; the model loads in warm_up().

        Args:
            model: Hugging Face model id. Must be a CustomVoice variant; the
                Base and VoiceDesign variants expose different methods.
            speaker: One of the model's presets. Female: Vivian, Serena,
                Ono_Anna, Sohee. Male: Uncle_Fu, Dylan, Eric, Ryan, Aiden.
                The instruction shapes delivery; the speaker sets the voice
                itself, and no instruction will talk one into being another.
            language: Named rather than left to detection, because Mel's
                lines are short and clipped when she is agitated, which is
                the least that detection has to work with.
            device: Torch device, e.g. "cuda:0" or "cpu".
            dtype: Torch dtype name. Use "float32" on CPU.
            instructions: Emotion tag -> delivery instruction. Missing tags
                and the empty tag fall back to the "" entry.
            expected_sample_rate: What audio.playback_sample_rate is set to.
                Checked against the model's real output in warm_up().
            silence_threshold: Amplitude below which audio counts as silence.
            edge_padding_ms: Silence kept at each end so nothing clips.
        """
        self._model_id = model
        self._speaker = speaker
        self._language = language
        self._device = device
        self._dtype = dtype
        self._instructions = dict(instructions or _DEFAULT_INSTRUCTIONS)
        self._expected_sample_rate = expected_sample_rate
        self._silence_threshold = silence_threshold
        self._padding = edge_padding_ms * expected_sample_rate // 1000
        self._model = None

    def warm_up(self) -> None:
        """See base class. Downloads several GB on a cold first run."""
        # Imported here so fake-only setups never load PyTorch.
        import torch  # pylint: disable=import-outside-toplevel
        from qwen_tts import Qwen3TTSModel  # pylint: disable=import-outside-toplevel

        self._model = Qwen3TTSModel.from_pretrained(
            self._model_id,
            device_map=self._device,
            dtype=getattr(torch, self._dtype, torch.float32))

        self._warm_and_check()

    def _warm_and_check(self) -> None:
        """Synthesises one throwaway line and checks the output rate.

        Two jobs in one call. The first pays any one-off cost now rather
        than on the trainee's first turn. The second catches a rate
        mismatch at startup: the server resamples nothing, so it would
        otherwise reach the headset as a Mel whose voice is the wrong pitch
        and speed - obvious to hear, but easy to misread as a bad model.
        """
        _, rate = self._generate("Warming up.", self._instructions.get("", ""))
        if rate != self._expected_sample_rate:
            print(f"WARNING: {self._model_id} outputs {rate} Hz but "
                  f"audio.playback_sample_rate is {self._expected_sample_rate}. "
                  "Set them to match, in config.yaml and PipelineConfig.asset.",
                  flush=True)

    def synthesise(self, text: str, emotion: str = "") -> np.ndarray:
        """See base class."""
        audio, _ = self._generate(text, self._instruction_for(emotion))
        if audio.size == 0:
            return audio
        return trim_silence(audio, self._silence_threshold, self._padding)

    def _generate(self, text: str, instruct: str) -> tuple[np.ndarray, int]:
        """Calls the model once.

        Args:
            text: The sentence to speak.
            instruct: How to deliver it. Omitted from the call when empty,
                because an explicit None is not the same as no instruction.

        Returns:
            The mono float32 audio and the rate the model produced it at.
        """
        options = {"text": text, "language": self._language, "speaker": self._speaker}
        if instruct:
            options["instruct"] = instruct

        # Every generate_* method on this model is built for batching, so it
        # returns a LIST of waveforms even for one line. Treating that list
        # as the audio does not raise - it just produces something unplayable.
        waveforms, rate = self._model.generate_custom_voice(**options)
        if waveforms is None or len(waveforms) == 0:
            return np.zeros(0, dtype=np.float32), rate

        audio = np.asarray(
            waveforms[0].detach().cpu() if hasattr(waveforms[0], "detach") else waveforms[0],
            dtype=np.float32)
        # Mel is one person in one place; her position in the room is
        # Unity's business, so mix any stereo down rather than send two ears.
        return (audio.mean(axis=1) if audio.ndim > 1 else audio), rate

    def _instruction_for(self, emotion: str) -> str:
        """Turns an emotion tag into a delivery instruction.

        Args:
            emotion: Mel's current emotion tag, possibly empty or unknown.

        Returns:
            The instruction for that tag, or the fallback.
        """
        return self._instructions.get(emotion, self._instructions.get("", ""))


# Lead with the speaker's age and sex: strong emotion prompts are known to
# drift the voice, and stating who is speaking first holds it steady.
# Overridable per deployment from the qwen options block in config.yaml.
class FasterQwenTextToSpeech(QwenTextToSpeech):
    """The same Qwen3-TTS models, loaded through faster-qwen3-tts.

    THE REASON FOR A SECOND QWEN ENGINE:
    The stock library generates at roughly 2x real time on a laptop RTX
    5090 - slower than playback, so streaming synthesis falls further
    behind with every sentence. The GPU is only ~26% busy while that
    happens, and the 0.6B model is no faster than the 1.7B, which together
    say the cost is not compute: it is the ~500 small kernel launches per
    decode step, which barely change with model size.

    faster-qwen3-tts captures that decode step as a CUDA graph, replaying
    it as one launch. Its author measures ~5x on an RTX 4090. Nothing else
    is required - no vLLM, no Triton, no FlashAttention, no compiler - so
    unlike the alternatives it costs no new infrastructure on Windows.

    Everything below the loader is inherited: same call, same instruction
    map, same trimming. Only the import and the construction differ, which
    is also what makes the two engines a fair A/B.
    """

    def __init__(
        self,
        model: str = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice",
        speaker: str = "Vivian",
        language: str = "English",
        device: str = "cuda",
        dtype: str = "bfloat16",
        attn_implementation: str = "sdpa",
        instructions: dict[str, str] | None = None,
        expected_sample_rate: int = 24000,
        silence_threshold: float = 0.01,
        edge_padding_ms: int = 20,
    ) -> None:
        """Stores settings; the model loads in warm_up().

        Args:
            model: As for QwenTextToSpeech.
            speaker: As for QwenTextToSpeech.
            language: As for QwenTextToSpeech.
            device: Torch device. Note this takes "cuda", not "cuda:0" -
                a different argument from the stock library's device_map.
            dtype: Torch dtype name; this loader accepts it as a string.
            attn_implementation: "sdpa" needs nothing extra installed.
            instructions: As for QwenTextToSpeech.
            expected_sample_rate: As for QwenTextToSpeech.
            silence_threshold: As for QwenTextToSpeech.
            edge_padding_ms: As for QwenTextToSpeech.
        """
        super().__init__(
            model=model,
            speaker=speaker,
            language=language,
            device=device,
            dtype=dtype,
            instructions=instructions,
            expected_sample_rate=expected_sample_rate,
            silence_threshold=silence_threshold,
            edge_padding_ms=edge_padding_ms)

        self._attn_implementation = attn_implementation

    def warm_up(self) -> None:
        """See base class.

        The throwaway synthesis matters more here than for other engines:
        it is the call that captures the CUDA graph, so skipping it would
        move that cost onto the trainee's first turn.
        """
        # Imported here so fake-only setups never load PyTorch.
        from faster_qwen3_tts import FasterQwen3TTS  # pylint: disable=import-outside-toplevel

        self._model = FasterQwen3TTS.from_pretrained(
            self._model_id,
            device=self._device,
            dtype=self._dtype,
            attn_implementation=self._attn_implementation)

        self._warm_and_check()


_KOKORO_REPO = "hexgrad/Kokoro-82M"


# Mel's four states onto Voxi's eight voicepacks. This is a first guess,
# not a finding, and two things about it deserve a listening test:
#
#   "angry" is the only pack near agitated, but Mel is upset about money,
#   not aggressive, and the scenario's rule is that she never escalates.
#   If angry reads as shouting, try "surprised" or "sad" instead.
#
#   softening and calm both land on neutral, which flattens the half of
#   the arc where the trainee's work shows. Try softening -> sad if the
#   two need separating, since Voxi's sad is subdued rather than weepy.
#
# Overridable from the voxi options block in config.yaml.
VOXI_VOICEPACKS: dict[str, str] = {
    "agitated": "bf_angry",
    "distressed": "bf_sad",
    "softening": "bf_neutral",
    "calm": "bf_neutral",
    "": "bf_neutral",
}


_DEFAULT_INSTRUCTIONS: dict[str, str] = {
    "agitated": "A woman in her forties, tense and frustrated, speaks quickly with "
                "clipped, pressured energy.",
    "distressed": "A woman in her forties, upset and close to tears, speaks quietly "
                  "with a wavering, unsteady voice.",
    "softening": "A woman in her forties, still guarded but beginning to relax, speaks "
                 "at a moderate pace with lowered energy.",
    "calm": "A woman in her forties speaks calmly and evenly at a moderate pace with "
            "warm, settled energy.",
    "": "A woman in her forties speaks at a moderate pace.",
}


def trim_silence(audio: np.ndarray, threshold: float, padding: int) -> np.ndarray:
    """Removes an engine's own leading and trailing silence, keeping a little.

    Engines pad every clip, so joined sentences would get double-length
    pauses. The server adds one controlled gap between sentences instead.

    Args:
        audio: One synthesised clip.
        threshold: Amplitude below which a sample counts as silence.
        padding: Samples of silence to keep at each end so nothing clips.

    Returns:
        The clip without its silent ends (unchanged if entirely silent).
    """
    loud = np.flatnonzero(np.abs(audio) > threshold)
    if loud.size == 0:
        return audio
    return audio[max(0, loud[0] - padding):loud[-1] + 1 + padding]


_ENGINES: dict[str, type[TextToSpeech]] = {
    "fake": FakeTextToSpeech,
    "kokoro": KokoroTextToSpeech,
    "voxi": VoxiTextToSpeech,
    "qwen": QwenTextToSpeech,
    "faster-qwen": FasterQwenTextToSpeech,
}


def build(settings: config.EngineSettings) -> TextToSpeech:
    """Creates the engine named in the config.

    Args:
        settings: The text_to_speech config section.

    Returns:
        The engine, constructed with the section's options.
    """
    return _ENGINES[settings.engine](**settings.options)
