# De-escalation Sim — Conversation Server

This is the Python server that powers the STT → LLM → TTS conversation loop
for the de-escalation VR training scenario.

**This is a standalone tool, built and run outside of Unity.** It has no
dependency on the Unity Editor, a Unity license, or the VR project being
open at all. It's a plain WebSocket/HTTP server — you run it from a
terminal, with Python, like any other backend service. It happens to live
in this repo (next to `Assets/`) for convenience, but nothing about it
requires Unity to build, run, or test.

There are two ways to talk to it:

1. **The Unity project.** `Assets/Scripts/ConversationClient.cs` connects to
   this server exactly the way anything else would — over `/health` and
   `/conversation`. Point `PipelineConfig.asset`'s `serverHost`/`serverPort`
   at wherever this server is running, and the Unity scenario works against
   it unchanged.
2. **The local test website**, included in this folder (`server/web/`).
   Run the server, open `http://localhost:8000` in a browser, and you can
   type lines to the conversational AI and hear/see its replies — no
   Unity, no headset, no VR project involved at all. This is the fast
   iteration loop for testing prompts, models, and scenario rules.

See [`docs/plans/python-server-implementation-plan.md`](../docs/plans/python-server-implementation-plan.md)
for the full design and build-order plan.

**Generating training data, grading it, and training the director (Laya):** see
[`datagen/README.md`](datagen/README.md).

## Quick start

