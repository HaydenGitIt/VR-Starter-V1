"""Exports graded lines in Laya's training format: {"state", "questions", "gold"}.

One row per graded teacher line:
- state: Mel's last line, then the staff member's previous and current line as
  transcribed. This is exactly what the server's director will see in the game.
- questions: the two noul questions, reached_her and repeated.
- gold: the share of graders that answered true, shaped like Laya's own typed-decisions
  data (label, noul, probabilities, confidence). Graders' disagreements give values
  between 0 and 1, which Laya's training uses.

Each grade is one vote, unless its verdict contradicts its category. Conversations go to
director_train.jsonl or director_test.jsonl by a hash of their id, so every line of a
conversation lands on the same side, the same way on every run. director_index.jsonl
gives each row's split, conversation, turn and votes, in the same order as the rows.

Usage, from the server folder:
    .venv/Scripts/python -m datagen.export [--graders haiku-5-5-grader ...] [--test-share 0.2]
"""

import argparse
import collections
import hashlib
import json
import pathlib
import statistics
from collections.abc import Iterator
from typing import Any

from datagen import providers, roles, store
from datagen.game import RULES

DIRECTOR_QUESTIONS = {
    "reached_her": {
        "type": "noul",
        "instructions": "A parent is upset about an $80/week childcare fee rise and an email "
                        "nobody answered. Did the staff member's latest line respond to how she "
                        "feels, rather than to the problem, the policy, the paperwork, or her "
                        "behaviour?",
        "criteria": {
            "true": "Acknowledges or reflects her feelings, agrees her complaint is fair "
                    "('That's not good enough'), owns a failure, gives her the floor, stays "
                    "present, takes her anger without defending, names what it costs her, is "
                    "warmly honest about limits, or offers to sit down. Short lines count.",
            "false": "Bare filler ('Okay', 'Right'), claims to understand ('I understand'), "
                     "explains the increase or points to the email, looks things up, promises a "
                     "reversal, tells her to calm down, compares her to other families, or is "
                     "garbled."}},
    "repeated": {
        "type": "noul",
        "instructions": "Is the staff member's latest line essentially the same acknowledgement "
                        "as their previous line, without moving anywhere?"},
}


def soft_gold(p: float) -> dict[str, Any]:
    """Writes a vote share as a noul answer, shaped like Laya's typed-decisions data.

    Args:
        p: Share of votes for true, from 0 to 1.

    Returns:
        The gold answer. An exact tie is labelled false: not shown to reach her.
    """
    p = round(p, 6)
    return {"label": "true" if p > 0.5 else "false", "noul": p,
            "probabilities": {"false": round(1 - p, 6), "true": p},
            "confidence": round(abs(2 * p - 1), 6)}


def is_test(conversation_id: str, test_share: float) -> bool:
    """Puts a conversation in the test split, or not, the same way on every run.

    Args:
        conversation_id: The conversation's id.
        test_share: Share of conversations held out for testing.

    Returns:
        True for the test split.
    """
    digest = hashlib.sha256(conversation_id.encode()).digest()
    return int.from_bytes(digest[:4], "big") / 2**32 < test_share


