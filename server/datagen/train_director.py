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


def evaluate(model_dir: pathlib.Path, data_dir: pathlib.Path, threshold: float,
             device: str | None) -> None:
    """Compares the trained director's verdicts, and Mel's own, with the graders' on test lines.

    Mel's verdicts are the tags she wrote in-game when the conversations were generated,
    i.e. how the game judges without a director.

    Args:
        model_dir: The trained checkpoint.
        data_dir: The data folder holding the export and the conversations.
        threshold: P(reached her) needed for "deescalated", as in the server's config.
        device: Where to run the model, or None for the GPU if available.
    """
    rows = store.read_jsonl(data_dir / "director_test.jsonl")
    index = [e for e in store.read_jsonl(data_dir / "director_index.jsonl") if e["split"] == "test"]
    conversations = store.read_conversations(data_dir)
    mel = {(c["id"], t["turn"]): t["verdict"] == "deescalated" for c in conversations for t in c["turns"]}
    mel_models = ", ".join(sorted({c["roles"]["parent"]["model"] for c in conversations}))
    engine = director.LayaDirector(str(model_dir), device=device, threshold=threshold)
    engine.warm_up()
    # Lines the graders split on have no right answer, so they're left out of the score.
    clear = [(row, entry) for row, entry in zip(rows, index)
             if row["gold"]["reached_her"]["noul"] in (0.0, 1.0)]
    expected = [row["gold"]["reached_her"]["noul"] == 1.0 for row, _ in clear]
    calls = {"Laya, the director": [engine.judge_state(row["state"]).verdict == "deescalated"
                                    for row, _ in clear],
             f"Mel's own tags ({mel_models})": [mel[(e["conversation_id"], e["turn"])]
                                                for _, e in clear]}
    print(f"\nOn the {len(clear)} test lines the graders agreed on "
          f"({len(rows) - len(clear)} split lines left out):")
    for label, reached in calls.items():
        wrong_strikes = sum(e and not r for r, e in zip(reached, expected))
        wrong_successes = sum(r and not e for r, e in zip(reached, expected))
        right = len(clear) - wrong_strikes - wrong_successes
        print(f"  {label:32} {right:3}/{len(clear)} match the graders ({right / max(len(clear), 1):.0%}); "
              f"wrong strikes {wrong_strikes}, wrong successes {wrong_successes}")
    print("  (wrong strike: a good line counted as a miss; wrong success: a poor line counted "
          "as reaching her)")


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
    parser.add_argument("--report-only", action="store_true",
                        help="skip training; just compare the saved model and Mel on the test lines")
    args = parser.parse_args(argv)
    train, test = args.data / "director_train.jsonl", args.data / "director_test.jsonl"
    test_rows = store.read_jsonl(test)
    if args.report_only:
        evaluate(args.output, args.data, args.threshold, args.device)
        return

    import laya.train  # pylint: disable=import-outside-toplevel  (heavy; only when training)
    import torch  # pylint: disable=import-outside-toplevel

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
        evaluate(args.output, args.data, args.threshold, args.device)
    print("\nTo use it in the game, put this in config.local.yaml and restart the server:\n"
          "director:\n  engine: laya")


if __name__ == "__main__":
    main()
