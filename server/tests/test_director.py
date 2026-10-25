"""Tests for the director engines and Mel acting out a director's verdict."""

import types
from collections.abc import Iterator
from typing import Any

import pytest

from app import config
from app.engines import dialogue, director


def test_state_from_history_matches_the_training_export() -> None:
    """The director reads Mel's last line (untagged) and the staff member's last two."""
    history = [{"role": "user", "content": "[SCENARIO_START]"},
               {"role": "assistant", "content": "<neutral><agitated>Eighty dollars."},
               {"role": "user", "content": "I hear you."},
               {"role": "assistant", "content": "<deescalated><agitated>Then you're the first."},
               {"role": "user", "content": "I hear you."}]
    assert director.state_from_history(history) == director.state(
        "Then you're the first.", "I hear you.", "I hear you.")
    assert director.state_from_history(history[:3]) == director.state("Eighty dollars.", "", "I hear you.")


def test_laya_director_applies_both_thresholds() -> None:
    """Reaching her needs P(reached) at the threshold and P(repeated) below its own."""
    engine = director.LayaDirector(threshold=0.5, repeated_threshold=0.5)

    def judge(reached: float, repeated: float) -> str:
        """Runs the engine on canned Laya answers."""
        answers = {"reached_her": {"noul": reached}, "repeated": {"noul": repeated}}
        engine._agent = types.SimpleNamespace(  # pylint: disable=protected-access
            predict=lambda state, questions: {"answers": answers})
        return engine.judge([{"role": "user", "content": "I hear you."}]).verdict

    assert judge(0.8, 0.1) == "deescalated"
    assert judge(0.4, 0.1) == "neutral"
    assert judge(0.8, 0.7) == "neutral"


def test_director_engine_is_chosen_in_config() -> None:
    """The director section picks the engine, and "none" leaves Mel's own tags in charge."""
    assert isinstance(director.build(config.load(["director.engine=none"]).director), director.NoDirector)
    assert isinstance(director.build(config.load(["director.engine=fake"]).director), director.FakeDirector)


def test_ollama_mel_acts_out_a_judgement(monkeypatch: pytest.MonkeyPatch) -> None:
    """The verdict is pre-filled and the note sent with the request, and the tags parse.

    Args:
        monkeypatch: Replaces the Ollama client's chat call.
    """
    requests: list[dict[str, Any]] = []

    def chat(**request: Any) -> Iterator[types.SimpleNamespace]:
        """Records the request and streams Mel's continuation after the pre-fill.

        Args:
            **request: The keyword arguments the engine sent.

        Returns:
            A two-chunk stream.
        """
        requests.append(request)
        return (types.SimpleNamespace(message=types.SimpleNamespace(content=c))
                for c in ["agitated>Is that", " it?"])

    engine = dialogue.OllamaDialogue()
    monkeypatch.setattr(engine._client, "chat", chat)  # pylint: disable=protected-access
    reply = engine.generate([{"role": "user", "content": "Okay."}],
                            director.judgement("neutral", 0.1))
    assert (reply.verdict, reply.emotion, "".join(reply.text)) == ("neutral", "agitated", "Is that it?")
    *_, note, prefill = requests[0]["messages"]
    assert note == {"role": "system", "content": director.NOTES["neutral"]}
    assert prefill == {"role": "assistant", "content": "<neutral><"}
