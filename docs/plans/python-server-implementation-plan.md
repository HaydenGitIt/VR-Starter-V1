# Python Conversation Server — Implementation Plan

Branch: `python-server` (off `New-pipeline`)
Location: `server/` in this repo
Status: in progress — see §8 for review decisions

## 1. What this server has to satisfy

The Unity side (`Assets/Scripts/ConversationClient.cs`, already written on
`New-pipeline`) defines the contract. The server must implement it exactly
as Unity already expects it — **no changes to `ConversationClient.cs`
should be needed** to make this work.

### `GET /health`
Returns whether the engines are warmed up, plus the audio formats in use:
```json
{
  "ready": true,
  "capture":  { "sample_rate": 16000, "channels": 1, "format": "float32" },
  "playback": { "sample_rate": 24000, "channels": 1, "format": "float32" }
}
```
Unity polls this before opening the WebSocket (engines can take ~15-20s to
warm up on a cold start) and trusts the reported sample rates over its own
config if they disagree.

### `ws:///conversation`
Text frames are JSON commands **in**:
| `type`   | `content` | meaning |
|----------|-----------|---------|
| `begin`  | —         | start the scenario; parent speaks first |
| `text`   | string    | a typed trainee line (bypasses STT — used for testing) |
| `reset`  | —         | clear session, start fresh |
| `summary`| —         | request debrief data |

Binary frames **in**: one complete utterance, float32 little-endian PCM at
the capture sample rate. (Unity's VAD has already found the start/end, so
this always arrives as one full utterance, never a stream of partial
frames.)

Text frames are JSON state updates **out**:
| `type` | fields | meaning |
|---|---|---|
| `ready` | `playback_sample_rate`, `capture_sample_rate`, `max_turns`, `strikes_allowed`, `progress_to_resolve` | handshake, sent once on connect |
| `tags` | `verdict` (`deescalated`\|`neutral`), `emotion` (`agitated`\|`distressed`\|`softening`\|`calm`) | **always sent before `reply` for a turn** |
| `transcript` | `text` | what STT heard |
| `discarded` | `reason` | nothing intelligible heard; turn not counted |
| `reply` | `text` | the parent's line, for the transcript log |
| `ending` | `outcome` (`resolved`\|`disengaged`) | scenario over |
| `turn_complete` | `turn`, `verdict`, `latency` | counters after a turn |
| `turn_end` | `ended` | no more audio coming; safe to reopen mic |
| `reset_complete` | — | ack for `reset` |
| `summary` | (debrief payload, TBD) | ack for `summary` |
| `error` | `message` | something went wrong |

Binary frames **out**: one synthesised sentence at a time, float32
little-endian PCM at the playback sample rate, so audio can start playing
before the whole reply is rendered.

Unity distinguishes binary vs JSON frames by checking if the first byte is
`'{'` (0x7B) — a valid design, since that byte value is an impossible
normalised-audio sample. The server must preserve this: never send a JSON
string that doesn't start with `{`, and never send PCM whose first sample
happens to render as that byte (vanishingly unlikely with real audio, not
worth guarding against).

## 2. Engine selection — mirroring `PipelineConfig`

`Assets/Config/PipelineConfig.asset` names engines by string
(`speechToTextEngine=whisper`, `dialogueEngine=ollama`,
`textToSpeechEngine=kokoro`, plus model/voice identifiers). The server owns
turning those strings into real objects — that's the whole point of the
Unity-side design ("Unity never talks to a model directly").

Proposed server-side config, `server/config.yaml`, mirrors the same field
names so the two configs are easy to eyeball against each other (not
auto-synced for now — see §7 open questions):

```yaml
server:
  host: 127.0.0.1
  port: 8000

speech_to_text:
  engine: whisper
  model: base.en
  device: cuda
  compute_type: float16

dialogue:
  engine: ollama
  model: llama3:8b

text_to_speech:
  engine: kokoro
  voice: af_heart

audio:
  capture_sample_rate: 16000
  capture_channels: 1
  playback_sample_rate: 24000
  playback_channels: 1

scenario:
  max_turns: 8
  strikes_allowed: 3
  progress_to_resolve: 4
```

