"""Named role settings: pick them by name on the command line or in the notebook.

Add your own by copying an entry. Keys are read from the environment variable named in
api_key_env, never stored here. rpm caps requests per minute within one process; free
tiers count every process that shares a key (e.g. a Gemini teacher and grader together).

Model notes:
- The *-game presets use the model the game itself is configured with on this machine
  (config.yaml, then config.local.yaml), so Mel matches the game, and a bigger model on
  a bigger machine is picked up without code changes. They are the scripts' defaults.
- Claude's API takes no temperature. Haiku 5.5 and Sonnet 5.5 always think, and thinking
  counts towards max_tokens, so they get room and low effort. Haiku 4.5 doesn't think
  unless asked, so the game's 55-token cap means 55 spoken tokens.
- Gemini's free tier: key from https://aistudio.google.com/apikey saved as GEMINI_API_KEY.
  Gemini 3 models always think. Free-tier limits are per Google project (check
  https://aistudio.google.com/rate-limit), and free-tier prompts may be used to improve
  Google's products, which is fine for synthetic data.
- Grok has no free tier on xAI's models page. Key saved as XAI_API_KEY.
"""

import copy
from typing import Any

from datagen.game import GAME

OLLAMA_URL = "http://127.0.0.1:11434"
GEMINI_URL = "https://generativelanguage.googleapis.com/v1beta/openai/"
XAI_URL = "https://api.x.ai/v1"
# Every request to one Ollama model must ask for the same num_ctx, or Ollama reloads the
# model between requests. Mel's prompt alone is ~3,800 tokens, hence the game's 8192.
_OLLAMA_SHARED = {"num_ctx": GAME["context_length"], "keep_alive": GAME["keep_alive"]}
GAME_MODEL = GAME["model"]  # the game's Mel on this machine


def _ollama(model: str, **options: Any) -> dict[str, Any]:
    """Builds a local Ollama role.

    Args:
        model: Ollama model tag.
        **options: Generation options.

    Returns:
        The role.
    """
    return {"provider": "ollama", "model": model, "base_url": OLLAMA_URL, "api_key_env": None,
            "options": {**options, **_OLLAMA_SHARED}}


def _api(provider: str, model: str, base_url: str | None, api_key_env: str, rpm: int,
         **options: Any) -> dict[str, Any]:
    """Builds an API role.

    Args:
        provider: "anthropic" or "openai_compatible".
        model: The provider's model ID.
        base_url: The endpoint, or None for the provider's default.
        api_key_env: Environment variable holding the key.
        rpm: Requests per minute to stay under.
        **options: Generation options.

    Returns:
        The role.
    """
    return {"provider": provider, "model": model, "base_url": base_url,
            "api_key_env": api_key_env, "rpm": rpm, "options": options}


_GEMINI_THINKING = {"extra_body": {"reasoning_effort": "low"}}

PRESETS: dict[str, dict[str, Any]] = {
    # --- The game's own model on this machine: Mel, and the defaults for the others ------
    "mel-game": _ollama(GAME_MODEL, temperature=GAME["temperature"], max_tokens=GAME["max_tokens"]),
    "teacher-game": _ollama(GAME_MODEL, temperature=1.0, max_tokens=60),
    "grader-game": _ollama(GAME_MODEL, temperature=0.0, max_tokens=200),
    # --- Mel (the parent) ---------------------------------------------------------------
    "mel-llama3": _ollama("llama3:8b", temperature=GAME["temperature"], max_tokens=GAME["max_tokens"]),
    "mel-haiku-4-5": _api("anthropic", "claude-haiku-4-5", None, "ANTHROPIC_API_KEY", 50,
                          max_tokens=GAME["max_tokens"]),
    # --- Teachers (the staff member) -----------------------------------------------------
    "llama3": _ollama("llama3:8b", temperature=1.0, max_tokens=60),
    "gemini-flash": _api("openai_compatible", "gemini-3.8-flash", GEMINI_URL, "GEMINI_API_KEY",
                         10, max_tokens=2000, **_GEMINI_THINKING),
    "haiku-5-5": _api("anthropic", "claude-haiku-5-5", None, "ANTHROPIC_API_KEY", 50,
                      max_tokens=2000, effort="low"),
    "sonnet-5-5": _api("anthropic", "claude-sonnet-5-5", None, "ANTHROPIC_API_KEY", 50,
                       max_tokens=2000, effort="low"),
    # --- Graders ---------------------------------------------------------------------------
    "llama3-grader": _ollama("llama3:8b", temperature=0.0, max_tokens=200),
    "gemini-flash-grader": _api("openai_compatible", "gemini-3.8-flash", GEMINI_URL,
                                "GEMINI_API_KEY", 10, max_tokens=4000, **_GEMINI_THINKING),
    "haiku-5-5-grader": _api("anthropic", "claude-haiku-5-5", None, "ANTHROPIC_API_KEY", 50,
                             max_tokens=4000, effort="low"),
    "sonnet-5-5-grader": _api("anthropic", "claude-sonnet-5-5", None, "ANTHROPIC_API_KEY", 50,
                              max_tokens=4000, effort="low"),
    "grok-grader": _api("openai_compatible", "grok-4.3", XAI_URL, "XAI_API_KEY", 30,
                        max_tokens=4000),
}


def preset(preset_name: str) -> dict[str, Any]:
    """Looks up a preset by name.

    Args:
        preset_name: A PRESETS key.

    Returns:
        A copy of the role, safe to modify.

    Raises:
        KeyError: With the list of valid names, if there is no such preset.
    """
    if preset_name not in PRESETS:
        raise KeyError(f"No preset {preset_name!r}. Choose from: {', '.join(PRESETS)}")
    return copy.deepcopy(PRESETS[preset_name])