def director_rows(conversations: list[dict[str, Any]], grades: list[dict[str, Any]],
                  label_graders: list[str] | None,
                  test_share: float) -> Iterator[tuple[dict[str, Any], dict[str, Any]]]:
    """Builds one row per teacher line that has at least one usable grade.

    Args:
        conversations: Conversation records.
        grades: Grade records from any number of graders.
        label_graders: Grader model names that vote, or None for all of them.
        test_share: Share of conversations held out for testing.

    Yields:
        (Laya row, index entry) pairs; the index entry names the row's split.
    """
    votes: dict[tuple[str, int], list[dict[str, Any]]] = collections.defaultdict(list)
    for grade in grades:
        if grade.get("consistent", True) and (label_graders is None
                                              or grade["grader_model"] in label_graders):
            votes[(grade["conversation_id"], grade["turn"])].append(grade)
    for record in conversations:
        split = "test" if is_test(record["id"], test_share) else "train"
        mel, previous = RULES.opening_line, ""
        for t in record["turns"]:
            if line_grades := votes.get((record["id"], t["turn"])):
                reached = [g["verdict"] == "deescalated" for g in line_grades]
                repeated = [g["category"] == "repeated" for g in line_grades]
                yield ({"state": {"parent_said": mel, "staff_previously_said": previous,
                                  "staff_now_says": t["transcript"]},
                        "questions": DIRECTOR_QUESTIONS,
                        "gold": {"reached_her": soft_gold(sum(reached) / len(reached)),
                                 "repeated": soft_gold(sum(repeated) / len(repeated))}},
                       {"split": split, "conversation_id": record["id"], "turn": t["turn"],
                        "graders": [g["grader_model"] for g in line_grades],
                        "reached_her_votes": reached})
            mel, previous = t["mel_line"], t["transcript"]


def _write_jsonl(path: pathlib.Path, rows: list[dict[str, Any]]) -> None:
    """Writes rows as a JSON Lines file, replacing any earlier export.

    Args:
        path: The file.
        rows: The rows.
    """
    path.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows), encoding="utf-8")


def write(data_dir: pathlib.Path, label_graders: list[str] | None = None,
          test_share: float = 0.2) -> list[tuple[dict[str, Any], dict[str, Any]]]:
    """Writes director_train.jsonl, director_test.jsonl and director_index.jsonl.

    Args:
        data_dir: The data folder.
        label_graders: Grader model names that vote, or None for all of them.
        test_share: Share of conversations held out for testing.

    Returns:
        The (row, index entry) pairs written.
    """
    pairs = list(director_rows(store.read_conversations(data_dir), store.read_grades(data_dir),
                               label_graders, test_share))
    for split in ("train", "test"):
        rows = [row for row, entry in pairs if entry["split"] == split]
        _write_jsonl(data_dir / f"director_{split}.jsonl", rows)
        p = [row["gold"]["reached_her"]["noul"] for row in rows]
        conversations = {e["conversation_id"] for _, e in pairs if e["split"] == split}
        print(f"{split:5} {len(rows):5} rows from {len(conversations):4} conversations"
              + (f"; reached_her mean {statistics.fmean(p):.2f}, label true for "
                 f"{sum(x > 0.5 for x in p)}" if p else ""))
    _write_jsonl(data_dir / "director_index.jsonl", [e for _, e in pairs])
    graders_per_line = collections.Counter(len(e["graders"]) for _, e in pairs)
    disagreed = sum(0 < sum(e["reached_her_votes"]) < len(e["reached_her_votes"]) for _, e in pairs)
    print(f"graders per line {dict(sorted(graders_per_line.items()))}; "
          f"graders disagreed on {disagreed} lines")
    return pairs


def main(argv: list[str] | None = None) -> None:
    """Parses the command line and writes the export.

    Args:
        argv: Arguments, or None for sys.argv.
    """
    parser = argparse.ArgumentParser(description="Export graded lines for Laya training.")
    parser.add_argument("--graders", nargs="+", metavar="GRADER",
                        help="graders that vote: preset names or provider/model names "
                             "(default: every grader in the data)")
    parser.add_argument("--test-share", type=float, default=0.2,
                        help="share of conversations held out for testing (default 0.2)")
    parser.add_argument("--data", type=pathlib.Path, default=store.DEFAULT_DATA_DIR,
                        help=f"data folder (default {store.DEFAULT_DATA_DIR})")
    args = parser.parse_args(argv)
    graders = None if not args.graders else [
        providers.name(roles.preset(g)) if g in roles.PRESETS else g for g in args.graders]
    write(args.data, graders, args.test_share)


if __name__ == "__main__":
    main()