Each engine category gets a small abstract base + a registry/factory that
picks the concrete class by the `engine` string — the Python-side
counterpart to the interfaces already sketched (but unused) in
`Assets/Scripts/ConversationServices.cs`:

```
server/
  app/
    main.py              FastAPI app: GET /health, WS /conversation, serves web/
    config.py            loads+validates config.yaml into typed settings
    protocol.py          pydantic models for every message/command above
    session.py           ScenarioSession: per-connection state machine
    engines/
      registry.py        engine-name string -> concrete class, per category
      stt/
        base.py          SpeechToTextEngine (ABC): transcribe(samples, format) -> TranscriptResult
        whisper_engine.py  faster-whisper implementation
      dialogue/
        base.py          DialogueEngine (ABC): generate_reply(utterance, history) -> (verdict, emotion, text)
        ollama_engine.py   calls local Ollama, parses self-emitted tags (see §4)
      tts/
        base.py          TextToSpeechEngine (ABC): synthesise(text, emotion) -> iterator of audio chunks
        kokoro_engine.py   Kokoro TTS implementation
  web/
    index.html / app.js / style.css   the self-hosted test site (§5)
  tests/
  requirements.txt
  README.md
```

## 3. Session / scenario state machine

Unity only reacts to what the server sends (`turn_complete` counts,
`ending`) — the strike/progress/turn-limit bookkeeping has no home yet and
needs to live server-side, in a `ScenarioSession` per connection:

- Holds conversation history, turn count, strike count, progress count.
- On a complete utterance (from audio or a `text` command):
  1. STT engine transcribes (skip if it came in as `text` already) → emit `transcript`, or `discarded` if empty/too short.
  2. Dialogue engine generates a reply → emit `tags` (verdict + emotion) **then** `reply`.
  3. Update strike/progress counts per the verdict; emit `turn_complete`.
  4. TTS engine synthesises the reply sentence-by-sentence → emit each as a binary audio frame, then `turn_end`.
  5. Check end conditions (`strikes_allowed` reached → `ending: disengaged`; `progress_to_resolve` reached → `ending: resolved`; `max_turns` reached with neither → presumably `disengaged`, confirm with design intent) and emit `ending` if triggered.
