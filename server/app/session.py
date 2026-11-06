"""Per-connection scenario state: history, counters and the two endings.

Ported from the original DeescalationServer's conversation.py.

Each handler is a generator of outgoing messages: a dict is sent as a JSON
text frame, bytes as a binary audio frame. Handlers are synchronous because
the engines block; the server runs them off the event loop.

A turn: transcript -> tags -> [ending] -> audio... -> reply -> turn_complete
-> [ending if out of turns] -> turn_end. Tags arrive before any dialogue
exists, so a third strike is known before anything is synthesised and the
cut to the failure Timeline has no speech to interrupt.
"""

import contextlib
import re
import time
from collections.abc import Iterable, Iterator
from typing import Any

import numpy as np

from app import config, mel_state
from app.engines import dialogue, director, stt, tts

Message = dict[str, Any] | bytes

_SENTENCE_END = re.compile(r"([.!?])(\s+|$)")


def audio_frame(audio: np.ndarray) -> bytes:
    """Packs audio as a float32 little-endian frame Unity reads as audio.

    Unity treats any frame starting with "{" (0x7B) as JSON. The first byte
    is the least significant byte of the first sample, so about 1 frame in
    256 would start with it and be dropped as unparseable JSON. Flipping
    that byte's lowest bit changes the sample by ~1e-7, which is inaudible.

    Args:
        audio: Mono float32 samples.

    Returns:
        The frame, never starting with "{".
    """
    data = bytearray(audio.astype("<f4").tobytes())
    if data[:1] == b"{":
        data[0] ^= 0x01
    return bytes(data)


