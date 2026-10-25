"""The teacher: the staff member's profiles, styles, persona, model call and speech noise.

Each conversation gets a profile: how this staff member tends to handle an upset parent,
weighted for a roughly even mix of good, mixed and poor handling across the rubric. Each
turn the profile picks a style (one technique from Mel's rubric), and occasionally a
random style instead, since real people are inconsistent. The teacher sees only Mel's
spoken lines, plus what any staff member would know; never her private backstory.
"""

import pathlib
import random
import re
from typing import Any

from datagen import providers

GOOD_STYLES = {
    "reflect": "Reflect her feeling back in your own words, e.g. that she sounds frustrated, "
               "worried or let down.",
    "acknowledge": "Briefly acknowledge her, e.g. that you hear her or you're sorry to hear it.",
    "validate": "Agree her grievance is legitimate, e.g. that it is a lot of money or that it "
                "isn't good enough.",
    "floor": "Give her the floor: invite her to tell you more, or ask what worries her most.",
    "present": "Show you are present and staying, e.g. that you're listening, you're here, "
               "or she can take her time.",
    "own": "If she has mentioned something the centre did wrong, own it and apologise; "
           "otherwise acknowledge how she feels.",
    "cost": "Name what this costs her personally, using only what she has told you, or that "
            "Ava is settled and happy in your room.",
    "honest_limits": "Be warmly honest that you can't change the fee yourself, without "
                     "brushing her off.",
    "lower_temperature": "Offer to lower the temperature, e.g. to sit down or step somewhere "
                         "quieter to talk.",
}
POOR_STYLES = {
    "filler": "Say only a bare filler such as 'Okay.', 'Right.', 'Mm.' or 'Sure.', "
              "nothing more.",
    "claim": "Tell her you understand, or that you know how she feels.",
    "problem": "Go straight to the practical problem: payment plans, subsidies, checking her "
               "account or invoice.",
    "policy": "Explain the policy: how the increase was decided, that it was all in the "
              "email, or that she needs to email the office.",
    "behaviour": "Comment on her behaviour: ask her to calm down or lower her voice, or say "
                 "there's no need to be like that.",
    "defensive": "Defend the centre: other centres have gone up too, costs are rising, "
                 "everyone's in the same boat.",
    "promise": "Promise to fix it: say you'll get the increase reversed or waived for her.",
    "repeat": "Say the same acknowledgement you used in your previous line again, almost "
              "word for word.",
}
STYLES = {**GOOD_STYLES, **POOR_STYLES}
_GOOD = {style: 1 for style in GOOD_STYLES}
_POOR = {"problem": 2, "policy": 2, "claim": 1, "filler": 1, "behaviour": 1, "defensive": 1,
         "promise": 1}

