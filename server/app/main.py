"""HTTP and WebSocket entry point. Run with: python -m app.main"""

import argparse
import asyncio
import contextlib
import json
import pathlib
import traceback
from collections.abc import AsyncIterator, Iterator
from typing import Any

import fastapi
import numpy as np
import uvicorn
from fastapi import staticfiles

from app import config, session
from app.engines import dialogue, stt, tts

_WEB_DIR = pathlib.Path(__file__).resolve().parent.parent / "web"



def _parse_args() -> list[str]:
    """Reads --set overrides from the command line.

    Returns:
        The override strings, in order.
    """
    parser = argparse.ArgumentParser(description="De-escalation conversation server.")
    parser.add_argument(
        "--set", action="append", default=[], metavar="SECTION.KEY=VALUE",
        help="override a config value for this run, e.g. dialogue.ollama.model=qwen2.5:3b")
    return parser.parse_args().set


# Only read the command line when run as the server, not when imported.
settings = config.load(_parse_args() if __name__ == "__main__" else ())
speech_to_text = stt.build(settings.speech_to_text)
dialogue_engine = dialogue.build(settings.dialogue)
text_to_speech = tts.build(settings.text_to_speech)
engines_ready = asyncio.Event()


def _warm_up() -> np.ndarray:
    """Loads every engine's models. Blocking; run in a thread.

    Returns:
        The opening line, synthesised once and reused by every session so
        the scenario starts the instant the trainee is ready.
    """
    speech_to_text.warm_up()
    dialogue_engine.warm_up()
    text_to_speech.warm_up()
    return text_to_speech.synthesise(settings.scenario.opening_line)


@contextlib.asynccontextmanager
async def lifespan(_: fastapi.FastAPI) -> AsyncIterator[None]:
    """Warms the engines in the background so /health can answer meanwhile.

    Args:
        _: The app (unused).

    Yields:
        Control to the running server.
    """

    async def warm() -> None:
        try:
            app.state.opening_audio = await asyncio.to_thread(_warm_up)
        except Exception:  # pylint: disable=broad-except
            # Otherwise the failure is silent and /health stays not-ready.
            traceback.print_exc()
            print("Warm-up FAILED; the server will never report ready.", flush=True)
            return
        engines_ready.set()
        print("Ready.", flush=True)

    task = asyncio.create_task(warm())
    yield
    task.cancel()


app = fastapi.FastAPI(lifespan=lifespan)


@app.get("/health")
def health() -> dict[str, Any]:
    """Reports readiness and audio formats, as ConversationClient expects.

    Returns:
        The health payload.
    """
    return {
        "ready": engines_ready.is_set(),
        "capture": _audio_format(settings.capture_sample_rate),
        "playback": _audio_format(settings.playback_sample_rate),
    }


@app.websocket("/conversation")
async def conversation(websocket: fastapi.WebSocket) -> None:
    """Runs one scenario session over a WebSocket.

    Args:
        websocket: The client connection.
    """
    await websocket.accept()
    await engines_ready.wait()
    scenario = session.ScenarioSession(
        settings, speech_to_text, dialogue_engine, text_to_speech,
        websocket.app.state.opening_audio)
    await websocket.send_json(scenario.ready_message())

    while True:
        frame = await websocket.receive()
        if frame["type"] == "websocket.disconnect":
            return
        try:
            if frame.get("bytes") is not None:
                messages = scenario.handle_audio(frame["bytes"])
            else:
                messages = scenario.handle_command(json.loads(frame["text"]))
            await _send_all(websocket, messages)
        except fastapi.WebSocketDisconnect:
            return
        except Exception as e:  # pylint: disable=broad-except
            await websocket.send_json({"type": "error", "message": str(e)})


async def _send_all(
    websocket: fastapi.WebSocket, messages: Iterator[session.Message]
) -> None:
    """Sends a handler's messages as they are produced.

    Each step of the generator runs in a thread, because producing a
    message may mean a blocking engine call.

    Args:
        websocket: The client connection.
        messages: Output of a ScenarioSession handler.
    """
    while (message := await asyncio.to_thread(next, messages, None)) is not None:
        if isinstance(message, bytes):
            await websocket.send_bytes(message)
        else:
            await websocket.send_json(message)


def _audio_format(sample_rate: int) -> dict[str, Any]:
    """Describes one audio stream. The server only handles mono float32.

    Args:
        sample_rate: Samples per second.

    Returns:
        The format block used in the health payload.
    """
    return {"sample_rate": sample_rate, "channels": 1, "format": "float32"}


# Mounted last so the routes above take priority over static files.
app.mount("/", staticfiles.StaticFiles(directory=_WEB_DIR, html=True), name="web")


def main() -> None:
    """Starts the server on the configured host and port."""
    uvicorn.run(app, host=settings.host, port=settings.port)


if __name__ == "__main__":
    main()
