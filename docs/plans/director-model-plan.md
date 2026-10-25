# Director model — plan

| | |
|---|---|
| **Status** | Proposal; nothing built yet. Replaces an earlier draft written before Jev and Laya were identified. |
| **Date** | 2026-10-04 |
| **Touches** | `server/` only. No Unity changes; the wire protocol stays the same. |

## 1. The idea

Today one Ollama call does two jobs. Mel's reply starts with
`<verdict><emotion>`, so the dialogue model **judges** the trainee and
**acts** Mel in the same breath ([`OllamaDialogue`](../../server/app/engines/dialogue.py)).
Those are different skills, and judging is where models slip: `qwen2.5:3b`
scored "Calm down" and "I understand" as reaching her, and `llama3:8b`
agreed with 18 of 23 known cases.

A **director** splits the jobs:

1. A fast decision model reads the trainee's line (plus a little context)
   and decides the verdict.
2. The server scores it as it does now (strikes, progress, endings).
3. The dialogue model is told the verdict and only has to act it out.

```
today:     transcript ─► LLM: <verdict><emotion> line… ─► score ─► speak
director:  transcript ─► director: verdict (~40 ms) ─► score ─► LLM: <emotion> line… ─► speak
                                                        └─ third strike: stop here, no LLM call
```

What this buys:

- **Scoring no longer depends on the creative model**, so swapping in a
  cheaper or more expressive dialogue model can't skew it.
- **Calibrated probabilities.** A threshold controls how often a good line is
  wrongly counted as a strike.
- **A third strike is decided before the LLM is even called.**
- **A smaller prompt later.** 29% of Mel's prompt is verdict rules that the
  dialogue model would no longer need: faster, and more room in the context
  window.
