"""One call shape for Ollama, Anthropic and OpenAI-compatible APIs.

A role is a dict: provider ("ollama", "anthropic" or "openai_compatible"), model,
base_url, api_key_env (the NAME of an environment variable; keys never appear in code,
config or outputs), an optional rpm (requests-per-minute cap) and generation options.
Transient failures are retried with exponential backoff.
"""

import copy
import dataclasses
import functools
import os
import random
import sys
import threading
import time
from typing import Any

PROVIDERS = ("ollama", "anthropic", "openai_compatible")
RETRY_ATTEMPTS = 5
RETRY_BASE_DELAY_S = 2.0
# Test hook: provider name -> fake HTTP transport. Empty means the real network.
FAKE_TRANSPORTS: dict[str, Any] = {}


@dataclasses.dataclass(frozen=True)
class Completion:
    """One model call's result.

    Attributes:
        text: The generated text (JSON text in schema mode).
        latency_s: Wall-clock seconds for the successful attempt.
        usage: Token counts and, for Ollama, its timing breakdown.
    """

    text: str
    latency_s: float
    usage: dict[str, Any]


def name(role: dict[str, Any]) -> str:
    """Names a role's model, e.g. "anthropic/claude-haiku-5-5".

    Args:
        role: A role.

    Returns:
        provider/model.
    """
    return f"{role['provider']}/{role['model']}"


def public(role: dict[str, Any]) -> dict[str, Any]:
    """Copies a role for a record. Roles hold environment variable names, never keys.

    Args:
        role: A role.

    Returns:
        provider, model, base_url, api_key_env, rpm and options.
    """
    keep = ("provider", "model", "base_url", "api_key_env", "rpm", "options")
    return {k: copy.deepcopy(role.get(k)) for k in keep}


def check_roles(roles: dict[str, dict[str, Any]]) -> None:
    """Fails fast on role settings that would be silently mishandled.

    Args:
        roles: Roles by name, e.g. {"teacher": ..., "parent": ...}.

    Raises:
        ValueError: On an unknown provider, an inline key, a temperature for Claude, or
            mismatched num_ctx for one Ollama model.
    """
    num_ctx: dict[tuple[str, str], Any] = {}
    for label, role in roles.items():
        if role["provider"] not in PROVIDERS:
            raise ValueError(f"{label}: unknown provider {role['provider']!r}.")
        if any("key" in k and k != "api_key_env" for k in [*role, *role["options"]]):
            raise ValueError(f"{label}: put keys in an environment variable named by api_key_env.")
        if role["provider"] == "anthropic" and "temperature" in role["options"]:
            raise ValueError(f"{label}: Claude's API rejects temperature; remove it from options.")
        if role["provider"] == "ollama":
            ctx = role["options"].get("num_ctx")
            first = num_ctx.setdefault((role["base_url"], role["model"]), ctx)
            if first != ctx:
                raise ValueError(f"{label}: num_ctx {ctx} differs from {first} for the same "
                                 "Ollama model, which makes Ollama reload it every switch.")
    parent = roles.get("parent")
    if parent and parent["provider"] == "ollama" and (parent["options"].get("num_ctx") or 0) < 8192:
        raise ValueError("parent: num_ctx under 8192 silently truncates late turns.")


def check_api_roles(roles: dict[str, dict[str, Any]]) -> None:
    """Sends a one-line prompt to every role that uses an API, and prints the outcome.

    Args:
        roles: Roles by name.
    """
    api_roles = {label: role for label, role in roles.items() if role["provider"] != "ollama"}
    if not api_roles:
        print("Every role uses Ollama; nothing to check.")
    for label, role in api_roles.items():
        workspace_env = role.get("workspace_env", "ANTHROPIC_WORKSPACE_ID")
        workspace = _env(workspace_env) if role["provider"] == "anthropic" and workspace_env else None
        try:
            result = complete(role, "Reply in one word.", [{"role": "user", "content": "Say ok."}])
            print(f"{label:8} OK      {role['model']}: {result.text.strip()!r} in {result.latency_s}s"
                  f"  (workspace header: {workspace or 'none'})")
        except Exception as e:  # pylint: disable=broad-except
            print(f"{label:8} FAILED  {role['model']}: {type(e).__name__}: {e}")


