# Review: Conversation server on `python-server`

| | |
|---|---|
| **Branch** | `python-server` (11 commits, `b90031c`..`6ad78cf`) |
| **Location** | [`server/`](../../server) |
| **Authors** | Richard, with Claude Code |
| **Date** | 2026-10-04 |
| **Status** | Ready for review. Not pushed. Unity scenes untouched. |

## 1. What this branch is for

The Unity client (`Assets/Scripts/ConversationClient.cs`) talks to a local
Python server that runs the AI pipeline: speech-to-text → dialogue model →
text-to-speech. A colleague already built a working version of that server,
`DeescalationServer` (`server.py`, `conversation.py`, `engines.py`,
`config.py` and a prompt file). It lives outside this repository and has
been tuned through playtesting.

**This branch reimplements the same server inside the tracked repo**, at
`server/`. It keeps the colleague's behaviour (protocol, scoring rules,
prompt, turn logic, latency tricks), with a few deliberate changes:

1. **Engines can be swapped by name in config.** Each part (STT, dialogue,
   TTS) can be replaced, or given a different model, by editing config,
   per machine or per run, with no code changes.
2. **Fake engines and automated tests.** The protocol and scenario rules
   can be tested without a GPU or any models.
3. **A self-hosted test website**, served by the same process. You can
   type or speak to Mel without opening Unity.
4. **Background warm-up.** `/health` answers `ready: false` while the
   engines load, which is what Unity's polling expects.
5. **Bug fixes** found during the port (§5), including one that affects
   the original server too.
6. **Code style.** Google Python style throughout: type hints, and
   docstrings with Args/Returns.

### Out of scope

- **No changes to Unity C# or scenes.** The only Unity-side change is one
  line in `Assets/Config/PipelineConfig.asset` (`progressToResolve: 6`).
- **Running the Unity scenes against the server** (plan step 8) is parked
  by choice. The test website uses the identical protocol instead.
- **Splitting the director model from the dialogue model** (see
  [`docs/plans/director-model-plan.md`](../plans/director-model-plan.md)).
  Verdicts still come from the dialogue model's own tags, as in the
  original.

## 2. Layout

About 1,150 lines of server Python, 170 lines in the two tool scripts, 290 lines of
HTML/JS, and 310 lines of tests.

```
server/
  config.yaml              shared defaults (committed)
  config.local.yaml        per-machine overrides (gitignored)
  requirements.txt         + requirements-dev.txt, requirements-cuda.txt
  prompts/parent_system_prompt.txt   colleague's prompt, verbatim
  app/
    main.py                FastAPI: GET /health, WS /conversation, serves web/
    config.py              YAML -> typed dataclasses, override layering, rule validation
    session.py             ScenarioSession: history, counters, endings, turn order
    engines/
      stt.py               SpeechToText base + fake + whisper (faster-whisper)
      dialogue.py          Dialogue base + fake + ollama
      tts.py               TextToSpeech base + fake + kokoro
  web/index.html, app.js   test page: health, begin/reset/summary, text, mic, playback
  send_wav.py              sends WAV files to the server the way Unity does
  check_dialogue.py        scores a model's tag format and verdicts on known cases
  samples/*.wav            two synthetic speech clips
  tests/                   21 pytest tests (no GPU or models needed)
```

Each engine module holds an abstract base, its implementations, and a
`name -> class` table. `build(settings)` picks the class named by the
config's `engine:` key. Engine constructors are cheap; model loading
happens in `warm_up()`, which `main.py` runs once in a background thread.

`ScenarioSession` handlers are plain synchronous generators that yield
outgoing messages (`dict` → JSON text frame, `bytes` → audio frame).
`main.py` advances each generator in a worker thread, so blocking engine
calls never stall the event loop. This also makes the session testable
without a socket.

## 3. Behaviour carried over from the original

The aim was behavioural parity with the colleague's `conversation.py`,
`engines.py` and `config.py`. Values come from his `config.py`.

**Protocol:** identical to what `ConversationClient.cs` expects. This was
checked field by field against its `ServerMessage` class.

