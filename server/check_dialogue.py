"""Checks a dialogue model's tag format and verdicts before you trust it.

Usage: .venv\\Scripts\\python check_dialogue.py [--model qwen2.5:3b]

Runs the configured dialogue engine (config.yaml + config.local.yaml) on
lines with known correct verdicts, ported from the original test_llm.py.
Each line is the trainee's first reply to Mel's opening, as in a real run.
Disagreements mean the model, the prompt, or the expectation is wrong.
"""

import argparse
import dataclasses
import time

from app import config
from app.engines import dialogue

CASES = [
    # Bare tics, claims to understand, aimed at her behaviour or the problem.
    ("Okay.", "neutral"),
    ("Right. Mm.", "neutral"),
    ("I understand.", "neutral"),
    ("I'm going to need you to calm down.", "neutral"),
    ("It's all explained in the letter that went home.", "neutral"),
    ("You'll need to email the office about that.", "neutral"),
    ("It's not really that big an increase.", "neutral"),
    ("Let me just look up your account and check something.", "neutral"),
    ("I'll get that reversed for you, no problem.", "neutral"),
    ("mumble mumble the thing with the", "neutral"),
    # Acknowledging, reflecting, giving her the floor, owning, warmth.
    ("I hear you.", "deescalated"),
    ("I'm sorry to hear that.", "deescalated"),
    ("That's not good enough.", "deescalated"),
    ("I'm listening.", "deescalated"),
    ("You sound really frustrated, and I don't blame you.", "deescalated"),
    ("You've every right to be annoyed about this.", "deescalated"),
    ("Tell me what's happened.", "deescalated"),
    ("Can you tell me what's worrying you most about it?", "deescalated"),
    ("You emailed us and nobody replied? That shouldn't have happened, I'm sorry.",
     "deescalated"),
    ("Eighty dollars a week is a lot of money. I won't pretend otherwise.", "deescalated"),
    ("I honestly can't change the fee, I don't have that power. "
     "But I can get Sarah to call you today.", "deescalated"),
    ("Do you want to come and sit down so we can talk about it properly?", "deescalated"),
    ("Ava's really settled here, she was showing me her painting this morning.",
     "deescalated"),
]


def main() -> None:
    """Runs every case and prints a per-line result and a summary."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--model", help="override the configured model")
    args = parser.parse_args()

    settings = config.load()
    engine_settings = settings.dialogue
    if args.model:
        engine_settings = dataclasses.replace(
            engine_settings, options={**engine_settings.options, "model": args.model})
    engine = dialogue.build(engine_settings)
    print(f"Warming {engine_settings.engine} {engine_settings.options.get('model', '')}...")
    engine.warm_up()

    rules = settings.scenario
    opening = [
        {"role": "user", "content": rules.start_token},
        {"role": "assistant",
         "content": f"<{rules.opening_verdict}><{rules.opening_emotion}>{rules.opening_line}"},
    ]
    agreed = malformed = 0
    tag_times = []
    for line, expected in CASES:
        started = time.monotonic()
        try:
            reply = engine.generate([*opening, {"role": "user", "content": line}])
        except dialogue.MalformedReply as e:
            malformed += 1
            print(f"  MALFORMED   {line!r}\n              {e}")
            continue
        tag_times.append(time.monotonic() - started)
        text = "".join(reply.text).strip()
        ok = reply.verdict == expected
        agreed += ok
        mark = "ok         " if ok else f"WANTED {expected[:4]}"
        print(f"  {mark} {reply.verdict:>11}/{reply.emotion:<10} {line!r}\n"
              f"              Mel: {text}")

    print(f"\nVerdicts as expected: {agreed}/{len(CASES)}   "
          f"malformed: {malformed}/{len(CASES)}   "
          f"median time to tags: {sorted(tag_times)[len(tag_times) // 2]:.2f}s"
          if tag_times else "\nEvery reply was malformed.")


if __name__ == "__main__":
    main()
