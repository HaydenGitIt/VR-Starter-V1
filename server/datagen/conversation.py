"""One full conversation, from Mel's opening to an ending, recorded turn by turn.

The conversation id seeds every random choice (profile, persona, styles, noise), so those
are reproducible; the models' outputs still vary from run to run.
"""

import datetime
import hashlib
import random
import time
from typing import Any

from datagen import providers, teacher
from datagen.game import PARENT_PROMPT, RULES, ParentGame

BASE_SEED = 20261004     # with the conversation id, fixes each profile and persona
ASR_NOISE_RATE = 0.10    # share of teacher lines degraded like a speech transcript
OFF_PROFILE_RATE = 0.12  # share of turns where the teacher acts out of character
PROMPT_HASHES = {label: hashlib.sha256(text.encode()).hexdigest()[:12]
                 for label, text in (("parent", PARENT_PROMPT), ("teacher", teacher.TEACHER_PROMPT))}


def conversation_id(worker: str, index: int) -> str:
    """Names a worker's index-th conversation.

    Args:
        worker: The generation process's name.
        index: 0-based position in its batch.

    Returns:
        An id like "gemini-flash-00042".
    """
    return f"{worker}-{index:05d}"


def conversation_seed(cid: str, base_seed: int = BASE_SEED) -> int:
    """Derives a conversation's seed from the base seed and its id.

    Args:
        cid: The conversation id.
        base_seed: The batch's base seed.

    Returns:
        A 32-bit seed.
    """
    return int(hashlib.sha256(f"{base_seed}:{cid}".encode()).hexdigest()[:8], 16)


def run_conversation(cid: str, teacher_role: dict[str, Any], parent_role: dict[str, Any],
                     base_seed: int = BASE_SEED, asr_noise_rate: float = ASR_NOISE_RATE,
                     off_profile_rate: float = OFF_PROFILE_RATE) -> dict[str, Any]:
    """Plays one scenario from Mel's opening to an ending.

    Args:
        cid: The conversation id; it seeds every random choice.
        teacher_role: The staff member's role.
        parent_role: Mel's role.
        base_seed: The batch's base seed.
        asr_noise_rate: Share of teacher lines degraded like a speech transcript.
        off_profile_rate: Share of turns where the teacher acts out of character.

    Returns:
        The conversation record.
    """
    seed = conversation_seed(cid, base_seed)
    rng = random.Random(seed)
    profile = teacher.pick_profile(rng)
    staff = teacher.pick_staff(rng)
    game = ParentGame(parent_role)
    mel_said, staff_said, turns = [RULES.opening_line], [], []
    started = time.monotonic()
    while not game.ended:
        turn = len(turns) + 1
        style, off_profile = teacher.pick_style(profile, turn, rng, off_profile_rate)
        said = teacher.teacher_line(teacher_role, staff, profile, style, mel_said, staff_said)
        noisy = rng.random() < asr_noise_rate
        transcript = teacher.asr_noise(said["text"], rng) if noisy else said["text"]
        mel = game.respond(transcript)
        turns.append({"turn": turn, "style": style, "off_profile": off_profile,
                      "teacher_clean": said["text"], "transcript": transcript, "asr_noise": noisy,
                      "teacher_raw": said["raw"], "teacher_s": said["teacher_s"],
                      "teacher_usage": said["usage"], **mel})
        staff_said.append(said["text"])
        mel_said.append(mel["mel_line"])
    last = turns[-1]
    return {"id": cid, "seed": seed, "base_seed": base_seed, "profile": profile,
            "group": teacher.PROFILES[profile]["group"], "staff": staff,
            "roles": {"teacher": providers.public(teacher_role), "parent": providers.public(parent_role)},
            "prompt_hash": PROMPT_HASHES, "outcome": last["outcome"], "turns_used": len(turns),
            "strikes": last["strikes"], "progress": last["progress"],
            "unparsed": last["unparsed_total"], "asr_noise_rate": asr_noise_rate,
            "duration_s": round(time.monotonic() - started, 2),
            "created": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
            "turns": turns}


def show_conversation(record: dict[str, Any], grades: dict[int, dict[str, Any]] | None = None) -> None:
    """Prints a conversation readably, with grades if given.

    Args:
        record: A conversation record.
        grades: Grade records by turn number, or None.
    """
    staff = record["staff"]
    print(f"{record['id']}  profile={record['profile']} ({record['group']})  "
          f"{staff['name']}, {staff['role']}, {staff['experience']}")
    print(f"  MEL: {RULES.opening_line}")
    for t in record["turns"]:
        heard = f"   [heard as: {t['transcript']}]" if t["asr_noise"] else ""
        off = " off-profile" if t["off_profile"] else ""
        print(f"\n  STAFF ({t['style']}{off}): {t['teacher_clean']}{heard}")
        if grades and t["turn"] in grades:
            g = grades[t["turn"]]
            print(f"    grade {g['score']}/4 {g['verdict']} [{g['category']}] {g['reason']}")
        retry = f", {t['retries']} retr{'y' if t['retries'] == 1 else 'ies'}" if t["retries"] else ""
        print(f"  MEL <{t['verdict']}><{t['emotion']}>{retry}: {t['mel_line'] or '(no spoken line)'}"
              f"    strikes {t['strikes']}, progress {t['progress']}")
    print(f"\n  OUTCOME: {record['outcome']} after {record['turns_used']} turns, "
          f"{record['duration_s']}s")