- `reset` clears all of the above and re-runs the `begin` flow.
- `summary` returns whatever the debrief screen needs — exact shape TBD,
  not blocking the rest of this plan (`ConversationClient.cs` doesn't parse
  it yet either — the comment literally says "Handled by the debrief
  screen in a later section").

## 4. Dialogue engine — deliberately not decoupling yet

Per your note, the director-model split is explicitly **out of scope for
this phase** (see `docs/plans/director-model-plan.md` for that, later). So
`ollama_engine.py` starts as a single call that both writes the parent's
line and emits its own verdict/emotion, most likely via a system prompt
that asks the model to prefix its reply with a parseable tag (e.g.
`[verdict=neutral emotion=agitated] <line text>`), which the server parses
into the `tags` message before forwarding the rest as `reply`. This keeps
day-one scope small; swapping this for a two-model director/dialogue split
later is a contained change to this one file, not a protocol change.

## 5. The self-hosted test website

Served by the same FastAPI app (`GET /` → `web/index.html`), so "spin up
the server" and "spin up the website" are the same command — no separate
process, no build step, no framework dependency. Plain HTML/CSS/JS.

**v1 (text-only, build this first):**
- Status line showing `/health` result (ready / warming up / unreachable).
- A text input + send button → opens a WebSocket to `/conversation` from
  the browser (same protocol Unity uses — this is just a second client),
  sends `text` commands.
- A running transcript panel rendering `tags`, `transcript`/`reply`,
  `turn_complete`, `ending` as they arrive.
- Audio playback of incoming binary frames via the Web Audio API (decode
  float32 PCM into an `AudioBuffer`, queue for gapless playback — same
  problem `StreamingAudioPlayer.cs` already solves on the Unity side, just
  in JS).
- Buttons for `begin` / `reset` / `summary`.

**v2 (optional, later):** browser mic capture + a simple JS VAD, to
exercise the real STT path too, not just typed text. Lower priority — `v1`
already fully exercises the dialogue + TTS legs, and `test_stt.py` already
proves STT in isolation.

This directly replaces needing Unity open at all for iterating on
prompts/models/scenario rules — the website *is* the "don't start a full
Unity project" tool.

## 6. Build order

1. **Skeleton:** FastAPI app, `/health` returns a static-but-accurate shape, no engines wired in yet.
2. **Protocol stub:** `/conversation` WS accepts a connection, sends `ready`, and for a `text` command echoes back a fake `tags`+`reply` (hardcoded) — proves the whole message protocol and session plumbing with zero model risk.
3. **Web UI v1 against the stub** — validates the website independently of any model integration.
4. **Real Whisper** (`faster-whisper`, settings from `Assets/Scripts/Test STT`'s `base.en`/cuda/float16) wired in for binary (audio) frames.
5. **Real Ollama dialogue engine** — single-call prompt producing verdict+emotion+line, per §4.
6. **Real Kokoro TTS** — streaming sentence-by-sentence audio frames.
7. **Scenario rules** — strikes/progress/max-turns bookkeeping, `ending`/`turn_complete` fully implemented per §3.
8. **Point Unity at it** — `ConnectionTest.unity` first (socket/health only), then `MicTest.unity` (adds real mic/VAD/audio), then the full scenario scene. If Unity behaves differently than the website against an identical server, the bug is isolated to Unity's transport/audio layer, not the engines.

## 7. Open questions

- **Kokoro on this machine:** need to confirm the actual Kokoro TTS
  package/runtime you want (pip package vs. ONNX runtime vs. something
  else) and whether it needs a GPU — this determines what `kokoro_engine.py`
  actually calls.
- **Ollama transport:** local HTTP API (`http://localhost:11434`) via the
  `ollama` Python package, assuming Ollama is already installed and the
  `llama3:8b` model pulled — confirm that's the case on your dev machine.
- **Python tooling:** plain `venv` + `requirements.txt` (as drafted above)
  vs. `poetry`/`uv` — no strong reason to prefer one, happy to match
  whatever you already use elsewhere.
- **Config drift:** `server/config.yaml` and `Assets/Config/PipelineConfig.asset`
  currently have to be kept in sync by hand. Fine for now given there's only
  one deployment target; worth a follow-up later (e.g. generating one from
  the other) if this becomes a real pain point.
- **`max_turns` reached with no resolution and no 3rd strike** — what's the
  intended `ending.outcome` in that edge case? `PipelineConfig.cs`'s
  comments describe strikes and progress but not this specific edge; needs
  a decision before step 7 above, not blocking anything earlier.

## 8. Review decisions (2026-10-03)

- **Turn message order:** `transcript → tags → reply → turn_complete → [ending] → audio… → turn_end{ended}`.
  The end check runs *before* TTS so Unity never has speech to interrupt.
- **Ending turn has no audio.** When a turn ends the scenario, no audio is sent; Unity's ending Timeline takes over.
- **`turn_end.ended`** = the scenario is over.
- **`max_turns` exhausted** with neither threshold reached → `disengaged`.
- **Unparseable verdict** → turn counted, but no strike and no progress (matches `ConversationLog` in `ConversationServices.cs`).
- **`reset`** only clears the session and acks with `reset_complete`; the client sends `begin` itself.
- **Opening line** on `begin` comes from config, not the LLM.
- **Blocking engine calls** run in `asyncio.to_thread`; engines warm in the background so `/health` reports `ready: false` until done.
- **Model swapping:** `config.yaml` holds defaults (`llama3:8b`); a gitignored `config.local.yaml` overrides per machine (e.g. `qwen2.5:3b`).
  Each engine section is `engine: <name>` plus free-form options passed to that engine, so a new engine needs no config-code change.
- **Simplified layout:** no `protocol.py`; one file per engine category (`stt.py`, `dialogue.py`, `tts.py`), each holding base class,
  implementations (including a `fake` engine for model-free testing) and a `build()` registry.
- **Tooling:** `uv` + Python 3.12 + `requirements.txt` (system Python is 3.7).
