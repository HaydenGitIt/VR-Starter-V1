"""The game's own settings, and Mel played through its real ScenarioSession.

Mel is not a copy of the game: she is server/app/session.py's ScenarioSession, fed text
instead of audio, with the fake STT and TTS engines. Its dialogue engine is a thin
ProviderDialogue that sends her prompt to any provider. So the prompt, fixed opening,
start token, tagged history, retries, fallback line, fragment dropping, scoring and
endings all come from the game code.
"""

import pathlib
import time
from typing import Any

import numpy as np

from app import config, session
from app.engines import dialogue, director, stt, tts
from datagen import providers

SERVER_DIR = pathlib.Path(__file__).resolve().parents[1]
SETTINGS = config.load()
RULES = SETTINGS.scenario
assert SETTINGS.dialogue.engine == "ollama", "Mel's options are read from the ollama block."
GAME = SETTINGS.dialogue.options  # temperature, max_tokens, tag_deadline_chars, ...
PARENT_PROMPT = (SERVER_DIR / GAME["prompt"]).read_text(encoding="utf-8")
TAGS = dialogue.OllamaDialogue._TAGS  # pylint: disable=protected-access  (the game's regex)


class ProviderDialogue(dialogue.Dialogue):
    """Mel on any provider, checked the way OllamaDialogue checks its stream.

    The game streams and gives up once tag_deadline_chars arrive with no tags. Here the
    whole (55-token) reply arrives at once, so the equivalent check is that the game's
    tag regex matches and ends within the deadline.
    """

    def __init__(self, role: dict[str, Any]) -> None:
        """Initialises the engine.

        Args:
            role: Mel's role.
        """
        self._role = role
        self.attempts: list[dict[str, Any]] = []

    def generate(self, history: list[dict[str, str]], state_note: str = "",
                 judgement: director.Judgement | None = None) -> dialogue.Reply:
        """See base class. Records every attempt in self.attempts.

        Mel judges the line herself here, as in the original design: the data's labels
        come from the graders, so ParentGame never passes a judgement.
        """
        result = providers.complete(self._role, f"{PARENT_PROMPT}\n\n{state_note}", history)
        self.attempts.append({"raw": result.text, "latency_s": result.latency_s,
                              "usage": result.usage})
        match = TAGS.match(result.text)
        if not match or match.end() > GAME["tag_deadline_chars"]:
            raise dialogue.MalformedReply(f"No tag in output: {result.text[:80]!r}")
        return dialogue.Reply(match.group(1).lower(),
                              dialogue._words(result.text[match.end():]))  # pylint: disable=protected-access


class ParentGame:
    """One run of the game's scenario session, driven by text."""

    def __init__(self, role: dict[str, Any]) -> None:
        """Starts the scenario; Mel has said her opening line.

        Args:
            role: Mel's role.
        """
        self.engine = ProviderDialogue(role)
        self.session = session.ScenarioSession(
            SETTINGS, stt.FakeSpeechToText(), self.engine, tts.FakeTextToSpeech(),
            opening_audio=np.zeros(1, dtype=np.float32))
        list(self.session.handle_command({"type": "begin"}))
        self.ended = False

    def respond(self, transcript: str) -> dict[str, Any]:
        """Plays one trainee turn through the game session.

        Args:
            transcript: What the trainee said, as the game would hear it.

        Returns:
            Mel's raw attempts, spoken line, tags, retries, latency and running counters.

        Raises:
            RuntimeError: If the game did not take the turn (e.g. the line was empty).
        """
        self.engine.attempts = []
        started = time.monotonic()
        messages = [m for m in self.session.handle_command({"type": "text", "content": transcript})
                    if isinstance(m, dict)]
        by_type = {m["type"]: m for m in messages}
        if "turn_complete" not in by_type:
            raise RuntimeError(f"The game did not take the turn: {messages}")
        summary = next(self.session.handle_command({"type": "summary"}))
        self.ended = by_type["turn_end"]["ended"]
        done = by_type["turn_complete"]
        return {
            "mel_raw": [a["raw"] for a in self.engine.attempts],
            "mel_line": by_type["reply"]["text"],
            "verdict": done["verdict"],              # "unparsed" when the fallback was used
            "emotion": done["emotion"],
            "unparsed": done["verdict"] == "unparsed",
            "retries": max(len(self.engine.attempts) - 1, 0),
            "parent_s": [a["latency_s"] for a in self.engine.attempts],
            "parent_usage": [a["usage"] for a in self.engine.attempts],
            "game_turn_s": round(time.monotonic() - started, 3),
            "strikes": summary["strikes"], "progress": summary["progress"],
            "unparsed_total": summary["unparsed"],
            "outcome": summary["outcome"] if self.ended else None,
        }
