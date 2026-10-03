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
                    types.append(json.loads(frame["text"])["type"])

    assert types == ["tags", "reply", "turn_complete", "audio", "turn_end"]
