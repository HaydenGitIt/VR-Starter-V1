"""Measures faster-qwen3-tts per emotion, to decide whether it can replace Kokoro.

The speed factor is seconds to generate per second of speech. Mel needs under
0.5x, or the trainee waits too long for her to start speaking. Stop the server
first so the GPU is free, and listen to the WAVs: faster is no use if the
emotions don't sound different.

  .venv-tts\Scripts\python check_faster_qwen.py
"""

import pathlib
import time
import wave

import numpy as np
import torch
import transformers
from faster_qwen3_tts import FasterQwen3TTS

from app import config

_OUT_DIR = pathlib.Path(__file__).resolve().parent / "samples" / "tts_check"
_TARGET = 0.5
_RUNS = 3
_LINES = {
    "agitated": "Eighty dollars. I can't afford that!",
    "distressed": "She loves it here. That's the whole problem.",
    "softening": "Right. Well. My hours got cut in March and now this.",
    "calm": "Thank you. That helps more than you know.",
}


def _save(path: pathlib.Path, audio: np.ndarray, rate: int) -> None:
    """Writes a 16-bit mono WAV.

    Args:
        path: Where to write it.
        audio: Mono float32 audio.
        rate: Sample rate in Hz.
    """
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(rate)
        out.writeframes((np.clip(audio, -1, 1) * 32767).astype("<i2").tobytes())


def main() -> None:
    """Loads the model, times each emotion and prints a verdict."""
    print(f"transformers {transformers.__version__}, torch {torch.__version__}, "
          f"{torch.cuda.get_device_name(0)}", flush=True)
    options = config.load(["text_to_speech.engine=qwen"]).text_to_speech.options

    started = time.monotonic()
    model = FasterQwen3TTS.from_pretrained(options["model"], device=options["device"],
                                           dtype=options["dtype"])
    model.warmup()
    print(f"Loaded in {time.monotonic() - started:.0f}s, "
          f"{torch.cuda.max_memory_reserved() / 1e9:.1f} GB of GPU memory", flush=True)

    _OUT_DIR.mkdir(parents=True, exist_ok=True)
    worst = 0.0
    for emotion, text in _LINES.items():
        for run in range(_RUNS):
            started = time.monotonic()
            waveforms, rate = model.generate_custom_voice(
                text=text, speaker=options["speaker"], language=options["language"],
                instruct=options["instructions"][emotion])
            took = time.monotonic() - started
            audio = np.asarray(waveforms[0], dtype=np.float32)
            factor = took / (audio.size / rate)
            print(f"{emotion:10} run {run + 1}: {took:5.1f}s for {audio.size / rate:.1f}s "
                  f"of speech = {factor:.2f}x", flush=True)
            if run:  # The first run of a line can include one-off setup.
                worst = max(worst, factor)
        _save(_OUT_DIR / f"faster_qwen_{emotion}.wav", audio, rate)

    print(f"Slowest: {worst:.2f}x, target under {_TARGET}x: {'PASS' if worst < _TARGET else 'FAIL'}")
    print(f"Listen to the WAVs in {_OUT_DIR}")


if __name__ == "__main__":
    main()
