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

### Tests

```bash
uv pip install --python .venv -r requirements-dev.txt
.venv\Scripts\python -m pytest
```

### Per-machine overrides

Create `server/config.local.yaml` (gitignored) and list only the keys you
want to change. It is merged over `config.yaml` at startup.

Configuration (engine selection, models, ports, VAD thresholds, scenario
rules) lives in `server/config.yaml`, mirroring the fields in
`Assets/Config/PipelineConfig.asset` — see the implementation plan for how
the two stay in sync.
