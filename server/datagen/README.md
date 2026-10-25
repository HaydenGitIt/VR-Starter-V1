# datagen: synthetic training data for the director model

Plays full conversations between a simulated staff member (the "teacher") and Mel, then
grades every teacher line and exports the result in Laya's training format. The
walkthrough is `notebooks/synthetic_conversations.ipynb`; these scripts do bigger runs.
Run everything from the `server/` folder.

## Setup

```bash
uv pip install --python .venv -r requirements-notebooks.txt
```

API keys are read from environment variables, never from code. On Windows, save them
with `setx`; they're picked up without restarting anything.

| Provider | Variable | Notes |
|---|---|---|
| Anthropic | `ANTHROPIC_API_KEY` | Add `ANTHROPIC_WORKSPACE_ID` if the key isn't tied to a workspace |
| Gemini (free tier) | `GEMINI_API_KEY` | Key from [AI Studio](https://aistudio.google.com/apikey); check your [limits](https://aistudio.google.com/rate-limit) |
| xAI Grok (paid) | `XAI_API_KEY` | |

## Commands

Generate, one process per teacher model. Each can run in its own terminal at the same time:

```bash
.venv/Scripts/python -m datagen.generate --teacher llama3 --conversations 100
.venv/Scripts/python -m datagen.generate --teacher gemini-flash --conversations 100
.venv/Scripts/python -m datagen.generate --teacher haiku-5-5 --conversations 100
```

Grade with several graders at once. Vet a grader on the 23 known cases first, or try a
dozen lines:

```bash
.venv/Scripts/python -m datagen.grade --graders gemini-flash-grader --sanity
.venv/Scripts/python -m datagen.grade --graders haiku-5-5-grader --limit 12
.venv/Scripts/python -m datagen.grade --graders haiku-5-5-grader gemini-flash-grader llama3-grader
```

Export for Laya:

```bash
.venv/Scripts/python -m datagen.export
```

Every command resumes where it left off: run it again after an interruption, or after a
free daily quota resets. A run stops by itself after 5 failures in a row, which is usually
a bad key or a used-up quota. `--help` on any command lists its options.

## Train the director and use it in the game

From `server/`, in order. Each step resumes if interrupted.

1. **Generate conversations.** Run several of these at once (in separate terminals) for
   variety; Mel is the game's own model unless you pass `--parent`.

   ```bash
   .venv/Scripts/python -m datagen.generate --conversations 300
   .venv/Scripts/python -m datagen.generate --teacher haiku-5-5 --conversations 300
   ```

2. **Grade every line**, with two or more graders, since their disagreements become soft labels:

   ```bash
   .venv/Scripts/python -m datagen.grade --graders grader-game haiku-5-5-grader
   ```

3. **Export** the training and test files:

   ```bash
   .venv/Scripts/python -m datagen.export
   ```

4. **Train.** Free the GPU first (`ollama stop <model>`), then:

   ```bash
   .venv/Scripts/python -m datagen.train_director
   ```

   It prints how often the trained director agrees with the graders on the test lines.
   Full training needs ~8 GB of free GPU memory; on smaller GPUs add `--freeze-encoder`,
   which trains much less of the model.
5. **Switch it on**: add this to `config.local.yaml` (or `config.yaml` for everyone) and
   start the server as usual (`.venv/Scripts/python -m app.main`):

   ```yaml
   director:
     engine: laya
   ```

   Type lines on the test page (http://localhost:8000) to try it. Each turn's director
   probability is in the session summary (`director_p`). `engine: none` switches it off.

## Presets

Named roles live in `datagen/roles.py`; copy an entry to add your own.

| Use | Presets |
|---|---|
| Mel | `mel-game` (default), `mel-llama3`, `mel-haiku-4-5` |
| Teacher | `teacher-game` (default), `llama3`, `gemini-flash`, `haiku-5-5`, `sonnet-5-5` |
| Grader | `grader-game`, `llama3-grader`, `gemini-flash-grader`, `haiku-5-5-grader`, `sonnet-5-5-grader`, `grok-grader` |

The `*-game` presets use the model the game itself runs on this machine: `config.yaml`'s
`dialogue.ollama.model`, or `config.local.yaml`'s if it sets one. So on a machine with a
bigger Mel, the data uses that Mel too, with no code changes.

Each API preset has an `rpm` (requests per minute) cap, applied within one process. Free
tiers count every process using the same key, so a Gemini teacher and a Gemini grader
running together share one quota.

Mel defaults to local `llama3:8b`. Ollama answers one request at a time, so local Mel sets
the pace (about 4.5 s per reply on the dev PC), however many generation processes run.

## Data

Everything goes in `datagen/data/` (gitignored), or wherever `--data` points:

```
conversations/<worker>.jsonl         one file per generation process
grades/<grader model>.jsonl          one file per grader
failures/<process>.jsonl             what failed; the next run retries it
director_train.jsonl, director_test.jsonl, director_index.jsonl   the export
```

One file per process means parallel processes never write the same file. The export
splits by conversation (every line of a conversation lands on the same side) and turns
several graders' votes into soft labels.
