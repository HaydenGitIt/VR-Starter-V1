"""Plays conversations and appends each to <data>/conversations/<worker>.jsonl.

Usage, from the server folder (one process per teacher model, in parallel if you like):

    .venv/Scripts/python -m datagen.generate --teacher llama3 --conversations 100
    .venv/Scripts/python -m datagen.generate --teacher gemini-flash --conversations 100
    .venv/Scripts/python -m datagen.generate --teacher haiku-5-5 --conversations 100

Each process writes its own file, named after --worker (default: the teacher preset), and
skips ids it has already written, so running it again resumes. Mel defaults to the game's
own local llama3:8b. Ollama answers one request at a time, so local Mel sets the pace
(about 4.5 s per reply on the dev PC) however many processes you run.
"""

import argparse
import pathlib
from typing import Any

from datagen import conversation, providers, roles, store


def run(teacher_role: dict[str, Any], parent_role: dict[str, Any], n: int, *, worker: str,
        data_dir: pathlib.Path = store.DEFAULT_DATA_DIR, concurrency: int = 4) -> int:
    """Generates a worker's conversations until its first n ids are all written.

    Args:
        teacher_role: The staff member's role.
        parent_role: Mel's role.
        n: How many conversations this worker should hold.
        worker: Names the output file and prefixes conversation ids.
        data_dir: The data folder.
        concurrency: Conversations in flight at once when the teacher uses an API. A local
            teacher runs one at a time: its calls would only queue behind Mel's on the GPU.

    Returns:
        How many conversations were written this run.
    """
    providers.check_roles({"teacher": teacher_role, "parent": parent_role})
    output = store.conversations_file(data_dir, worker)
    done = {r["id"] for r in store.read_jsonl(output)}
    todo = [cid for i in range(n) if (cid := conversation.conversation_id(worker, i)) not in done]
    print(f"[{worker}] teacher {providers.name(teacher_role)}, parent {providers.name(parent_role)}; "
          f"{len(done)} already in {output}")
    return store.run_jobs(
        lambda cid: conversation.run_conversation(cid, teacher_role, parent_role), todo,
        concurrency=1 if teacher_role["provider"] == "ollama" else concurrency,
        output=output, failures=store.failures_file(data_dir, f"generate-{worker}"),
        label=worker, key=lambda cid: {"id": cid},
        describe=lambda r: (f"{r['id']} {r['profile']:22} {r['outcome']:10} "
                            f"{r['turns_used']} turns {r['duration_s']:6.1f}s"))


def main(argv: list[str] | None = None) -> None:
    """Parses the command line and generates conversations.

    Args:
        argv: Arguments, or None for sys.argv.
    """
    parser = argparse.ArgumentParser(description="Generate synthetic conversations.")
    parser.add_argument("--conversations", type=int, required=True,
                        help="how many conversations this worker should hold in total")
    parser.add_argument("--teacher", default="llama3",
                        help=f"teacher preset (default llama3). One of: {', '.join(roles.PRESETS)}")
    parser.add_argument("--parent", default="mel-llama3",
                        help="Mel's preset (default mel-llama3, the game's own Mel)")
    parser.add_argument("--worker", help="output file name and id prefix (default: the teacher "
                                         "preset); give each parallel process its own")
    parser.add_argument("--concurrency", type=int, default=4,
                        help="conversations in flight at once with an API teacher (default 4)")
    parser.add_argument("--data", type=pathlib.Path, default=store.DEFAULT_DATA_DIR,
                        help=f"data folder (default {store.DEFAULT_DATA_DIR})")
    args = parser.parse_args(argv)
    run(roles.preset(args.teacher), roles.preset(args.parent), args.conversations,
        worker=args.worker or args.teacher, data_dir=args.data, concurrency=args.concurrency)


if __name__ == "__main__":
    main()
