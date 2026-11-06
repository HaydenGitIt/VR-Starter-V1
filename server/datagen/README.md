# Training data and the director (Laya)

Run everything from `server/`. On a new machine, do "On a new machine" in
[`../README.md`](../README.md) first. API models need keys as environment variables
(`setx ANTHROPIC_API_KEY ...`, `GEMINI_API_KEY`, `XAI_API_KEY`). An Anthropic key that
isn't tied to a workspace also needs `ANTHROPIC_WORKSPACE_ID`.

## 1. Generate conversations

```bash
.venv/Scripts/python -m datagen.generate --conversations 300
```

- A simulated staff member (the teacher) talks to Mel until the scenario ends.
- `--teacher` and `--parent` pick model presets. Both default to the game's own model on
  this machine. For variety, run a second process at the same time with another teacher,
  e.g. `--teacher haiku-5-5`.
- Writes `datagen/data/conversations/<teacher>.jsonl`. Running it again resumes.

| To change | Edit |
|---|---|
| Mel | `prompts/parent_system_prompt.txt`: the game's own prompt, so this changes the game too |
| The teacher's prompt | `datagen/prompts/teacher_prompt.txt` |
| How well or badly the teacher handles her (profiles, styles, personas) | `datagen/teacher.py` |
| Scenario rules and Mel's opening line | `config.yaml`, under `scenario` |
| Models and their settings | `datagen/roles.py` (named presets; copy one to add a model) |

## 2. Grade the lines

```bash
.venv/Scripts/python -m datagen.grade --graders grader-game haiku-5-5-grader
```

- Each grader judges every teacher line from the conversation up to it, never Mel's reply:
  reached her or not, a 0–4 score, a category and a reason.
- Graders are presets from `datagen/roles.py`:

  | Preset | Model |
  |---|---|
  | `grader-game` | the game's local model |
  | `llama3-grader` | local `llama3:8b` |
  | `haiku-5-5-grader` | Claude Haiku 5.5 (cheap) |
  | `sonnet-5-5-grader` | Claude Sonnet 5.5 |
  | `gemini-flash-grader` | Gemini Flash (free tier) |
  | `grok-grader` | Grok (paid) |

  List two or more: their disagreements become in-between labels for Laya.
- Vet a grader first: `--sanity` grades the 23 known cases, and `--limit 12` grades a dozen
  lines.
- The grader's prompt is `datagen/prompts/grader_prompt.txt`. Its category list must match
  `DEESCALATING` and `NOT_DEESCALATING` in `datagen/grading.py`, which is checked on start.
- Writes `datagen/data/grades/<grader>.jsonl`.

## 3. Fine-tune Laya and use it in the game

```bash
.venv/Scripts/python -m datagen.export
.venv/Scripts/python -m datagen.train_director
```

- **Export** writes one row per graded line, split 80/20 by conversation, into
  `director_train.jsonl` and `director_test.jsonl`.
- **Train.** Free the GPU first (`ollama stop <model>`). Full training needs roughly 8 GB of
  free GPU memory (an estimate). On smaller GPUs add `--freeze-encoder`, which trains far
  less of the model.
- Training saves to `models/director/` and prints how often Laya, and Mel's own tags, agree
  with the graders on the test lines. `--report-only` prints that again without training.
- It's on by default. To turn it off, set `engine: none` in `config.local.yaml`, then restart
  the server.

  ```yaml
  director:
    engine: none
  ```

**How Laya is prompted.** For each line it gets:

- **The state:** Mel's last line, the staff member's previous line, and the line itself as
  transcribed (`state()` in `app/engines/director.py`).
- **The questions in `prompts/director.yaml`:** `reached_her` ("did the line respond to how
  she feels, rather than the problem…", with descriptions of what counts as true and false),
  and `repeated`.

Laya answers with P(reached her), and the line counts as reaching her at
`director.laya.threshold` in `config.yaml` (default 0.5). After changing the questions,
re-export and retrain; the server warns if a model was trained on other wording.

**How Mel is prompted with the director on.** She only acts out the verdict:

- Her prompt is `prompts/parent_system_prompt_director.txt`: her usual prompt without the
  rules for judging the trainee, and she writes no tags.
- She gets the matching note from `notes_to_mel` in `prompts/director.yaml`, and one from
  `state_notes` saying how she feels now.

## Where things are

- `datagen/data/` (gitignored) holds `conversations/`, `grades/`, `failures/` and the
  export. Each process writes its own file.
- `models/director/` (gitignored, ~800 MB) holds the trained Laya. Copy it between
  machines, or retrain.
- `notebooks/synthetic_conversations.ipynb` walks through the same steps on a small pilot.
