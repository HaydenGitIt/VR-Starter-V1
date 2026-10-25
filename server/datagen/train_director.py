"""Fine-tunes Laya on the export, so the server's director can judge trainee lines.

Usage, from the server folder, after `python -m datagen.export`:

    .venv/Scripts/python -m datagen.train_director
    .venv/Scripts/python -m datagen.train_director --freeze-encoder   # GPUs under ~8 GB

Trains on director_train.jsonl, reports on director_test.jsonl, and saves the checkpoint
to models/director (gitignored), where the server's laya director looks by default.
Full training needs roughly 8 GB of free GPU memory; --freeze-encoder trains only Laya's
small output head, which fits almost anywhere but learns much less. Stop other GPU users
first (e.g. `ollama stop llama3:8b`).
"""

import argparse
import json
import pathlib
import shutil
from typing import Any

from app.engines import director
from datagen import store

SERVER_DIR = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = SERVER_DIR / "models" / "director"


def evaluate(model_dir: pathlib.Path, rows: list[dict[str, Any]], threshold: float,
             device: str | None) -> None:
    """Reports how often the trained director's verdict matches the graders' on test lines.

    Args:
        model_dir: The trained checkpoint.
        rows: Test rows from the export.
        threshold: P(reached her) needed for "deescalated", as in the server's config.
        device: Where to run the model, or None for the GPU if available.
    """
    engine = director.LayaDirector(str(model_dir), device=device, threshold=threshold)
    engine.warm_up()
    # Lines the graders split on have no right answer, so they're left out of the score.
    clear = [row for row in rows if row["gold"]["reached_her"]["noul"] in (0.0, 1.0)]
    wrong_strikes = wrong_successes = 0
    for row in clear:
        reached = engine.judge_state(row["state"]).verdict == "deescalated"
        expected = row["gold"]["reached_her"]["noul"] == 1.0
        wrong_strikes += expected and not reached
        wrong_successes += reached and not expected
    right = len(clear) - wrong_strikes - wrong_successes
    print(f"\nDirector verdicts on the {len(clear)} test lines the graders agreed on "
          f"({len(rows) - len(clear)} split lines left out): {right} match "
          f"({right / max(len(clear), 1):.0%}). Wrong strikes (good lines counted as misses): "
          f"{wrong_strikes}. Wrong successes (poor lines counted as reaching her): "
          f"{wrong_successes}.")


def main(argv: list[str] | None = None) -> None:
    """Parses the command line, trains, and reports.

    Args:
        argv: Arguments, or None for sys.argv.
    """
    parser = argparse.ArgumentParser(description="Fine-tune Laya as the server's director.")
    parser.add_argument("--base", default="typed-decisions",
                        help="Laya checkpoint to start from (default typed-decisions)")
    parser.add_argument("--output", type=pathlib.Path, default=DEFAULT_OUTPUT,
                        help=f"where to save the checkpoint (default {DEFAULT_OUTPUT})")
    parser.add_argument("--epochs", type=int, default=4)
    parser.add_argument("--micro-batch", type=int, default=8)
    parser.add_argument("--grad-accum", type=int, default=2,
                        help="micro-batches per update; small datasets need more updates")
    parser.add_argument("--freeze-encoder", action="store_true",
                        help="train only the output head (for GPUs too small for full training)")
    parser.add_argument("--device", help="cuda or cpu (default: the GPU if available)")
    parser.add_argument("--threshold", type=float, default=0.5,
                        help="P(reached her) for the test report, as in config.yaml")
    parser.add_argument("--data", type=pathlib.Path, default=store.DEFAULT_DATA_DIR,
                        help=f"data folder holding the export (default {store.DEFAULT_DATA_DIR})")
    args = parser.parse_args(argv)

    import laya.train  # pylint: disable=import-outside-toplevel  (heavy; only when training)
    import torch  # pylint: disable=import-outside-toplevel

    train, test = args.data / "director_train.jsonl", args.data / "director_test.jsonl"
    test_rows = store.read_jsonl(test)
    print(f"{len(store.read_jsonl(train))} training rows, {len(test_rows)} test rows")
    config = laya.train.TrainConfig(
        epochs=args.epochs, micro_batch=args.micro_batch, grad_accum=args.grad_accum,
        freeze_encoder=args.freeze_encoder, eval_data=str(test) if test_rows else None)
    try:
        laya.train.finetune(str(train), args.base, str(args.output), config,
                            device=args.device or "auto")
    except torch.cuda.OutOfMemoryError:
        raise SystemExit("Not enough GPU memory for full training. Stop other GPU users "
                         "(e.g. `ollama stop llama3:8b`), or re-run with --freeze-encoder "
                         "or --device cpu.") from None
    # A second full copy of the weights, kept only to resume an interrupted run: ~800 MB.
    shutil.rmtree(args.output / "checkpoint_latest", ignore_errors=True)
    (args.output / "director_training.json").write_text(json.dumps(
        {"base": args.base, "data": str(args.data), **{k: getattr(args, k) for k in (
            "epochs", "micro_batch", "grad_accum", "freeze_encoder")}}, indent=2), encoding="utf-8")
    print(f"Saved to {args.output}")
    if test_rows:
        evaluate(args.output, test_rows, args.threshold, args.device)
    print("\nTo use it in the game, put this in config.local.yaml and restart the server:\n"
          "director:\n  engine: laya")


if __name__ == "__main__":
    main()