class ScenarioSession:
    """One trainee's attempt at the scenario."""

    def __init__(
        self,
        settings: config.Settings,
        speech_to_text: stt.SpeechToText,
        dialogue_engine: dialogue.Dialogue,
        text_to_speech: tts.TextToSpeech,
        opening_audio: np.ndarray | None = None,
        director_engine: director.Director | None = None,
    ) -> None:
        """Initialises an empty session.

        Args:
            settings: Server settings, including the scenario rules.
            speech_to_text: Engine for incoming audio.
            dialogue_engine: Engine that writes Mel's replies.
            text_to_speech: Engine for outgoing audio.
            opening_audio: The opening line, synthesised in advance. If
                None it is synthesised when the scenario begins.
            director_engine: Engine that judges the trainee's lines, or None
                for Mel's own tags to decide.
        """
        self._settings = settings
        self._rules = settings.scenario
        self._stt = speech_to_text
        self._dialogue = dialogue_engine
        self._tts = text_to_speech
        self._opening_audio = opening_audio
        self._director = director_engine or director.NoDirector()
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
            "max_turns": self._rules.max_turns,
            "strikes_allowed": self._rules.strikes_allowed,
            "progress_to_resolve": self._rules.steps_to_resolve,
        }

    @property
    def ended(self) -> bool:
        """True once the scenario has reached an ending."""
        return self._outcome is not None

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
        if self._outcome:
            yield {"type": "error", "message": "The scenario has already ended."}
            return

        samples = np.frombuffer(data, dtype="<f4")
        text = self._stt.transcribe(samples, self._settings.capture_sample_rate).strip()
        if text:
            yield {"type": "transcript", "text": text}
        yield from self._take_turn(text, started)

    def _reset(self) -> None:
        """Clears all state for a fresh attempt."""
        self._history: list[dict[str, str]] = []
        self._log: list[dict[str, Any]] = []
        self._turn = 0
        self._tracker = mel_state.StateTracker(self._rules.levels, self._rules.strikes_allowed)
        self._unparsed = 0
        self._outcome: str | None = None

    def _begin(self) -> Iterator[Message]:
        """Starts a fresh attempt with Mel's fixed opening line.

        Yields:
            Messages to send back.
        """
        self._reset()
        rules = self._rules
        self._history.append({"role": "user", "content": rules.start_token})
        self._remember(rules.opening_verdict, rules.opening_line)

        emotion = self._tracker.emotion
        if self._opening_audio is None:
            self._opening_audio = self._tts.synthesise(rules.opening_line, emotion)
        yield {"type": "tags", "verdict": rules.opening_verdict, "emotion": emotion}
        yield audio_frame(self._opening_audio)
        yield {"type": "reply", "text": rules.opening_line}
        yield {"type": "turn_end", "ended": False}

    def _take_turn(self, text: str, started: float) -> Iterator[Message]:
        """Runs one trainee turn: generate, score, speak, record.

        Args:
            text: What the trainee said.
            started: time.monotonic() when the turn arrived, for latency.

        Yields:
            Messages to send back.
        """
        if self._outcome:
            yield {"type": "error", "message": "The scenario has already ended."}
            return
        # An empty line must never reach the model: given one, it writes the
        # staff member's next line instead. Nothing said, so no turn used.
        if not text.strip():
            yield {"type": "discarded", "reason": "Nothing intelligible was heard."}
            return

        self._history.append({"role": "user", "content": text})
        judgement = self._director.judge(self._history)
        if judgement:
            # Before she speaks, so her line and her voice match where this leaves her.
            self._tracker.apply(judgement.verdict)
        if judgement and self._tracker.outcome and not self._speaks_ending(self._tracker.outcome):
            # Decided before any model is called: the ending has nothing for Mel to say.
            reply = dialogue.Reply(judgement.verdict, (piece for piece in ()))
        else:
            reply = self._generate(judgement)
        self._turn += 1

        if reply is None:
            # A generation fault uses a turn (so faults cannot run the
            # scenario past max_turns) but is never a strike.
            self._unparsed += 1
            verdict, line = "unparsed", self._rules.fallback_line
            emotion = self._tracker.emotion
            yield {"type": "tags", "verdict": self._rules.fallback_verdict, "emotion": emotion}
            yield from self._speak([line], [])
            self._remember(self._rules.fallback_verdict, line)
        else:
            verdict = judgement.verdict if judgement else reply.verdict
            if not judgement:
                self._tracker.apply(verdict)
            emotion = self._tracker.emotion
            yield {"type": "tags", "verdict": verdict, "emotion": emotion}
            speak = True
            if outcome := self._tracker.outcome:
                self._outcome = outcome
                yield {"type": "ending", "outcome": outcome}
                speak = self._speaks_ending(outcome)
            # Closing the stream stops generation, so no stray audio can
            # arrive after an ending Timeline has started.
            spoken: list[str] = []
            with contextlib.closing(reply.text):
                if speak:
                    yield from self._speak(self._spoken_sentences(reply.text), spoken)
            line = " ".join(spoken)
            self._remember(verdict, line)

        yield {"type": "reply", "text": line}
        latency = round(time.monotonic() - started, 3)
        self._log.append({"turn": self._turn, "trainee": text, "mel": line,
                          "verdict": verdict, "emotion": emotion, "latency": latency,
                          "director_p": round(judgement.probability, 3) if judgement else None})
        yield {"type": "turn_complete", "turn": self._turn, "verdict": verdict,
               "emotion": emotion, "latency": latency, "level": self._tracker.level,
               "strikes": self._tracker.strikes, "progress": self._tracker.progress}

        # Running out of turns is failing to reach her.
        if not self._outcome and self._turn >= self._rules.max_turns:
            self._outcome = "disengaged"
            yield {"type": "ending", "outcome": "disengaged"}
        yield {"type": "turn_end", "ended": self.ended}

    def _generate(self, judgement: director.Judgement | None) -> dialogue.Reply | None:
        """Asks the dialogue engine for a reply, retrying malformed ones.

        Args:
            judgement: The director's verdict for Mel to act out, or None.

        Returns:
            The reply, or None if every attempt was malformed, which only happens when her
            own model judges.
        """
        note = director.STATE_NOTES[self._tracker.emotion]
        for _ in range(self._rules.max_retries + 1):
            try:
                return self._dialogue.generate(self._history, note, judgement)
            except dialogue.MalformedReply:
                continue
        return None

    def _speaks_ending(self, outcome: mel_state.Outcome) -> bool:
        """Says whether Mel speaks her last line on an ending.

        Args:
            outcome: The ending reached.

        Returns:
            True if the line is spoken.
        """
        return (self._rules.speak_reply_on_success if outcome == mel_state.Outcome.RESOLVED
                else self._rules.speak_reply_on_failure)

    def _spoken_sentences(self, pieces: Iterator[str]) -> Iterator[str]:
        """Splits streamed text into sentences as each one completes.

        A trailing fragment with no closing punctuation was cut off by the
        token cap and sounds like a fault, so it is dropped unless it is
        all Mel said (or drop_truncated_fragment is off).

        Args:
            pieces: Text as it streams from the model.

        Yields:
            Each complete sentence, in order.
        """
        pending = ""
        said_something = False
        for piece in pieces:
            pending += piece
            last_end = 0
            for match in _SENTENCE_END.finditer(pending):
                if sentence := pending[last_end:match.end(1)].strip():
                    said_something = True
                    yield sentence
                last_end = match.end()
            pending = pending[last_end:]

        leftover = pending.strip()
        if leftover and (not said_something or not self._rules.drop_truncated_fragment):
            yield leftover

    def _speak(self, sentences: Iterable[str], spoken: list[str]) -> Iterator[Message]:
        """Synthesises sentences as they arrive, with a gap between each.

        Args:
            sentences: The sentences to speak.
            spoken: Receives each sentence that produced audio, so the log
                matches what the trainee actually heard.

        Yields:
            float32 little-endian audio frames.
        """
        gap = np.zeros(self._settings.sentence_gap_ms * self._settings.playback_sample_rate // 1000,
                       dtype="<f4").tobytes()
        for sentence in sentences:
            # Her emotion is settled before any of her dialogue, so an
            # engine that can act on it already knows how to say this line.
            audio = self._tts.synthesise(sentence, self._tracker.emotion)
            if audio.size == 0:
                continue
            if spoken:
                yield gap
            spoken.append(sentence)
            yield audio_frame(audio)

    def _remember(self, verdict: str, line: str) -> None:
        """Adds Mel's line to the history, as her model writes it.

        Args:
            verdict: The verdict, which her model only writes when it is the one judging.
            line: What she said.
        """
        judges = isinstance(self._director, director.NoDirector)
        self._history.append({"role": "assistant", "content": f"<{verdict}>{line}" if judges else line})

    def _summary(self) -> Message:
        """Builds the debrief data, in the original server's shape.

        Returns:
            The "summary" message.
        """
        return {
            "type": "summary",
            "outcome": self._outcome or "ongoing",
            "turns": self._turn,
            "strikes": self._tracker.strikes,
            "progress": self._tracker.progress,
            "level": self._tracker.level,
            "unparsed": self._unparsed,
            "log": self._log,
        }