- **Debrief material.** The same call can return a reason category ("aimed at
  the problem", "claimed to understand") for the debrief screen.

## 2. Jev and Laya

Both are "System One" models. They don't generate text; they answer typed
questions about a piece of text with calibrated probabilities. `noul` gives
the probability a statement is true, `choice` picks one label from a set,
and `score` places it on a rubric.

| | **Jev** (typesafe.ai) | **Laya** (Convai Innovations) |
|---|---|---|
| Access | Hosted API, early access only | Open weights, Apache-2.0, `pip install laya` |
| Runs on | Their cloud | Your machine (GPU or CPU) |
| Model | Not disclosed | ModernBERT-large, 421M parameters, 512-token context (English); a 322M multilingual variant |
| Speed (their figures) | 70–500 ms per call | ~40 ms per question on a T4 GPU; 0.2–0.5 s on an Apple M-series CPU |
| Adapting to our rubric | Question wording only; no fine-tuning documented | Question wording, plus a fine-tuning notebook (free Kaggle GPUs) and calibration helpers |
| Out of the box | Unknown for this task | Base checkpoint is **near chance** on their own typed-decisions benchmark (0.36); their fine-tuned one scores 0.77. They suggest 100–1,000 labelled examples per task |
| Privacy | Trainee transcripts leave the machine; retention terms not published | Stays local |
| Cost | $0.042 per million input tokens; output free | Free; ~0.8 GB of weights on the GPU, or CPU |

**The useful coincidence:** Laya ships `laya-serve`, a local HTTP server
with Jev's API (`POST /v1/systemone`, same request and response shapes). One
director engine that speaks that API can point at Jev's cloud or a local Laya;
only a URL and a key change.

**Recommendation:** start with Laya through `laya-serve`. It's local, free,
private, fast and fine-tunable. Treat Jev as a comparison, if early access
arrives and sending transcripts to a third party is acceptable. Expect Laya
to need fine-tuning on our own labelled lines. Its zero-shot numbers are
weak, and this rubric is deliberately counter-intuitive: "I understand"
doesn't reach her, but "That's not good enough" does.

Other options of the same shape:

| Option | Fit |
|---|---|
| Second Ollama call with a judge-only prompt | No new dependencies, so it's the cheapest baseline. But it's still an LLM judging: slower (~0.2–1 s on the dev PC) and as error-prone as today with small models |
| Small fine-tuned classifier (e.g. SetFit or ModernBERT-base) | Laya's idea without the framework. Trains on tens to hundreds of examples in minutes and runs very fast, but has no built-in calibration |
| Zero-shot NLI model (e.g. a DeBERTa zero-shot checkpoint) | No training, but likely to fail exactly the counter-intuitive cases above |

## 3. Where to wire it in

**In the server, in [`ScenarioSession._take_turn`](../../server/app/session.py),
between accepting the transcript and calling the dialogue model** (currently
lines 189–190):

```python
self._history.append({"role": "user", "content": text})
judgement = self._director.judge(self._history)   # new: the verdict is decided here
reply = self._generate(judgement)                 # the dialogue model is told it
```

Why there:

- **The session already owns the rules.** Scoring, strikes and endings live
  there. The director feeds them directly, and the third-strike cut happens
  before any model writes a word.
- **Not in Unity.** Unity never talks to models (`PipelineConfig.cs` says
  so). The verdict is needed on the server before generation, and the `tags`
  message already carries it to Unity. `PipelineConfig.asset`'s engine fields
  are labels the server doesn't read, so a `directorEngine` label there would
  be optional.
- **Not inside `OllamaDialogue`.** That would tie judging to one dialogue
  engine. As its own engine, the director can be swapped, faked in tests, and
  paired with any dialogue model.

It becomes a fourth engine type, `server/app/engines/director.py`, built
like `stt.py`, `dialogue.py` and `tts.py`: a base class, implementations, a
name-to-class table and `build()`.

```python
@dataclasses.dataclass(frozen=True)
class Judgement:
    """The director's decision on the trainee's latest line.

    Attributes:
        verdict: "deescalated" or "neutral".
        probability: Calibrated P(the line reached her), for logs and tuning.
        note: One line telling Mel how to react. Never stored in history.
    """

    verdict: str
    probability: float
    note: str


class Director(abc.ABC):
    """Decides whether the trainee's latest line reached Mel."""

    def warm_up(self) -> None:
        """Loads models. Called once before the server reports ready."""

    @abc.abstractmethod
    def judge(self, history: list[dict[str, str]]) -> Judgement | None:
        """Judges the trainee's latest line.

        Args:
            history: The conversation so far; the last message is the trainee's.

        Returns:
            The judgement, or None to let the dialogue model judge (today's behaviour).
        """
```

There are three engines:

- **`none`** returns `None`. It's the default, so nothing changes until you switch.
- **`fake`** uses keyword rules, for tests.
- **`systemone`** calls Jev or `laya-serve` over HTTP.

Any error or timeout also returns `None`, so a dead director falls back to
today's behaviour instead of breaking the turn.

```yaml
director:
  engine: none                    # none = the dialogue model judges itself, as today
  systemone:                      # Jev's API: Jev in the cloud, or Laya via laya-serve
    url: http://127.0.0.1:8001/v1/systemone   # wherever laya-serve listens; Jev: https://api.typesafe.ai/v1/systemone
    model: english                # laya-serve checkpoint; Jev: jev-latest
    api_key_env: TYPESAFE_API_KEY # read from the environment, never committed
    threshold: 0.5                # P(reached her) needed for "deescalated"; tune on the evaluation set
    timeout_s: 1.0                # slower than this -> None -> the dialogue model judges
```

### Telling Mel the verdict (measured)

Two additions, both made only to the request, never to the stored history:

1. **Pre-fill her reply with `<verdict><`.** Ollama continues a trailing
   assistant message, so the verdict is forced and the model writes only the
   emotion and her line. Starting `raw` with the pre-fill keeps the existing
   tag regex working. Your coworker's prompt is unchanged.
2. **A one-line director's note** as a system message, e.g. *"Their last
   line did not reach you. Do not thank them or agree. Push back, or close
   off a little."*

I tested this on the dev PC (Ollama 0.34.4). Forcing `<verdict><` gave
well-formed tags in 16 of 16 replies across both models. Then, with 8 replies
per setup, I forced the rubric-correct verdict on the rubric's tricky lines,
where the models misjudge on their own:

| | Line acts out the forced verdict (my reading) |
|---|---|
| qwen2.5:3b, pre-fill only | ~3/8 (thanks people it was told didn't reach her) |
| qwen2.5:3b, pre-fill + note | ~7/8 |
| llama3:8b, pre-fill only | 8/8 |
| llama3:8b, pre-fill + note | 8/8 (curter lines) |

The note matters most for small models, which is exactly when a director
helps most. The samples are small; the evaluation in §5 should measure this
properly.

**Emotion stays with the dialogue model for now.** The pre-fill leaves the
emotion tag open, so the arc rules in Mel's prompt keep working. A
director-decided third strike makes no LLM call, so its tags use her last
emotion. Moving emotion to the director, or to rules on the counters
("softening after four or five genuine exchanges"), is a later option.

## 4. The questions to ask

The prompt's verdict rules distil into two `noul` questions, plus an
optional `choice` question for the debrief. Each must fit Laya's 512-token
budget together with the state, so keep the wording tight.

```json
{
  "model": "english",
  "state": {
    "parent_said": "Eighty dollars. Centre fees have gone up eighty dollars...",
    "staff_previously_said": "I hear you.",
    "staff_now_says": "I hear you."
  },
  "questions": {
    "reached_her": {
      "type": "noul",
      "instructions": "A parent is upset about an $80/week childcare fee rise and an email nobody answered. Did the staff member's latest line respond to how she feels, rather than to the problem, the policy, the paperwork, or her behaviour?",
      "criteria": {
        "true": "Acknowledges or reflects her feelings, agrees her complaint is fair ('That's not good enough'), owns a failure, gives her the floor, stays present, takes her anger without defending, names what it costs her, is warmly honest about limits, or offers to sit down. Short lines count.",
        "false": "Bare filler ('Okay', 'Right'), claims to understand ('I understand'), explains the increase or points to the email, looks things up, promises a reversal, tells her to calm down, compares her to other families, or is garbled."
      }
    },
    "repeated": {
      "type": "noul",
      "instructions": "Is the staff member's latest line essentially the same acknowledgement as their previous line, without moving anywhere?"
    }
  }
}
```

**Decision rule:** `deescalated` if `P(reached_her) ≥ threshold` and
`P(repeated) < 0.5`; otherwise `neutral`.

- **Threshold.** A wrongly counted strike punishes a trainee who did well;
  a wrongly counted success teaches the wrong habit. Calibrated
  probabilities let you choose that trade-off on the evaluation set, ideally
  with whoever owns the pedagogy.
- **Unsure band (optional).** If the probability sits close to the
  threshold, return `None` and let the dialogue model judge. Keep this only
  if it measures better.
- **The state's shape** (labelled fields as above, or a message list) is
  worth trying both ways on the evaluation set; Laya accepts either.

## 5. Data and evaluation (do this first)

Every option lives or dies on one question: *does it agree with the rubric
on lines it hasn't seen?* So build the yardstick first.

- **Evaluation set.**
  - Start from `check_dialogue.py`'s 23 cases, the prompt's ~30 example
    exchanges, and the phrases its rubric lists.
  - Add paraphrases per rubric category (an LLM can draft them; a person
    reviews them), plus multi-turn cases for "repeating the same
    acknowledgement".
  - Aim for 200–300 labelled turns, split into train and test.
  - Real sessions add more: the summary log already records each trainee
    line with its verdict.
- **Labels from someone who owns the pedagogy**, especially for the
  counter-intuitive cases.
- **`check_director.py`.** Like `check_dialogue.py`, but it scores any
  director, including today's self-judging dialogue model as the baseline.
  It reports accuracy, wrong-strike rate, wrong-success rate, latency, and
  whether Mel's line matched the verdict.

## 6. Build steps

Each step is small and verifiable.

1. **Evaluation set and `check_director.py`**, with today's behaviour as the
   baseline. *Verify:* the baseline reproduces the review's 15/23
   (`qwen2.5:3b`) and 18/23 (`llama3:8b`) on the original cases.
2. **Director slot.** Add the `none` and `fake` engines, the session wiring,
   and the pre-fill + note in `OllamaDialogue` (`generate()` gains an
   optional judgement). *Verify:*
   - all existing tests pass with `none`;
   - new tests show that a fake third strike makes no dialogue call, the
     forced verdict reaches `tags`, and the note never enters history.
3. **`systemone` engine**, with `laya-serve` running in its own virtual
   environment, away from the server's CUDA PyTorch setup. *Verify:* one
   turn end to end, with the director's probability in the server log.
4. **Laya zero-shot** on the evaluation set, with both the English and
   `laya-typed-decisions` checkpoints. Use the numbers to decide whether to
   fine-tune.
5. **Fine-tune and calibrate Laya** on the training split: their Kaggle
   notebook, then `fit_temperature` (the model card says it ships
   over-confident). *Verify:* on the test split.
6. *Optional:* **Jev** with an early-access key. Same engine and script;
   only the URL and key change.
7. *Optional:* **remove the verdict rules** (29% of the prompt) from Mel's
   prompt, keeping her persona, arc and examples. *Verify:* dialogue quality
   and latency with `check_director.py`.
8. *Optional:* a `choice` question over the rubric categories, so the
   debrief can say *why* each turn counted.

## 7. Costs and risks

- **Data is the real cost.** Neither model knows this rubric, and Laya's
  base checkpoint is near chance zero-shot. Budget time for labelling.
- **Latency.**
  - Laya on the GPU adds ~40 ms before generation, partly offset by the LLM
    no longer writing the verdict tag.
  - On the CPU it adds ~0.2–0.5 s.
  - Jev adds a network round trip; Laya's benchmark puts its median at
    ~0.25 s.
  - Third strikes get faster, since no LLM call is made.
- **GPU memory.** Laya's weights are ~0.8 GB, plus CUDA overhead if it runs
  in its own process. It probably fits next to `qwen2.5:3b` on the 6 GB dev
  PC (about 3.2 GB in use at the last measurement), but not next to
  `llama3:8b`. There, run it on the CPU (it has an ONNX/INT8 path) or on the
  target machine.
- **Privacy.** Jev sends what trainees say to a third party. For a training
  study that probably needs consent and the vendor's retention terms, which
  aren't published.
- **Two models can disagree.** The note keeps Mel's line in step with the
  verdict; the evaluation should keep measuring this.
- **Limited context.** The director sees only the last exchange or two
  (512 tokens). Judgements that need the whole conversation stay with the
  dialogue model or need extra questions.

## 8. Open questions

- Is sending transcripts to a cloud API (Jev) acceptable for this project?
- Who labels the evaluation set, and who decides the trade-off between wrong
  strikes and wrong successes?
- Should the director eventually own Mel's emotion too, or should the LLM
  keep it?
- Is a hybrid acceptable (director when confident, dialogue model
  otherwise), or should one model always decide?

## Sources

- Jev: [Introducing System One models and Jev](https://typesafe.ai/blog/introducing-system-one-models-and-jev), [API reference](https://docs.typesafe.ai/api.md)
- Laya: [model card](https://huggingface.co/convaiinnovations/laya), [GitHub](https://github.com/NandhaKishorM/laya), [PyPI](https://pypi.org/project/laya/)
- Measurements: this repo's server and Ollama 0.34.4 on the RTX 2060 dev PC, 2026-10-04.
