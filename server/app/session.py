"""Per-connection scenario state: history, counters and end conditions.

Each handler is a generator of outgoing messages: a dict is sent as a JSON
text frame, bytes as a binary audio frame. Handlers are synchronous because
the engines block; the server runs them off the event loop.

Turn order: transcript -> tags -> reply -> turn_complete -> [ending]
-> audio... -> turn_end. A turn that ends the scenario sends no audio, so
Unity can start its ending Timeline without interrupting speech.
"""

import time
from collections.abc import Iterator
from typing import Any

import numpy as np

from app import config
from app.engines import dialogue, stt, tts

Message = dict[str, Any] | bytes


class ScenarioSession:
    """One trainee's attempt at the scenario."""

    def __init__(
        self,
        settings: config.Settings,
        speech_to_text: stt.SpeechToText,
        dialogue_engine: dialogue.Dialogue,
        text_to_speech: tts.TextToSpeech,
    ) -> None:
        """Initialises an empty session.

        Args:
            settings: Server settings, including the scenario rules.
            speech_to_text: Engine for incoming audio.
            dialogue_engine: Engine that writes the parent's replies.
            text_to_speech: Engine for outgoing audio.
        """
        self._settings = settings
        self._stt = speech_to_text
        self._dialogue = dialogue_engine
        self._tts = text_to_speech
        self._reset()

    def ready_message(self) -> Message:
        """Builds the handshake sent once on connect.

        Returns:
            The "ready" message.
        """
        return {
            "type": "ready",
            "playback_sample_rate": self._settings.playback_sample_rate,
            "capture_sample_rate": self._settings.capture_sample_rate,
            "max_turns": self._settings.max_turns,
            "strikes_allowed": self._settings.strikes_allowed,
            "progress_to_resolve": self._settings.progress_to_resolve,
        }

    def handle_command(self, command: dict[str, Any]) -> Iterator[Message]:
        """Handles one JSON command from the client.

        Args:
            command: The parsed command, with "type" and optional "content".

        Yields:
            Messages to send back.
        """
        kind = command.get("type")
        if kind == "begin":
            yield from self._begin()
        elif kind == "text":
            yield from self._take_turn(str(command.get("content") or ""), time.monotonic())
        elif kind == "reset":
            self._reset()
            yield {"type": "reset_complete"}
        elif kind == "summary":
            yield self._summary()
        else:
            yield {"type": "error", "message": f"Unknown command: {kind!r}"}

    def handle_audio(self, data: bytes) -> Iterator[Message]:
        """Handles one complete utterance from the client.

        Args:
            data: float32 little-endian PCM at the capture sample rate.

        Yields:
            Messages to send back.
        """
        started = time.monotonic()
        if self._ended:
            yield {"type": "error", "message": "The scenario has ended."}
            return

        samples = np.frombuffer(data, dtype="<f4")
        text = self._stt.transcribe(samples, self._settings.capture_sample_rate).strip()
        if not text:
            yield {"type": "discarded", "reason": "Nothing intelligible was heard."}
            return

        yield {"type": "transcript", "text": text}
        yield from self._take_turn(text, started)

    def _reset(self) -> None:
        """Clears all state for a fresh attempt."""
        self._history: list[dict[str, str]] = []
        self._log: list[dict[str, str]] = []
        self._turn = 0
        self._strikes = 0
        self._progress = 0
        self._ended = False
        self._outcome: str | None = None

    def _begin(self) -> Iterator[Message]:
        """Starts a fresh attempt with the parent's opening line.

        Yields:
            Messages to send back.
        """
        self._reset()
        line = self._settings.opening_line
        self._history.append({"role": "assistant", "content": line})
        yield {"type": "reply", "text": line}
        yield from self._speak(line)
        yield {"type": "turn_end", "ended": False}

    def _take_turn(self, text: str, started: float) -> Iterator[Message]:
        """Runs one trainee turn through the dialogue engine and the rules.

        Args:
            text: What the trainee said.
            started: time.monotonic() when the turn arrived, for latency.

        Yields:
            Messages to send back.
        """
        if self._ended:
            yield {"type": "error", "message": "The scenario has ended."}
            return
        if not text.strip():
            yield {"type": "discarded", "reason": "Empty line."}
            return

        self._history.append({"role": "user", "content": text})
        reply = self._dialogue.reply(self._history)
        self._history.append({"role": "assistant", "content": reply.text})
        self._log.append({
            "trainee": text,
            "parent": reply.text,
            "verdict": reply.verdict,
            "emotion": reply.emotion,
        })

        yield {"type": "tags", "verdict": reply.verdict, "emotion": reply.emotion}
        yield {"type": "reply", "text": reply.text}

        self._turn += 1
        if reply.verdict == "neutral":
            self._strikes += 1
        elif reply.verdict == "deescalated":
            self._progress += 1
        yield {
            "type": "turn_complete",
            "turn": self._turn,
            "verdict": reply.verdict,
            "latency": round(time.monotonic() - started, 2),
        }

        self._outcome = self._check_outcome()
        if self._outcome:
            self._ended = True
            yield {"type": "ending", "outcome": self._outcome}
        else:
            yield from self._speak(reply.text)
        yield {"type": "turn_end", "ended": self._ended}

    def _check_outcome(self) -> str | None:
        """Applies the scenario rules to the current counters.

        Returns:
            "disengaged", "resolved", or None if the scenario continues.
        """
        if self._strikes >= self._settings.strikes_allowed:
            return "disengaged"
        if self._progress >= self._settings.progress_to_resolve:
            return "resolved"
        if self._turn >= self._settings.max_turns:
            return "disengaged"
        return None

    def _speak(self, text: str) -> Iterator[Message]:
        """Synthesises a line into binary audio frames.

        Args:
            text: The line to speak.

        Yields:
            One float32 little-endian frame per synthesised chunk.
        """
        for chunk in self._tts.synthesise(text):
            yield chunk.astype("<f4").tobytes()

    def _summary(self) -> Message:
        """Builds the debrief data.

        Returns:
            The "summary" message.
        """
        return {
            "type": "summary",
            "turns": self._log,
            "strikes": self._strikes,
            "progress": self._progress,
            "outcome": self._outcome,
        }