**Turn order:**

```
transcript -> tags -> [ending] -> audio (sentence, gap, sentence...) -> reply
           -> turn_complete -> [ending if out of turns] -> turn_end
```

| Behaviour | Where |
|---|---|
| Model streams; tags are parsed from the first tokens (60-char deadline) before any dialogue exists | `engines/dialogue.py` `OllamaDialogue` |
| A third strike is decided at tag time. No audio is generated, and the model stream is closed so Ollama stops | `session.py` `_take_turn` |
| `speak_reply_on_failure: false`, `speak_reply_on_success: true` | `config.yaml` `scenario` |
| Running out of turns gives `disengaged`, sent after the last line is spoken | `session.py` |
| Malformed tags: retried `max_retries` (1) times, then a fixed fallback line. It uses a turn but never counts as a strike, and is logged as `unparsed` | `session.py` `_generate` |
| Each sentence goes to TTS as soon as it completes; 90 ms gap between sentences | `session.py` `_speak` |
| Trailing fragment cut off by the token cap is dropped, unless it is all she said | `session.py` `_spoken_sentences` |
| History stores Mel's lines with their tags, after a `[SCENARIO_START]` user turn | `session.py` |
| Fixed opening line, synthesised once at warm-up and reused | `main.py` `_warm_up` |
| Empty transcripts never reach the model and use no turn | `session.py` |
| Kokoro clips trimmed of silence (threshold 0.01, 20 ms padding) | `engines/tts.py` |
| Rules: 8 turns, 3 strikes, **6 progress**; impossible rule combinations rejected at load | `config.py` `Scenario.__post_init__` |
| `llama3:8b`, temperature 0.8, 55-token cap | `config.yaml` `dialogue.ollama` |
| Summary shape `{outcome, turns, strikes, progress, unparsed, log[...]}` | `session.py` `_summary` |
| Windows CUDA DLLs from `nvidia-*-cu12` wheels added to the search path | `engines/stt.py` |

## 4. Differences from the original

