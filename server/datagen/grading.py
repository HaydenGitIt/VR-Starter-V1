"""Grading: every teacher line judged from the conversation so far, never Mel's reaction.

The grader gets Mel's opening and the earlier exchanges (Mel's lines without tags, staff
lines as transcribed), then the line to grade. It never sees Mel's reply to that line,
her in-game tags or the teacher's profile. The prompt (prompts/grader_prompt.txt) is
Mel's rubric rewritten for an observer, with the repetition rule and 0-4 score anchors.
It asks for {"reason", "category", "verdict", "score"}, reason first so the model commits
to an explanation before the verdict.
"""

import hashlib
import json
import pathlib
import re
import statistics
from typing import Any

from datagen import providers
from datagen.game import RULES

GRADER_PROMPT = (pathlib.Path(__file__).resolve().parent / "prompts" / "grader_prompt.txt"
                 ).read_text(encoding="utf-8")
GRADER_HASH = hashlib.sha256(GRADER_PROMPT.encode()).hexdigest()[:12]
DEESCALATING = ["reflecting", "acknowledging", "validating", "owning_failure", "giving_floor",
                "staying_present", "taking_anger", "naming_cost", "honest_limits",
                "lowering_temperature"]
NOT_DEESCALATING = ["filler", "claimed_understanding", "problem_focused", "promising",
                    "behaviour_focused", "deflecting", "repeated", "garbled", "other_neutral"]
CATEGORIES = DEESCALATING + NOT_DEESCALATING
assert all(f"\n{c}:" in GRADER_PROMPT for c in CATEGORIES), "prompt and CATEGORIES disagree"
GRADE_SCHEMA = {
    "type": "object",
    "properties": {
        "reason": {"type": "string"},
        "category": {"type": "string", "enum": CATEGORIES},
        "verdict": {"type": "string", "enum": ["deescalated", "neutral"]},
        "score": {"type": "integer", "enum": [0, 1, 2, 3, 4]},
    },
    "required": ["reason", "category", "verdict", "score"],
    "additionalProperties": False,
}


def grader_input(staff_lines: list[str], mel_lines: list[str]) -> str:
    """Formats the conversation up to the line being graded.

    Args:
        staff_lines: Staff lines as heard, the last one being graded.
        mel_lines: Mel's spoken lines, opening first; one fewer than staff_lines.

    Returns:
        The grader's user message.
    """
    rows = []
    for mel, staff in zip(mel_lines, staff_lines[:-1]):
        rows += [f"MEL: {mel or '(says nothing)'}", f"STAFF: {staff}"]
    rows.append(f"MEL: {mel_lines[len(staff_lines) - 1] or '(says nothing)'}")
    return "CONVERSATION SO FAR\n" + "\n".join(rows) + f"\n\nLINE TO GRADE\nSTAFF: {staff_lines[-1]}"


def parse_grade(text: str) -> dict[str, Any] | None:
    """Parses and validates the grader's JSON.

    Args:
        text: The grader's reply.

    Returns:
        The grade fields, or None if the reply is not a valid grade.
    """
    match = re.search(r"\{.*\}", text, re.DOTALL)
    try:
        data = json.loads(match.group(0)) if match else None
    except json.JSONDecodeError:
        return None
    if not isinstance(data, dict):
        return None
    score = data.get("score")
    valid = (isinstance(score, int) and not isinstance(score, bool) and 0 <= score <= 4
             and data.get("verdict") in ("deescalated", "neutral")
             and data.get("category") in CATEGORIES
             and isinstance(data.get("reason"), str) and data["reason"].strip())
    return {k: data[k] for k in ("score", "verdict", "category", "reason")} if valid else None


def grade_text(role: dict[str, Any], user_message: str) -> dict[str, Any]:
    """Grades one line, retrying once on invalid output.

    Args:
        role: The grader's role.
        user_message: From grader_input().

    Returns:
        The grade plus a verdict/category consistency flag, attempts, latency and usage.

    Raises:
        ValueError: If both attempts were invalid.
    """
    raws, latency = [], 0.0
    for attempt in (1, 2):
        result = providers.complete(role, GRADER_PROMPT,
                                    [{"role": "user", "content": user_message}], GRADE_SCHEMA)
        raws.append(result.text)
        latency += result.latency_s
        if grade := parse_grade(result.text):
            # Every category belongs to one verdict; a mismatch is kept but flagged.
            consistent = (grade["category"] in DEESCALATING) == (grade["verdict"] == "deescalated")
            return {**grade, "consistent": consistent, "attempts": attempt,
                    "grade_s": round(latency, 3), "usage": result.usage}
    raise ValueError(f"Invalid grader output twice: {raws}")


def grade_turn(role: dict[str, Any], record: dict[str, Any], index: int) -> dict[str, Any]:
    """Grades one teacher turn of a conversation.

    Args:
        role: The grader's role.
        record: A conversation record.
        index: The 0-based turn index.

    Returns:
        The grade record.
    """
    turns = record["turns"]
    staff = [t["transcript"] for t in turns[:index + 1]]
    mel = [RULES.opening_line] + [t["mel_line"] for t in turns[:index]]
    grade = grade_text(role, grader_input(staff, mel))
    return {"conversation_id": record["id"], "turn": turns[index]["turn"],
            "grader_model": providers.name(role), "grader_prompt_hash": GRADER_HASH,
            "parent_verdict": turns[index]["verdict"], **grade}


def sanity_check(role: dict[str, Any]) -> list[dict[str, Any]]:
    """Grades check_dialogue.py's 23 known cases, each as the first reply to Mel's opening.

    Disagreements mean the grader (or the expectation) is wrong.

    Args:
        role: The grader's role.

    Returns:
        One result per case: the line, the expected verdict, and the grade.
    """
    import check_dialogue  # pylint: disable=import-outside-toplevel  (server/check_dialogue.py)

    results = []
    for line, expected in check_dialogue.CASES:
        try:
            grade = grade_text(role, grader_input([line], [RULES.opening_line]))
        except ValueError:
            grade = {"verdict": "invalid", "score": "-", "category": "-", "grade_s": 0.0}
        results.append({"line": line, "expected": expected, **grade})
        mark = "ok  " if grade["verdict"] == expected else f"WANTED {expected[:4]}"
        print(f"{mark:11} {grade['verdict']:>11} {grade['score']} {grade['category']:21} {line!r}")
    agreed = sum(r["verdict"] == r["expected"] for r in results)
    print(f"\n{providers.name(role)}: {agreed}/{len(results)} verdicts as expected; "
          f"{sum(not r.get('consistent', True) for r in results)} contradict their category; "
          f"median {statistics.median(r['grade_s'] for r in results):.2f}s per grade")
    return results
