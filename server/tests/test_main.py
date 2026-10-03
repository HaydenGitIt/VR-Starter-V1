"""End-to-end test of the WebSocket protocol through the real app."""

import json

from fastapi import testclient

from app import main


def test_conversation_over_websocket() -> None:
    """Connect, get the handshake, take one turn, and check frame kinds."""
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
