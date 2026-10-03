"""HTTP and WebSocket entry point. Run with: python -m app.main"""

from typing import Any

import fastapi
import uvicorn

from app import config

settings = config.load()
app = fastapi.FastAPI()


@app.get("/health")
def health() -> dict[str, Any]:
    """Reports readiness and audio formats, as ConversationClient expects.

    Returns:
        The health payload.
    """
    return {
        "ready": True,
        "capture": _audio_format(settings.capture_sample_rate),
        "playback": _audio_format(settings.playback_sample_rate),
    }


def _audio_format(sample_rate: int) -> dict[str, Any]:
    """Describes one audio stream. The server only handles mono float32.

    Args:
        sample_rate: Samples per second.

    Returns:
        The format block used in the health payload.
    """
    return {"sample_rate": sample_rate, "channels": 1, "format": "float32"}


def main() -> None:
    """Starts the server on the configured host and port."""
    uvicorn.run(app, host=settings.host, port=settings.port)


if __name__ == "__main__":
    main()
