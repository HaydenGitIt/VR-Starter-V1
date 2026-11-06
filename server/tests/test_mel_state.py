"""Tests for Mel's state tracker."""

import dataclasses

import pytest

from app import config, mel_state
from app.engines import director

_LEVELS = ["agitated", "agitated", "distressed", "distressed", "softening", "calm"]


def _tracker(*verdicts: str) -> mel_state.StateTracker:
    """Creates a tracker and applies some verdicts.

    Args:
        *verdicts: The verdicts, in order.

    Returns:
        The tracker afterwards.
    """
    tracker = mel_state.StateTracker(_LEVELS, strikes_allowed=3)
    for verdict in verdicts:
        tracker.apply(verdict)
    return tracker


def test_starts_at_the_top_level_with_no_strikes() -> None:
    """Six levels start at 5, on the first emotion."""
    tracker = _tracker()
    assert (tracker.level, tracker.emotion, tracker.strikes, tracker.outcome) == (
        5, "agitated", 0, None)


def test_each_deescalation_steps_through_the_levels() -> None:
    """The emotion follows from the count alone, and level 0 resolves."""
    tracker = _tracker()
    seen = []
    for _ in _LEVELS[1:]:
        tracker.apply("deescalated")
        seen.append((tracker.level, tracker.emotion.value))
    assert seen == [(4, "agitated"), (3, "distressed"), (2, "distressed"),
                    (1, "softening"), (0, "calm")]
    assert tracker.outcome == mel_state.Outcome.RESOLVED


def test_deescalated_never_adds_a_strike() -> None:
    """Only failed turns are strikes."""
    assert _tracker("deescalated", "deescalated").strikes == 0


def test_neutral_holds_her_and_adds_a_strike() -> None:
    """A neutral line does not move her."""
    tracker = _tracker("deescalated", "neutral")
    assert (tracker.level, tracker.strikes) == (4, 1)


def test_escalated_moves_her_back_and_adds_a_strike() -> None:
    """An escalating line undoes one step."""
    tracker = _tracker("deescalated", "deescalated", "escalated")
    assert (tracker.level, tracker.emotion, tracker.strikes) == (4, "agitated", 1)


def test_escalated_stops_at_her_starting_level() -> None:
    """She has no state above where she starts."""
    tracker = _tracker("escalated")
    assert (tracker.level, tracker.progress, tracker.strikes) == (5, 0, 1)


def test_strikes_allowed_disengages_her() -> None:
    """Neutral and escalated strikes count towards the same limit."""
    assert _tracker("neutral", "escalated").outcome is None
    assert _tracker("neutral", "escalated", "neutral").outcome == mel_state.Outcome.DISENGAGED


def test_unknown_verdict_is_rejected() -> None:
    """Anything that is not a Verdict is a bug, not a turn to score."""
    with pytest.raises(ValueError):
        _tracker("unparsed")


def test_levels_are_read_from_the_config() -> None:
    """The config sets the ladder, and everything else follows from it."""
    rules = config.load(["scenario.levels=[agitated, softening, calm]"]).scenario
    assert (rules.steps_to_resolve, rules.opening_emotion) == (2, "agitated")


def test_config_rejects_a_level_that_is_not_an_emotion() -> None:
    """A typo stops the server at startup rather than reaching Unity."""
    rules = config.load().scenario
    with pytest.raises(ValueError):
        dataclasses.replace(rules, levels=["agitated", "furious"])


def test_every_emotion_has_a_note_for_mel() -> None:
    """A new Emotion must come with its note in prompts/director.yaml."""
    assert set(director.STATE_NOTES) == set(mel_state.Emotion)


def test_every_verdict_has_a_note_for_mel() -> None:
    """A director could return any Verdict."""
    assert set(director.NOTES) == set(mel_state.Verdict)