# phases: (last turn it applies to, {style: weight}). Turns are 1-based.
PROFILES: dict[str, dict[str, Any]] = {
    "excellent_varied": {
        "group": "good", "weight": 22, "phases": [(99, _GOOD)],
        "habit": "You are skilled and warm with upset parents. You keep listening, you never "
                 "argue, and you never say the same thing twice."},
    "warm_and_honest": {
        "group": "good", "weight": 11,
        "phases": [(99, {"acknowledge": 2, "validate": 2, "honest_limits": 2, "present": 1,
                         "floor": 1, "cost": 1})],
        "habit": "You are kind and straightforward. You don't pretend you can fix things you "
                 "can't, but you make sure people feel heard."},
    "good_but_repetitive": {
        "group": "mixed", "weight": 11,
        "phases": [(1, {"acknowledge": 1}), (99, {"repeat": 3, "acknowledge": 1, "present": 1})],
        "habit": "You mean well. Once you find a phrase that seems to work, you lean on it."},
    "poor_then_improves": {
        "group": "mixed", "weight": 11, "phases": [(2, _POOR), (99, _GOOD)],
        "habit": "You start out flustered and businesslike, then realise she needs to be heard "
                 "and change tack."},
    "good_then_declines": {
        "group": "mixed", "weight": 11, "phases": [(3, _GOOD), (99, _POOR)],
        "habit": "You start well, but you get impatient and slip into fixing, explaining and "
                 "defending."},
    "problem_solver": {
        "group": "poor", "weight": 6,
        "phases": [(99, {"problem": 6, "promise": 1, "acknowledge": 1})],
        "habit": "You are practical. You want to solve the problem straight away."},
    "policy_reciter": {
        "group": "poor", "weight": 5,
        "phases": [(99, {"policy": 6, "defensive": 1, "claim": 1})],
        "habit": "You stick to the rules and the official line, and you explain the process."},
    "dismissive": {
        "group": "poor", "weight": 5,
        "phases": [(99, {"behaviour": 5, "defensive": 1, "filler": 1})],
        "habit": "Upset parents make you uncomfortable and you want this to stop quickly."},
    "claims_understanding": {
        "group": "poor", "weight": 5,
        "phases": [(99, {"claim": 5, "problem": 1, "acknowledge": 1})],
        "habit": "You want to show empathy, so you keep telling people you understand."},
    "over_promiser": {
        "group": "poor", "weight": 5,
        "phases": [(99, {"promise": 5, "claim": 1, "acknowledge": 1})],
        "habit": "You hate letting people down, so you promise whatever will make them happy."},
    "nervous_filler": {
        "group": "poor", "weight": 4,
        "phases": [(99, {"filler": 5, "claim": 1, "acknowledge": 1})],
        "habit": "You are nervous and unsure what to say, so you say very little."},
    "defensive": {
        "group": "poor", "weight": 4,
        "phases": [(99, {"defensive": 5, "policy": 1, "behaviour": 1})],
        "habit": "You feel the centre is being attacked and you stand up for it."},
}

STAFF_NAMES = ["Priya", "Jess", "Tom", "Mei", "Sam", "Aroha", "Daniel", "Fatima", "Chloe",
               "Liam", "Grace", "Hamid", "Bec", "Josh", "Ana", "Kai", "Nikki", "Raj"]
STAFF_ROLES = ["an educator in Ava's room", "the room leader in Ava's room",
               "an assistant educator", "a casual relief educator", "a trainee educator on placement",
               "an educator from the room next door"]
STAFF_EXPERIENCE = ["in your first month", "with about a year's experience",
                    "with five years' experience", "with fifteen years in childcare"]
STAFF_MANNER = ["You speak briefly and plainly.", "You are chatty and warm by nature.",
                "You are a little formal.", "You are quietly spoken.",
                "You talk in casual, everyday Australian English."]

# The teacher's system prompt, editable in prompts/teacher_prompt.txt. {name}, {role},
# {experience}, {manner}, {habit} and {style} are filled in for each turn.
TEACHER_PROMPT = (pathlib.Path(__file__).resolve().parent / "prompts" / "teacher_prompt.txt"
                  ).read_text(encoding="utf-8").rstrip()


def pick_profile(rng: random.Random) -> str:
    """Draws a teacher profile by weight.

    Args:
        rng: The conversation's random generator.

    Returns:
        A PROFILES key.
    """
    names = list(PROFILES)
    return rng.choices(names, weights=[PROFILES[n]["weight"] for n in names])[0]


def pick_staff(rng: random.Random) -> dict[str, str]:
    """Draws the staff member's identity.

    Args:
        rng: The conversation's random generator.

    Returns:
        name, role, experience and manner.
    """
    return {"name": rng.choice(STAFF_NAMES), "role": rng.choice(STAFF_ROLES),
            "experience": rng.choice(STAFF_EXPERIENCE), "manner": rng.choice(STAFF_MANNER)}


def pick_style(profile: str, turn: int, rng: random.Random,
               off_profile_rate: float) -> tuple[str, bool]:
    """Chooses the technique for this turn.

    Args:
        profile: A PROFILES key.
        turn: The 1-based turn number.
        rng: The conversation's random generator.
        off_profile_rate: Chance of a random style instead of the profile's.

    Returns:
        The STYLES key, and whether it was an off-profile pick.
    """
    if rng.random() < off_profile_rate:
        style, off = rng.choice(list(STYLES)), True
    else:
        weights = next(w for last, w in PROFILES[profile]["phases"] if turn <= last)
        style, off = rng.choices(list(weights), weights=list(weights.values()))[0], False
    return ("acknowledge" if style == "repeat" and turn == 1 else style), off


