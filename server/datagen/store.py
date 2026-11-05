"""Where the data lives, JSON Lines reading and writing, and the resumable job runner.

    <data>/conversations/<worker>.jsonl          one file per generation process
    <data>/grades/<grader model>.jsonl           one file per grader
    <data>/failures/<process>.jsonl              what failed; the next run retries it
    <data>/director_{train,test,index}.jsonl     the export

One output file per process means parallel processes never write the same file.
"""

import concurrent.futures
import json
import pathlib
import re
import time
from collections.abc import Callable, Iterable, Iterator
from typing import Any

DEFAULT_DATA_DIR = pathlib.Path(__file__).resolve().parent / "data"
# This many failures in a row usually means a bad key or a used-up daily quota, not bad
# luck, so the run stops instead of burning through every remaining job.
MAX_CONSECUTIVE_FAILURES = 5


def slug(text: str) -> str:
    """Makes a name safe for a file name, e.g. "ollama/llama3:8b" -> "ollama_llama3_8b".

    Args:
        text: Any name.

    Returns:
        Letters, digits, dots and hyphens, with other runs replaced by "_".
    """
    return re.sub(r"[^A-Za-z0-9.-]+", "_", text).strip("_")


def conversations_file(data_dir: pathlib.Path, worker: str) -> pathlib.Path:
    """Names a generation process's output file.

    Args:
        data_dir: The data folder.
        worker: The generation process's name.

    Returns:
        The file's path.
    """
    return data_dir / "conversations" / f"{slug(worker)}.jsonl"


def grades_file(data_dir: pathlib.Path, grader_model: str) -> pathlib.Path:
    """Names a grader's output file.

    Args:
        data_dir: The data folder.
        grader_model: The grader's provider/model name.

    Returns:
        The file's path.
    """
    return data_dir / "grades" / f"{slug(grader_model)}.jsonl"


def failures_file(data_dir: pathlib.Path, process: str) -> pathlib.Path:
    """Names a process's failure log.

    Args:
        data_dir: The data folder.
        process: E.g. "generate-gemini-flash" or "grade-ollama_llama3_8b".

    Returns:
        The file's path.
    """
    return data_dir / "failures" / f"{slug(process)}.jsonl"


def read_jsonl(path: pathlib.Path) -> list[dict[str, Any]]:
    """Reads a JSON Lines file, skipping a torn last line.

    Args:
        path: The file; a missing file reads as empty.

    Returns:
        One dict per line.
    """
    if not path.exists():
        return []
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError:
            print(f"  skipping a malformed line in {path.name}")
    return rows


def append_jsonl(path: pathlib.Path, row: dict[str, Any]) -> None:
    """Appends one row to a JSON Lines file, creating its folder if needed.

    Args:
        path: The file.
        row: The record.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")


def read_conversations(data_dir: pathlib.Path) -> list[dict[str, Any]]:
    """Reads every generation process's conversations.

    Args:
        data_dir: The data folder.

    Returns:
        All conversation records, file by file.
    """
    return [r for path in sorted((data_dir / "conversations").glob("*.jsonl")) for r in read_jsonl(path)]


def read_grades(data_dir: pathlib.Path) -> list[dict[str, Any]]:
    """Reads every grader's grades.

    Args:
        data_dir: The data folder.

    Returns:
        All grade records, file by file.
    """
    return [g for path in sorted((data_dir / "grades").glob("*.jsonl")) for g in read_jsonl(path)]


def run_all(fn: Callable[[Any], Any], items: Iterable[Any],
            concurrency: int) -> Iterator[tuple[Any, Any, Exception | None]]:
    """Applies fn to each item, yielding results as they finish.

    Args:
        fn: The work for one item.
        items: The items.
        concurrency: Worker threads; 1 runs in this thread, in order.

    Yields:
        (item, result, None) on success, or (item, None, error) on failure.
    """
    if concurrency <= 1:
        for item in items:
            try:
                yield item, fn(item), None
            except Exception as e:  # pylint: disable=broad-except
                yield item, None, e
        return
    pool = concurrent.futures.ThreadPoolExecutor(concurrency)
    try:
        futures = {pool.submit(fn, item): item for item in items}
        for future in concurrent.futures.as_completed(futures):
            error = future.exception()
            yield futures[future], None if error else future.result(), error
    finally:
        pool.shutdown(wait=True, cancel_futures=True)


def run_jobs(fn: Callable[[Any], dict[str, Any]], jobs: list[Any], *, concurrency: int,
             output: pathlib.Path, failures: pathlib.Path, label: str,
             key: Callable[[Any], dict[str, Any]],
             describe: Callable[[dict[str, Any]], str]) -> int:
    """Runs jobs, appending each result as it finishes, so a later run can resume.

    Failures are logged and skipped. After MAX_CONSECUTIVE_FAILURES in a row the run
    stops early: run again later (e.g. once a daily quota resets) to carry on.

    Args:
        fn: The work for one job; returns the record to append.
        jobs: The jobs still to do.
        concurrency: Jobs in flight at once.
        output: The file results are appended to.
        failures: The file failures are appended to.
        label: Prefix for progress lines, e.g. the worker or grader name.
        key: Identifies a job in the failure log, e.g. {"id": ..., "turn": ...}.
        describe: Summarises a finished record for its progress line.

    Returns:
        How many jobs succeeded.
    """
    print(f"[{label}] {len(jobs)} to do (concurrency {concurrency})", flush=True)
    started, succeeded, failed_in_a_row = time.monotonic(), 0, 0
    for job, record, error in run_all(fn, jobs, concurrency):
        if error:
            failed_in_a_row += 1
            append_jsonl(failures, {**key(job), "error": repr(error)})
            print(f"[{label}] {key(job)} FAILED: {error!r:.300}", flush=True)
            if failed_in_a_row >= MAX_CONSECUTIVE_FAILURES:
                print(f"[{label}] stopping after {failed_in_a_row} failures in a row "
                      "(a bad key or a used-up quota?). Run again later to resume.", flush=True)
                break
            continue
        failed_in_a_row = 0
        append_jsonl(output, record)
        succeeded += 1
        if len(jobs) <= 20 or succeeded % 10 == 0 or succeeded == len(jobs):
            each = (time.monotonic() - started) / succeeded
            print(f"[{label}] {describe(record)}  [{succeeded}/{len(jobs)}, {each:.1f}s each, "
                  f"~{each * (len(jobs) - succeeded) / 3600:.1f}h left]", flush=True)
    return succeeded
