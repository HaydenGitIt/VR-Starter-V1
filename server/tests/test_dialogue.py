"""Tests for the Ollama engine's requests, with no Ollama needed."""

import types
from collections.abc import Iterator
from typing import Any

import pytest

from app.engines import dialogue


def test_ollama_requests_share_context_and_keep_alive(monkeypatch: pytest.MonkeyPatch) -> None:
    """Warm-up and turns ask for the configured context size and keep-alive.

    Ollama reloads the model if num_ctx changes between requests, and its
    default keep-alive unloads the model after 5 idle minutes.

    Args:
        monkeypatch: Replaces the Ollama client's chat call.
    """
    requests: list[dict[str, Any]] = []

    def chat(**request: Any) -> Iterator[types.SimpleNamespace]:
        """Records the request and streams one tagged chunk.

        Args:
            **request: The keyword arguments the engine sent.

        Returns:
            A one-chunk stream.
        """
        requests.append(request)
        chunk = types.SimpleNamespace(message=types.SimpleNamespace(content="<neutral><calm>Hi."))
        return (c for c in [chunk])

    engine = dialogue.OllamaDialogue(context_length=6144, keep_alive="30m")
    monkeypatch.setattr(engine._client, "chat", chat)
    engine.warm_up()
    engine.generate([{"role": "user", "content": "Hello."}])
    assert [(r["options"]["num_ctx"], r["keep_alive"]) for r in requests] == [(6144, "30m")] * 2
