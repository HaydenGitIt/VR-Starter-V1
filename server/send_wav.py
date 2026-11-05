"""Sends WAV files to a running server as utterances, the way Unity does.

Usage: .venv\\Scripts\\python send_wav.py FILE.wav [FILE.wav ...] [--port 8000]

Files must be 16-bit mono at the server's capture rate (16000 Hz), which
is what Unity's CapturedUtterances folder contains.
"""

import argparse
import asyncio
import json
import wave

import numpy as np
import websockets


def read_wav(path: str) -> tuple[np.ndarray, int]:
    """Loads a 16-bit mono WAV as float32 samples.

    Args:
        path: The WAV file.

    Returns:
        The samples in [-1, 1] and the sample rate.

    Raises:
        ValueError: If the file is not 16-bit mono.
    """
    with wave.open(path, "rb") as f:
        if f.getnchannels() != 1 or f.getsampwidth() != 2:
            raise ValueError(f"{path}: needs 16-bit mono audio.")
        pcm = np.frombuffer(f.readframes(f.getnframes()), dtype="<i2")
        return pcm.astype(np.float32) / 32768, f.getframerate()


async def send(paths: list[str], port: int) -> None:
    """Sends each file as one utterance and prints what comes back.

    Args:
        paths: WAV files to send, in order.
        port: Port the server is listening on.
    """
    async with websockets.connect(f"ws://127.0.0.1:{port}/conversation") as ws:
        ready = json.loads(await ws.recv())
        for path in paths:
            samples, rate = read_wav(path)
            if rate != ready["capture_sample_rate"]:
                raise ValueError(
                    f"{path}: is {rate} Hz, server expects {ready['capture_sample_rate']} Hz.")
            print(f"\n>>> {path} ({samples.size / rate:.1f}s)")
            await ws.send(samples.astype("<f4").tobytes())
            while True:
                message = await ws.recv()
                if isinstance(message, bytes):
                    print(f"  <audio {len(message) // 4} samples>")
                    continue
                print(f"  {message}")
                if json.loads(message)["type"] in ("turn_end", "discarded", "error"):
                    break


def main() -> None:
    """Parses arguments and sends the files."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("paths", nargs="+", help="WAV files to send")
    parser.add_argument("--port", type=int, default=8000)
    args = parser.parse_args()
    asyncio.run(send(args.paths, args.port))


if __name__ == "__main__":
    main()