class RateLimiter:
    """Spaces requests evenly, to stay under a requests-per-minute limit."""

    def __init__(self, rpm: float) -> None:
        """Initialises the limiter.

        Args:
            rpm: Requests allowed per minute.
        """
        self._interval = 60.0 / rpm
        self._next = 0.0
        self._lock = threading.Lock()

    def wait(self) -> None:
        """Blocks until the next request may start."""
        with self._lock:
            now = time.monotonic()
            start = max(now, self._next)
            self._next = start + self._interval
        time.sleep(start - now)


_LIMITERS: dict[tuple[Any, ...], RateLimiter] = {}
_LIMITERS_LOCK = threading.Lock()


def _limiter(role: dict[str, Any]) -> RateLimiter | None:
    """Finds the limiter shared by every role on the same endpoint and model.

    Args:
        role: A role; only one with an rpm is limited.

    Returns:
        The limiter, or None.
    """
    if not role.get("rpm"):
        return None
    key = (role["provider"], role.get("base_url"), role["model"], role.get("api_key_env"))
    with _LIMITERS_LOCK:
        return _LIMITERS.setdefault(key, RateLimiter(role["rpm"]))


def complete(role: dict[str, Any], system: str, messages: list[dict[str, str]],
             schema: dict[str, Any] | None = None) -> Completion:
    """Sends one request to the role's provider, retrying transient failures.

    Args:
        role: A role.
        system: The system prompt.
        messages: Chat messages, oldest first, alternating user and assistant.
        schema: A JSON schema to constrain the reply to, or None for free text.

    Returns:
        The completion.

    Raises:
        Exception: The provider's error, if it is not transient or retries run out.
    """
    call = {"ollama": _call_ollama, "anthropic": _call_anthropic,
            "openai_compatible": _call_openai_compatible}[role["provider"]]
    limiter = _limiter(role)
    for attempt in range(RETRY_ATTEMPTS):
        if limiter:
            limiter.wait()
        started = time.monotonic()
        try:
            text, usage = call(role, system, messages, schema)
            return Completion(text, round(time.monotonic() - started, 3), usage)
        except Exception as e:
            if attempt == RETRY_ATTEMPTS - 1 or not _transient(e):
                raise
            delay = RETRY_BASE_DELAY_S * 2 ** attempt * random.uniform(0.75, 1.25)
            print(f"  {name(role)}: {type(e).__name__}, retrying in {delay:.1f}s")
            time.sleep(delay)
    raise AssertionError("unreachable")


def _transient(error: Exception) -> bool:
    """Says whether a failed call is worth retrying.

    Args:
        error: What the provider raised.

    Returns:
        True for rate limits, server errors, timeouts and dropped connections.
    """
    status = getattr(error, "status_code", None)
    if status is None:
        status = getattr(getattr(error, "response", None), "status_code", None)
    if isinstance(status, int):
        return status == 429 or status >= 500
    names = {cls.__name__ for cls in type(error).__mro__}
    return (isinstance(error, (ConnectionError, TimeoutError))
            or bool(names & {"TransportError", "APIConnectionError", "APITimeoutError"}))


def _env(variable: str) -> str | None:
    """Reads a setting such as an API key, preferring the value Windows has saved.

    On Windows, `setx` (or the environment variables dialog) saves a value for programs
    started afterwards. A Jupyter or VS Code session keeps the copy it started with, even
    across kernel restarts, so that copy is missing or out of date after a key changes.
    The saved value therefore wins; the inherited environment is the fallback, and the
    only source on other systems.

    Args:
        variable: The variable's name.

    Returns:
        Its value, or None if it is set nowhere.
    """
    if sys.platform == "win32":
        import winreg  # pylint: disable=import-outside-toplevel

        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
                saved = winreg.QueryValueEx(key, variable)[0]
            if saved:
                return saved
        except OSError:
            pass
    return os.environ.get(variable) or None


