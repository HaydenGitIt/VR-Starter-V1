"""Grades every teacher line with one or more graders, at the same time.

Usage, from the server folder:

    .venv/Scripts/python -m datagen.grade --graders haiku-5-5-grader gemini-flash-grader llama3-grader
    .venv/Scripts/python -m datagen.grade --graders haiku-5-5-grader --limit 12
    .venv/Scripts/python -m datagen.grade --graders gemini-flash-grader --sanity

Each grader runs in its own thread and writes <data>/grades/<grader model>.jsonl. Lines
it has already graded are skipped, so running it again resumes, and picks up lines from
conversations generated since. --sanity grades check_dialogue.py's 23 known cases instead,
to vet a grader before a long run.
"""

import argparse
import concurrent.futures
import pathlib
from typing import Any

from datagen import grading, providers, roles, store


def grade_with(role: dict[str, Any], data_dir: pathlib.Path, concurrency: int = 4,
               limit: int | None = None) -> int:
    """Grades every teacher line this grader hasn't graded yet.

    Args:
        role: The grader's role.
        data_dir: The data folder.
        concurrency: Lines graded at once by an API grader; a local grader does one at a time.
        limit: Grade at most this many lines this run, or None for all.

    Returns:
        How many lines were graded this run.
    """
    model = providers.name(role)
    output = store.grades_file(data_dir, model)
    done = {(g["conversation_id"], g["turn"]) for g in store.read_jsonl(output)}
    jobs = [(record, i) for record in store.read_conversations(data_dir)
            for i, t in enumerate(record["turns"]) if (record["id"], t["turn"]) not in done]
    return store.run_jobs(
        lambda job: grading.grade_turn(role, *job), jobs[:limit],
        concurrency=1 if role["provider"] == "ollama" else concurrency,
        output=output, failures=store.failures_file(data_dir, f"grade-{model}"),
        label=model, key=lambda job: {"id": job[0]["id"], "turn": job[1] + 1},
        describe=lambda g: (f"{g['conversation_id']} turn {g['turn']}: {g['score']}/4 "
                            f"{g['verdict']} [{g['category']}]"))


def run(grader_roles: list[dict[str, Any]], data_dir: pathlib.Path = store.DEFAULT_DATA_DIR,
        concurrency: int = 4, limit: int | None = None) -> None:
    """Runs several graders at the same time, one thread each.

    Args:
        grader_roles: The graders' roles.
        data_dir: The data folder.
        concurrency: Lines in flight at once per API grader.
        limit: Grade at most this many lines per grader this run, or None for all.
    """
    for role in grader_roles:
        providers.check_roles({"grader": role})
    with concurrent.futures.ThreadPoolExecutor(len(grader_roles)) as pool:
        futures = [pool.submit(grade_with, role, data_dir, concurrency, limit) for role in grader_roles]
        for future in futures:
            future.result()


def main(argv: list[str] | None = None) -> None:
    """Parses the command line and grades.

    Args:
        argv: Arguments, or None for sys.argv.
    """
    parser = argparse.ArgumentParser(description="Grade every teacher line.")
    parser.add_argument("--graders", nargs="+", required=True, metavar="PRESET",
                        help=f"grader presets. Any of: {', '.join(roles.PRESETS)}")
    parser.add_argument("--limit", type=int, help="grade at most this many lines per grader")
    parser.add_argument("--concurrency", type=int, default=4,
                        help="lines in flight at once per API grader (default 4)")
    parser.add_argument("--sanity", action="store_true",
                        help="grade the 23 known cases instead, to vet each grader")
    parser.add_argument("--data", type=pathlib.Path, default=store.DEFAULT_DATA_DIR,
                        help=f"data folder (default {store.DEFAULT_DATA_DIR})")
    args = parser.parse_args(argv)
    grader_roles = [roles.preset(g) for g in args.graders]
    if args.sanity:
        for role in grader_roles:
            grading.sanity_check(role)
        return
    run(grader_roles, args.data, args.concurrency, args.limit)


if __name__ == "__main__":
    main()