Needs Python 3.12 (the Kokoro TTS package does not support 3.13 yet).
[uv](https://docs.astral.sh/uv/) fetches it for you:

```bash
cd server
uv venv --python 3.12 .venv
uv pip install --python .venv -r requirements.txt
.venv\Scripts\python -m app.main
# then open http://localhost:8000 for the test page
```

The first start downloads the Whisper model (~150 MB for `base.en`) to
`~/.cache/huggingface`. If CUDA gives trouble, set `device: cpu` and
`compute_type: int8` under `speech_to_text` in `config.local.yaml`.

### On a new machine

Everything machine-specific lives outside git, so after cloning or pulling:

1. Install [uv](https://docs.astral.sh/uv/) and [Ollama](https://ollama.com), and pull the
   model Mel should use there, e.g. `ollama pull llama3:8b`.
2. From `server/`, create the environment and install everything:

   ```bash
   uv venv --python 3.12 .venv
   uv pip install --python .venv -r requirements.txt -r requirements-dev.txt -r requirements-notebooks.txt
   uv pip install --python .venv -r requirements-cuda.txt
   ```

   The last line is for NVIDIA GPUs only.
3. If Mel should be a different model than `config.yaml`'s, say so in `config.local.yaml`
   (see [Dialogue model](#dialogue-model)). Data generation then uses the same model for Mel.
4. For API models in data generation, save the keys as environment variables
   (see `datagen/README.md`).
5. Check it: `.venv\Scripts\python -m pytest` should pass every test.
6. A trained director (`models/director/`) is not in git, since it's ~800 MB. Train one on
   that machine (`datagen/README.md`), or copy the folder across.

### Dialogue model

Needs [Ollama](https://ollama.com) running, with the model pulled
(`ollama pull llama3:8b`). Mel's persona and the verdict rubric live in
`prompts/parent_system_prompt.txt` — edit it freely, no code changes needed.
To use a different model on one machine, put this in `config.local.yaml`:

```yaml
dialogue:
  ollama:
    model: qwen2.5:3b
```

The model stays loaded in Ollama after the server stops (`keep_alive: -1`),
so restarts are instant. To free the GPU memory, run `ollama stop llama3:8b`
(or whichever model you use).

Before trusting a new model or prompt, check its tag format and verdicts
against known-correct answers (it uses the configured model unless told
otherwise):

```bash
.venv\Scripts\python check_dialogue.py --model llama3:8b
```

### The director (Laya)

By default Mel's dialogue model also judges each trainee line, in the tags it writes
first. With a director, a separate decision model judges the line instead (a fine-tuned
[Laya](https://huggingface.co/convaiinnovations/laya)). Mel's model is then told the
verdict and only acts it out, using `prompts/parent_system_prompt_director.txt`. The
protocol and Unity are unchanged.

```yaml
# config.local.yaml
director:
  engine: laya    # or "none" for Mel's own tags
```

Training one is a few commands; see "Train the director" in `datagen/README.md`.
Measured on the 6 GB dev PC: with Laya next to `qwen2.5:3b`, Whisper and Kokoro, the
server used 5.9 GB of GPU memory, about the limit. Verdicts arrived ~0.4 s after each
typed line, except an 11 s first turn.

### Speech synthesis

Kokoro (`text_to_speech.kokoro` in `config.yaml`) downloads its model
(~330 MB) on first start. `requirements.txt` installs CPU-only PyTorch,
which works but Mel starts speaking ~1-2s after the trainee. With an
NVIDIA GPU, install the CUDA build afterwards (~2.5 GB download):

```bash
uv pip install --python .venv -r requirements-cuda.txt
```

Measured on an RTX 2060 (6 GB) with `qwen2.5:3b`: first audio ~0.5s after
the trainee finishes, 3.2 GB of GPU memory in use. `llama3:8b` does not
fully fit alongside the other engines on 6 GB, so it partly runs on the
CPU and first audio takes ~1.6-2s; it needs ~8 GB to run at full speed.

#### Emotional delivery (Qwen3-TTS)

Kokoro always sounds the same. Mel's emotional temperature is the signal
the trainee is learning to read, so `text_to_speech.engine: qwen` uses
Qwen3-TTS CustomVoice instead, which takes a plain-English instruction per
sentence. Her emotion tag arrives before any of her dialogue, so the right
instruction is always known by the time a sentence is spoken.

```bash
uv pip install --python .venv -r requirements-qwen.txt
```

Edit `text_to_speech.qwen.instructions` in `config.yaml` to change how each
emotion sounds, and `speaker` to change the voice itself. Fidelity across
emotions is uneven, so those instructions want listening tests rather than
assumption. Much larger than Kokoro: several GB downloaded on first start
and a 1.7B model resident on the GPU, so check it fits alongside the
dialogue model before switching a low-memory machine over.

On this branch `text_to_speech.engine` is `faster-qwen`, the same voice through
`faster-qwen3-tts`. `requirements.txt` installs it, and it only loads on `transformers`
5.15.1 or 5.16.1. It needs under 0.5x realtime on `check_tts.py` to be usable (8.9x on
the 6 GB dev PC, where it barely fits, so that machine should use Kokoro).

### Sending recorded speech

On the test page, click **Talk**, speak, then click **Stop & send** — the
recording goes through Whisper exactly as Unity's utterances do (typed
lines skip speech recognition). Use headphones so the mic does not pick
up Mel. You can also send 16-bit mono 16 kHz WAVs (e.g. from Unity's `CapturedUtterances`
folder) to a running server:

```bash
.venv\Scripts\python send_wav.py path\to\utterance.wav
```

### Tests

```bash
uv pip install --python .venv -r requirements-dev.txt
.venv\Scripts\python -m pytest
```

### Switching models and settings

Three layers, each overriding the one before:

1. `config.yaml` — the shared defaults (committed).
2. `config.local.yaml` — this machine's defaults (gitignored). List only
   the keys you want to change.
3. `--set section.key=value` — this run only, repeatable:

```bash
.venv\Scripts\python -m app.main --set dialogue.ollama.model=qwen2.5:3b
.venv\Scripts\python -m app.main --set dialogue.ollama.model=llama3:8b --set speech_to_text.whisper.model=small.en
.venv\Scripts\python -m app.main --set dialogue.engine=fake --set text_to_speech.engine=fake
```

Configuration (engine selection, models, ports, VAD thresholds, scenario
rules) lives in `server/config.yaml`, mirroring the fields in
`Assets/Config/PipelineConfig.asset` — see the implementation plan for how
the two stay in sync.