| Area | Original | This branch | Why |
|---|---|---|---|
| Engine selection | `*_ENGINE` strings are display labels only; engines are hard-wired in `engines.py` | `engine:` key selects the class; each engine has its own options block | Replacing Kokoro (expected) or any other part is one new class plus a config line |
| Per-machine config | Edit the tracked `config.py` | `config.local.yaml` (gitignored), merged over `config.yaml` | `llama3:8b` on the target machine, `qwen2.5:3b` on the 6 GB dev PC, with no conflicts |
| Per-run config | — | `--set section.key=value` (repeatable) | Switch a model or engine for one run without editing files |
| Imports | Whisper, Ollama and Kokoro always imported | Imported lazily in `warm_up()` | Fake-only runs and tests need no GPU stack |
| Warm-up | Blocks startup; server unreachable until done | Background task; `/health` reports `ready: false` meanwhile | Matches Unity's health polling |
| Warm-up failure | Raises at startup | Printed loudly; server stays `ready: false` | — |
| Ollama warm-up | Sends "Hello." | Sends the real system prompt | First turn's time to tags: 1.9 s → 0.24 s (prompt is cached) |
| Whisper non-speech | Only empty transcripts discarded | Segments with `no_speech_prob > 0.6` also dropped | Whisper returns "You" for silence and noise (see §5) |
| Kokoro language code | Separate `TTS_LANG_CODE`, checked for consistency | Derived from the voice prefix | The two can no longer disagree |
| Audio frame encoding | Raw float32 | First byte guarded against `{` | Fixes dropped frames (see §5) |
| Generated opening | `USE_FIXED_OPENING = False` supported | Fixed opening only | Original defaults to fixed; dropped to keep code small |
| `/health` extras | Also reports scenario rules and engine names | Only `ready`, `capture`, `playback` | Unity reads only these. Easy to add if wanted |
| Testing | Manual scripts (`test_llm.py`, `test_pipeline.py`, …) | 21 pytest tests with fakes; `check_dialogue.py` (port of `test_llm.py`'s cases); `send_wav.py`; the website | — |

## 5. Bugs found and fixed

1. **About 1 in 256 audio frames silently dropped by Unity (affects the original
   server too).** `ConversationClient.HandleMessage` treats a frame
   whose first byte is `{` (0x7B) as JSON. Its comment says no
   normalised sample can start that way, but that holds only for the
   *most* significant byte. In little-endian float32 the first byte is the
   *least* significant one, which is effectively random. Measured: 0.39%
   of frames (1/256). With about 2–3 speech frames per turn, roughly one turn
   in 100–150 would lose a sentence and log a parse error. **Fix
   (server-side, so Unity is unchanged):** `session.audio_frame()` flips
   the lowest bit of that byte when needed. The sample changes by about 1e-7,
   which is inaudible. Covered by `test_audio_frame_never_starts_with_a_brace`.
   *The comment in `ConversationClient.cs` is still wrong. Worth
   correcting whenever that file is next edited.*
2. **Whisper invents "You" from silence and noise.** Without a filter, a
   cough or a chair scrape would count as a turn, probably a strike.
   Silence and noise scored `no_speech_prob` 0.77–0.9 and speech 0.0, so a 0.6
   threshold separates them cleanly (configurable).
3. **First turn about 1.7 s slower than every other turn.** Caused by
   Ollama processing the 400-line system prompt for the first time
   (see §4).
4. **Switching engines passed the wrong options.** Found while adding
   `--set`: `--set dialogue.engine=fake` would have handed Ollama's
   options to the fake engine. Fixed by the per-engine option blocks.
5. **Stale test page after edits.** Responses now carry
   `Cache-Control: no-cache`.

Two bugs introduced during the port (audio collected instead of streamed,
and `close()` called on a plain iterator) were caught by the new tests
before commit.

## 6. Configuration in practice

There are three layers, each overriding the one before:
`config.yaml` → `config.local.yaml` → `--set`.

```bash
# This PC (config.local.yaml selects qwen2.5:3b)
.venv\Scripts\python -m app.main
# One run on llama3, with a bigger Whisper model
.venv\Scripts\python -m app.main --set dialogue.ollama.model=llama3:8b --set speech_to_text.whisper.model=small.en
# No models at all
.venv\Scripts\python -m app.main --set dialogue.engine=fake --set text_to_speech.engine=fake --set speech_to_text.engine=fake
```

## 7. Verification

**Automated:** `pytest` runs 21 tests in under 2 s, with no GPU. They cover turn order,
sentence gaps, the third-strike cut with no audio, the resolving line being
spoken, the out-of-turns ending, retries, the fallback (no strike), truncated
and lone fragments, tagged history, silence discard, reset, rule
validation, config overrides and engine switching, frame encoding, Kokoro
trimming, and one end-to-end WebSocket exchange.

**Manual, on an RTX 2060 (6 GB):**

- `send_wav.py` with both sample clips: exact Whisper transcripts. Noise
  is `discarded`.
- Full pipeline over the WebSocket with all real engines, including a run
  from cold start.
- The test page in a browser: typed turns, a full third-strike ending, and
  mic input (simulated by me, then confirmed by Richard with a real mic).

**Latency** (from the end of the trainee's turn, CUDA PyTorch, `qwen2.5:3b`):

| Stage | Time |
|---|---|
| Transcript | ~0.4 s |
| Tags | +0.1–0.4 s |
| **Mel starts speaking** | **0.44–0.62 s** |
| Turn complete | 0.6–1.1 s |

With CPU PyTorch, first audio takes 1.0–2.1 s. With `llama3:8b` on 6 GB it
takes 1.6–2.0 s, because 22% of the model runs on the CPU. The original server's
documented figure is about 0.85 s to first audio.

**Judging quality** (`check_dialogue.py`, 23 cases from the original
`test_llm.py`, one run at temperature 0.8):

| Model | Expected verdicts | Malformed | Median time to tags |
|---|---|---|---|
| `qwen2.5:3b` | 15/23 | 0 | 0.14 s |
| `llama3:8b` | 18/23 | 0 | 1.17 s (partly on the CPU) |

**Not verified:**

- Unity itself against this server (step 8, parked).
- That closing the stream actually stops Ollama generating server-side. The
  code does what the original did, but this wasn't observed directly.
- Behaviour on the target machine, or with `llama3:8b` fully on the GPU.

## 8. Known limitations and open questions

**For the reviewer's judgement:**

- **`qwen2.5:3b` is not fit for judging.** It scores most poor responses
  ("Calm down", "I understand", "Let me look up your account") as
  `deescalated`. It's fine for pipeline testing only.
- **`llama3:8b`'s misses suggest prompt work.** "I understand" and "Okay."
  were scored as reaching her, although the prompt explicitly lists them as neutral.
  "It's not really that big an increase" was scored as reaching her too.
  "That's not good enough" (agreeing with her) was scored as neutral.
- **`llama3:8b` needs about 8 GB of GPU memory** to run alongside Whisper and
  Kokoro at full speed.
- **With the default rules, running out of turns is only reachable
  through fallback turns.** 8 real verdicts always hit 6 progress or 3
  strikes first. This is intended, but worth knowing.
- **Running out of turns lets Mel finish her line, but strikes and
  resolution are decided before audio.** Ported as-is from the original.
  Whether that asymmetry is deliberate wasn't confirmed.
- **On a third-strike cut, `reply.text` is empty**, because nothing is
  generated past the tags. The original behaves the same way; Unity will log
  an empty "MEL:" line.

**Technical:**

- **Concurrent connections share one instance of each engine** with no
  lock. One Unity client plus one browser tab at the same time could
  interleave Kokoro and Whisper calls. The original has the same
  property. If this matters, a single lock around turn processing is the
  fix.
- **The test page uses click-to-talk, not voice detection**, and the
  deprecated (but universally supported) `ScriptProcessorNode`. It's a
  test tool, not a client.
- **Config drift.** `config.yaml` and `PipelineConfig.asset` are kept in
  sync by hand. `PipelineConfig.cs`'s code default for `progressToResolve`
  is still 4 (it only affects newly created assets).
- **CUDA PyTorch** comes from PyTorch's own package index
  (`requirements-cuda.txt`, pinned `torch==2.11.0+cu128`). Re-running
  `requirements.txt` keeps it.
- **Windows-specific DLL path handling** lives in `engines/stt.py`. It
  does nothing on other platforms.
- **The original's `test_client.py`, `test_pipeline.py` and `test_tts.py`
  were not ported.** The website, `send_wav.py` and `check_dialogue.py`
  cover the same ground.

## 9. Suggested review order

1. [`server/app/session.py`](../../server/app/session.py): the turn logic.
   Compare it against the original `conversation.py`.
2. [`server/app/engines/dialogue.py`](../../server/app/engines/dialogue.py):
   tag parsing and stream closing.
3. [`server/config.yaml`](../../server/config.yaml): every value and its
   rationale.
4. [`server/tests/test_session.py`](../../server/tests/test_session.py):
   whether the tests encode the intended rules.
5. `audio_frame()` in `session.py`, alongside `ConversationClient.HandleMessage`.

To run it locally, see [`server/README.md`](../../server/README.md).

## 10. Commits

| Commit | Change |
|---|---|
| `b90031c` | Skeleton, `/health` |
| `97ea36b` | WebSocket protocol, scenario session, fake engines |
| `472dc45` | Test web page |
| `656384f` | Whisper STT, `send_wav.py` |
| `1b44d3d` | Port of the original turn logic (streaming, retries, fallback) |
| `62106c3` | Ollama dialogue engine, original prompt, `check_dialogue.py` |
| `9d45498` | `--set` overrides, per-engine option blocks |
| `3fca842` | Kokoro TTS, prompt caching at warm-up |
| `d8b741b` | Optional CUDA PyTorch |
| `38701fd` | `progressToResolve: 6` in `PipelineConfig.asset` |
| `6ad78cf` | Mic input on the test page, frame-encoding fix, no-cache |