def _api_key(env_name: str | None) -> str | None:
    """Reads an API key from the environment, never from code.

    Args:
        env_name: The environment variable to read, or None for no key.

    Returns:
        The key, or None if env_name is None.

    Raises:
        RuntimeError: If the variable is named but not set.
    """
    if not env_name:
        return None
    key = _env(env_name)
    if not key:
        raise RuntimeError(f"Set the {env_name} environment variable.")
    return key


def _seconds(nanoseconds: int | None) -> float | None:
    """Converts one of Ollama's nanosecond durations.

    Args:
        nanoseconds: The duration, or None if Ollama did not report it.

    Returns:
        Seconds, rounded to milliseconds, or None.
    """
    return None if nanoseconds is None else round(nanoseconds / 1e9, 3)


@functools.cache
def _ollama_client(base_url: str | None) -> Any:
    """Creates one Ollama client per server.

    Args:
        base_url: The Ollama URL, or None for the default.

    Returns:
        An ollama.Client.
    """
    import ollama  # pylint: disable=import-outside-toplevel

    return ollama.Client(host=base_url)


def _call_ollama(role: dict[str, Any], system: str, messages: list[dict[str, str]],
                 schema: dict[str, Any] | None) -> tuple[str, dict[str, Any]]:
    """Calls Ollama's native chat API, so num_ctx, keep_alive and think reach the server.

    Args:
        role: A role with provider "ollama".
        system: The system prompt.
        messages: Chat messages.
        schema: JSON schema for structured output, or None.

    Returns:
        The reply text and its usage.
    """
    opts = role["options"]
    options = {"temperature": opts.get("temperature"), "num_predict": opts.get("max_tokens"),
               "num_ctx": opts.get("num_ctx")}
    extra: dict[str, Any] = {}
    if schema:
        extra["format"] = schema
    if opts.get("think") is not None:
        extra["think"] = opts["think"]
    reply = _ollama_client(role.get("base_url")).chat(
        model=role["model"], messages=[{"role": "system", "content": system}, *messages],
        options={k: v for k, v in options.items() if v is not None},
        keep_alive=opts.get("keep_alive"), **extra)
    usage = {"input_tokens": reply.prompt_eval_count, "output_tokens": reply.eval_count,
             "prompt_eval_s": _seconds(reply.prompt_eval_duration),
             "eval_s": _seconds(reply.eval_duration), "load_s": _seconds(reply.load_duration)}
    return reply.message.content or "", usage


@functools.cache
def _anthropic_client(base_url: str | None, api_key_env: str | None,
                      workspace_env: str | None) -> Any:
    """Creates one Anthropic client per endpoint, key and workspace.

    Args:
        base_url: API URL, or None for Anthropic's.
        api_key_env: Environment variable holding the key.
        workspace_env: Environment variable holding a workspace ID, or None. A key that
            isn't tied to a workspace (sk-ant-usr-...) must name one on every request.

    Returns:
        An anthropic.Anthropic client with the SDK's own retries off.
    """
    import anthropic  # pylint: disable=import-outside-toplevel

    transport = FAKE_TRANSPORTS.get("anthropic")
    http_client = anthropic.DefaultHttpxClient(transport=transport) if transport else None
    workspace = _env(workspace_env) if workspace_env else None
    return anthropic.Anthropic(
        api_key=_api_key(api_key_env) or "", base_url=base_url, max_retries=0,
        http_client=http_client,
        default_headers={"anthropic-workspace-id": workspace} if workspace else None)