def sanitise(text: str, name: str) -> str:
    """Reduces model output to one or two spoken sentences.

    Args:
        text: The teacher model's raw output.
        name: The staff member's name, to strip as a speaker label.

    Returns:
        The spoken line, or "" if nothing usable is left.
    """
    text = re.sub(r"\*[^*\n]*\*|\([^)\n]*\)|\[[^\]\n]*\]|<[^>\n]*>", " ", text)
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    while len(lines) > 1 and lines[0].endswith(":"):     # "Here's what I'd say:" preamble
        lines.pop(0)
    line = lines[0] if lines else ""
    line = re.sub(rf"^\W*(?:staff(?: member)?|educator|teacher|me|you|{re.escape(name)})\W*?:\s*",
                  "", line, flags=re.IGNORECASE)
    if quoted := re.match(r'^[^"“]*:\s*["“](.+?)["”]', line):
        line = quoted.group(1)
    line = re.sub(r"\s+", " ", line).strip().strip("\"'“”").strip()
    line = " ".join(re.split(r"(?<=[.!?])\s+", line)[:2])
    return line if re.search(r"[A-Za-z]", line) else ""


def teacher_line(role: dict[str, Any], staff: dict[str, str], profile: str, style: str,
                 mel_said: list[str], staff_said: list[str],
                 attempts: int = 3) -> dict[str, Any]:
    """Asks the teacher model for its next spoken line.

    Args:
        role: The teacher's role.
        staff: The staff identity.
        profile: A PROFILES key.
        style: A STYLES key for this turn.
        mel_said: Mel's spoken lines so far, oldest first (opening included).
        staff_said: The teacher's own earlier lines.
        attempts: Calls to make before giving up on an empty line.

    Returns:
        text (the clean line), raw output, attempts used, latency and usage.

    Raises:
        RuntimeError: If every attempt sanitised to nothing.
    """
    system = TEACHER_PROMPT.format(**staff, habit=PROFILES[profile]["habit"], style=STYLES[style])
    messages: list[dict[str, str]] = []
    for i, mel in enumerate(mel_said):
        messages.append({"role": "user", "content": mel or "..."})
        if i < len(staff_said):
            messages.append({"role": "assistant", "content": staff_said[i]})
    raws, latency, usage = [], 0.0, []
    for _ in range(attempts):
        result = providers.complete(role, system, messages)
        raws.append(result.text)
        latency += result.latency_s
        usage.append(result.usage)
        if line := sanitise(result.text, staff["name"]):
            return {"text": line, "raw": raws, "teacher_s": round(latency, 3), "usage": usage}
    raise RuntimeError(f"Teacher produced no usable line: {raws}")


MISHEARINGS = {"hear": "here", "you're": "your", "there": "their", "eighty": "eighteen",
               "fees": "fee", "listening": "listing", "frustrated": "frustrating",
               "Ava": "ever", "Ava's": "evers", "can't": "can", "week": "weak", "sit": "set"}


def asr_noise(line: str, rng: random.Random) -> str:
    """Degrades a line the way a speech transcript might.

    Args:
        line: The clean line.
        rng: The conversation's random generator.

    Returns:
        The noisy transcript (always different from line).
    """
    words = line.split()
    swappable = [i for i, w in enumerate(words) if w.strip(".,!?") in MISHEARINGS]
    if swappable and rng.random() < 0.6:
        i = rng.choice(swappable)
        core = words[i].strip(".,!?")
        words[i] = words[i].replace(core, MISHEARINGS[core])
    if len(words) > 4 and rng.random() < 0.2:
        del words[rng.randrange(1, len(words))]
    if rng.random() < 0.6:
        words.insert(rng.randrange(0, min(len(words), 3) + 1), rng.choice(["um", "uh", "um,"]))
    noisy = " ".join(words)
    if rng.random() < 0.7:
        noisy = noisy.lower()
    if rng.random() < 0.7:
        noisy = re.sub(r"[.,!?;:]", "", noisy)
    return noisy if noisy != line else noisy.lower().rstrip(".!?") + " um"
