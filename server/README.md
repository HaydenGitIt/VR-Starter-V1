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

Before trusting a new model or prompt, check its tag format and verdicts
against known-correct answers (it uses the configured model unless told
otherwise):

```bash
.venv\Scripts\python check_dialogue.py --model llama3:8b
```

### Speech synthesis

Kokoro (`text_to_speech.kokoro` in `config.yaml`) downloads its model
(~330 MB) on first start. The default PyTorch install is CPU-only, which
works but adds ~1s before Mel starts speaking; a CUDA build of PyTorch
makes it near-instant, at the cost of some GPU memory.

### Sending recorded speech

Typed lines on the test page skip speech recognition. To exercise it,
send 16-bit mono 16 kHz WAVs (e.g. from Unity's `CapturedUtterances`
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