def _call_anthropic(role: dict[str, Any], system: str, messages: list[dict[str, str]],
                    schema: dict[str, Any] | None) -> tuple[str, dict[str, Any]]:
    """Calls the Anthropic Messages API.

    Args:
        role: A role with provider "anthropic".
        system: The system prompt.
        messages: Chat messages (must start with a user message).
        schema: JSON schema for structured output, or None.

    Returns:
        The reply text and its usage.

    Raises:
        RuntimeError: If the model refused.
    """
    opts = role["options"]
    output_config: dict[str, Any] = {}
    if opts.get("effort"):
        output_config["effort"] = opts["effort"]
    if schema:
        output_config["format"] = {"type": "json_schema", "schema": schema}
    extra: dict[str, Any] = {}
    if output_config:
        extra["output_config"] = output_config
    if opts.get("thinking"):
        extra["thinking"] = opts["thinking"]
    client = _anthropic_client(role.get("base_url"), role.get("api_key_env"),
                               role.get("workspace_env", "ANTHROPIC_WORKSPACE_ID"))
    reply = client.messages.create(
        model=role["model"], max_tokens=opts.get("max_tokens", 1024), system=system,
        messages=messages, **extra)
    if reply.stop_reason == "refusal":
        raise RuntimeError(f"{role['model']} refused: {reply.stop_details}")
    text = "".join(block.text for block in reply.content if block.type == "text")
    usage = {"input_tokens": reply.usage.input_tokens, "output_tokens": reply.usage.output_tokens,
             "stop_reason": reply.stop_reason}
    return text, usage


@functools.cache
def _openai_http() -> Any:
    """Creates the shared HTTP client for OpenAI-compatible endpoints.

    Returns:
        An httpx.Client.
    """
    import httpx  # pylint: disable=import-outside-toplevel

    return httpx.Client(transport=FAKE_TRANSPORTS.get("openai_compatible"), timeout=120)


def _call_openai_compatible(role: dict[str, Any], system: str, messages: list[dict[str, str]],
                            schema: dict[str, Any] | None) -> tuple[str, dict[str, Any]]:
    """Calls an OpenAI-compatible /chat/completions endpoint (OpenAI, Gemini, Grok, ...).

    Args:
        role: A role with provider "openai_compatible". options may hold an "extra_body"
            dict for server-specific fields (e.g. reasoning_effort).
        system: The system prompt.
        messages: Chat messages.
        schema: JSON schema for structured output, or None.

    Returns:
        The reply text and its usage.
    """
    import httpx  # pylint: disable=import-outside-toplevel

    opts = role["options"]
    body: dict[str, Any] = {"model": role["model"],
                            "messages": [{"role": "system", "content": system}, *messages]}
    for key in ("temperature", "max_tokens"):
        if opts.get(key) is not None:
            body[key] = opts[key]
    if schema:
        body["response_format"] = {"type": "json_schema",
                                   "json_schema": {"name": "reply", "schema": schema, "strict": True}}
    body.update(opts.get("extra_body", {}))
    key = _api_key(role.get("api_key_env"))
    url = f"{role['base_url'].rstrip('/')}/chat/completions"
    headers = {"Authorization": f"Bearer {key}"} if key else {}
    response = _openai_http().post(url, json=body, headers=headers)
    schema_dropped = response.status_code == 400 and "response_format" in body
    if schema_dropped:
        # Some servers reject structured output or parts of the schema. The prompt still
        # asks for JSON and the caller validates it, so try once more without.
        del body["response_format"]
        response = _openai_http().post(url, json=body, headers=headers)
    if response.is_error:
        # raise_for_status() would drop the server's explanation of what it rejected.
        raise httpx.HTTPStatusError(f"{response.status_code} from {url}: {response.text[:500]}",
                                    request=response.request, response=response)
    data = response.json()
    usage = data.get("usage") or {}
    return (data["choices"][0]["message"].get("content") or "",
            {"input_tokens": usage.get("prompt_tokens"),
             "output_tokens": usage.get("completion_tokens"), "schema_dropped": schema_dropped})
