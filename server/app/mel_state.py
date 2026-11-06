"""Tracks Mel's state: where she is between agitated and calm, and the strikes so far.

Both are decided here from each verdict, never by a model. Whoever judges the trainee
(Laya, or Mel's own model with director.engine: none) only supplies the verdict.

The levels come from scenario.levels in config.yaml, listed from where she starts to where
she resolves. With six entries she starts at level 5 and resolves at level 0.
"""

import enum
from collections.abc import Sequence


class Verdict(enum.StrEnum):
    """What the trainee's line did to Mel."""

    DEESCALATED = "deescalated"
    NEUTRAL = "neutral"
    ESCALATED = "escalated"


class Emotion(enum.StrEnum):
    """How Mel feels, matching ParentEmotion in Unity's ConversationServices.cs."""

    AGITATED = "agitated"
    DISTRESSED = "distressed"
    SOFTENING = "softening"
    CALM = "calm"


class Outcome(enum.StrEnum):
    """How the scenario ended."""

    RESOLVED = "resolved"
    DISENGAGED = "disengaged"


class StateTracker:
    """Mel's level and strikes for one attempt at the scenario."""

    def __init__(self, levels: Sequence[str], strikes_allowed: int) -> None:
        """Starts Mel at her first level with no strikes.

        Args:
            levels: Emotion names, from where she starts to where she resolves.
            strikes_allowed: Strikes at which she disengages.
        """
        self._levels = [Emotion(name) for name in levels]
        self._strikes_allowed = strikes_allowed
        self._start = len(self._levels) - 1
        self._level = self._start
        self._strikes = 0

    @property
    def level(self) -> int:
        """Steps left until she resolves: the highest level at the start, 0 when resolved."""
        return self._level

    @property
    def emotion(self) -> Emotion:
        """How she feels at her current level."""
        return self._levels[self._start - self._level]

    @property
    def strikes(self) -> int:
        """Turns that failed to reach her."""
        return self._strikes

    @property
    def progress(self) -> int:
        """Steps taken from her starting level."""
        return self._start - self._level

    @property
    def outcome(self) -> Outcome | None:
        """The ending she has reached, or None while the scenario continues."""
        if self._level == 0:
            return Outcome.RESOLVED
        if self._strikes >= self._strikes_allowed:
            return Outcome.DISENGAGED
        return None

    def apply(self, verdict: str) -> None:
        """Moves Mel according to one verdict.

        Deescalated moves her one level towards resolved. Neutral holds her and adds a
        strike. Escalated moves her one level back, stopping at her starting level, and
        adds a strike.

        Args:
            verdict: A Verdict value.
        """
        verdict = Verdict(verdict)
        if verdict == Verdict.DEESCALATED:
            self._level -= 1
            return
        self._strikes += 1
        if verdict == Verdict.ESCALATED:
            self._level = min(self._level + 1, self._start)
