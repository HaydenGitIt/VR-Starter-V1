"""Exercises the configured TTS engine on its own, outside the server.

Run this before switching the server over to a new engine. A failure here
is a plain traceback at the line that caused it; the same failure during
the server's background warm-up is a thread that quietly dies, leaving a
server that never reports ready and says nothing about why.

It answers the four questions a new engine raises:

  1. Does the model load at all?
  2. Does the generate call have the signature we think it has?
  3. What sample rate does it really output? (audio.playback_sample_rate
     and Unity's PipelineConfig must both match it, or Mel arrives at the
     wrong pitch and speed.)
  4. How long does a sentence take? Mel starts speaking after the first
     one, so this is most of the trainee's perceived delay.

It also writes a WAV per emotion, because whether "distressed" actually
sounds distressed is not something a test can decide.

  .venv\\Scripts\\python check_tts.py
  .venv\\Scripts\\python check_tts.py --set text_to_speech.qwen.speaker=Serena
"""

import argparse
import pathlib
import time
import wave

import numpy as np

from app import config
from app.engines import tts

_OUT_DIR = pathlib.Path(__file__).resolve().parent / "samples" / "tts_check"


def _parse_args() -> argparse.Namespace:
    """Reads the command line.

    Returns:
        The parsed arguments.
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--set", dest="overrides", action="append", default=[], metavar="section.key=value",
        help="Config override, e.g. text_to_speech.engine=qwen. Repeatable.")
    parser.add_argument(
        "--text", default=None,
        help="Line to speak. Defaults to the scenario's opening line.")
    return parser.parse_args()


def _write_wav(path: pathlib.Path, audio: np.ndarray, sample_rate: int) -> None:
    """Writes mono float32 audio as a 16-bit WAV.

    Args:
        path: File to write.
        audio: Mono float32 samples in [-1, 1].
        sample_rate: Rate to record in the header.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    pcm = (np.clip(audio, -1.0, 1.0) * 32767).astype("<i2")
    with wave.open(str(path), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(sample_rate)
        f.writeframes(pcm.tobytes())


def main() -> None:
    """Loads the engine, speaks one line per emotion, and reports."""
    args = _parse_args()
    settings = config.load(args.overrides)
    chosen = settings.text_to_speech

    print(f"Engine:   {chosen.engine}")
    for key, value in chosen.options.items():
        # The instruction map is long; show that it is there, not all of it.
        shown = f"<{len(value)} entries>" if isinstance(value, dict) else value
        print(f"  {key}: {shown}")

    engine = tts.build(chosen)

    print("\nLoading the model. A cold first run may download several GB.")
    started = time.monotonic()
    engine.warm_up()
    print(f"Loaded in {time.monotonic() - started:.1f}s.")

    text = args.text or settings.scenario.opening_line

    # Every emotion the scenario and the prompt can produce. An engine that
    # ignores emotion simply makes five identical files, which is itself
    # worth hearing when comparing engines.
    emotions = ["agitated", "distressed", "softening", "calm", ""]

    print(f"\nSpeaking: {text!r}\n")
    failures = 0

    for emotion in emotions:
        label = emotion or "(no tag)"
        started = time.monotonic()
        try:
            audio = engine.synthesise(text, emotion)
        except Exception as error:                              # noqa: BLE001
            # Keep going: one emotion failing while the others work is a
            # much more useful report than stopping at the first.
            print(f"  {label:<12} FAILED  {type(error).__name__}: {error}")
            failures += 1
            continue

        elapsed = time.monotonic() - started
        if audio.size == 0:
            print(f"  {label:<12} produced no audio in {elapsed:.2f}s")
            failures += 1
            continue

        seconds = audio.size / settings.playback_sample_rate
        path = _OUT_DIR / f"{chosen.engine}_{emotion or 'default'}.wav"
        _write_wav(path, audio, settings.playback_sample_rate)

        # Ratio of synthesis time to audio length. Below 1.0 means the
        # engine generates faster than the audio plays, which is what keeps
        # sentence-by-sentence streaming ahead of playback.
        print(f"  {label:<12} {elapsed:5.2f}s for {seconds:5.2f}s of audio "
              f"({elapsed / seconds:.2f}x realtime)  -> {path.name}")

    print(f"\nWAVs in {_OUT_DIR}")
    print("Listen for: the right voice, delivery that actually differs between")
    print("emotions, and no clicks at the start or end (those would join badly).")

    if failures:
        print(f"\n{failures} of {len(emotions)} emotions failed.")
    else:
        print(f"\nAll {len(emotions)} emotions spoke.")

    print(f"\nIf the pitch or speed sounds wrong, the engine is not producing "
          f"{settings.playback_sample_rate} Hz. Match audio.playback_sample_rate in "
          f"config.yaml AND playbackSampleRate in Assets/Config/PipelineConfig.asset "
          f"to whatever it really outputs.")


if __name__ == "__main__":
    main()
