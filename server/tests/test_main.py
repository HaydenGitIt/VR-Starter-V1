"""End-to-end test of the WebSocket protocol through the real app."""

import json

import pytest
from fastapi import testclient

from app import main
from app.engines import dialogue, stt, tts


def test_conversation_over_websocket(monkeypatch: pytest.MonkeyPatch) -> None:
    """Connect, get the handshake, take one turn, and check frame kinds.

    Args:
        monkeypatch: Swaps in fake engines, whatever config.yaml says.
    """
    monkeypatch.setattr(main, "speech_to_text", stt.FakeSpeechToText())
    monkeypatch.setattr(main, "dialogue_engine", dialogue.FakeDialogue())
    monkeypatch.setattr(main, "text_to_speech", tts.FakeTextToSpeech())
    with testclient.TestClient(main.app) as client:
        with client.websocket_connect("/conversation") as ws:
            assert ws.receive_json()["type"] == "ready"

            ws.send_text(json.dumps({"type": "text", "content": "Calm down."}))
            types = []
            while not types or types[-1] != "turn_end":
                frame = ws.receive()
                if frame.get("bytes") is not None:
                    assert frame["bytes"][0] != ord("{")
                    if types[-1] != "audio":
                        types.append("audio")
                else:
                    assert frame["text"].startswith("{")
                    message = json.loads(frame["text"])
                    assert message["type"] != "error", message
                    types.append(message["type"])

    assert types == ["tags", "audio", "reply", "turn_complete", "turn_end"]


class _Unreachable(dialogue.Dialogue):
    """A dialogue engine whose server is down."""

    def generate(self, history: list[dict[str, str]]) -> dialogue.Reply:
        """Fails as if Ollama were not running."""
        raise ConnectionError("Failed to connect to Ollama.")


def test_engine_failure_still_ends_the_turn(monkeypatch: pytest.MonkeyPatch) -> None:
    """An engine error is reported, then the turn ends so Unity reopens the mic.

    Args:
        monkeypatch: Swaps in fake engines, with a dialogue engine that fails.
    """
    monkeypatch.setattr(main, "speech_to_text", stt.FakeSpeechToText())
    monkeypatch.setattr(main, "dialogue_engine", _Unreachable())
    monkeypatch.setattr(main, "text_to_speech", tts.FakeTextToSpeech())
    with testclient.TestClient(main.app) as client:
        with client.websocket_connect("/conversation") as ws:
            assert ws.receive_json()["type"] == "ready"
            ws.send_text(json.dumps({"type": "text", "content": "I hear you."}))
            # Queued now so a missing turn_end fails the test instead of hanging it.
            ws.send_text(json.dumps({"type": "summary"}))
            assert ws.receive_json()["type"] == "error"
            assert ws.receive_json() == {"type": "turn_end", "ended": False}
            assert ws.receive_json()["type"] == "summary"
