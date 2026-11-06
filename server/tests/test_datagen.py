"""Tests for the synthetic data package, with fake models: no network, GPU or cost."""

import json
import time
from collections.abc import Iterator
from typing import Any

import httpx
import pytest

from datagen import export, generate, grade, providers, store

_SCHEMA = {"type": "object", "properties": {"ok": {"type": "boolean"}},
           "required": ["ok"], "additionalProperties": False}
_HI = [{"role": "user", "content": "hi"}]


@pytest.fixture(name="fake_http")
def _fake_http(monkeypatch: pytest.MonkeyPatch) -> Iterator[dict[str, Any]]:
    """Routes provider HTTP calls to fake transports, with a fake key and fast retries.

    Args:
        monkeypatch: Sets the key and retry delay for the test only.

    Yields:
        The provider -> transport table to fill in.
    """
    monkeypatch.setattr(providers, "RETRY_BASE_DELAY_S", 0.01)
    monkeypatch.setenv("FAKE_PROVIDER_TEST_KEY", "fake-key-for-tests")
    providers._anthropic_client.cache_clear()  # pylint: disable=protected-access
    providers._openai_http.cache_clear()  # pylint: disable=protected-access
    yield providers.FAKE_TRANSPORTS
    providers.FAKE_TRANSPORTS.clear()
    providers._anthropic_client.cache_clear()  # pylint: disable=protected-access
    providers._openai_http.cache_clear()  # pylint: disable=protected-access


def _transport(module: Any, replies: list[tuple[int, dict[str, Any]]], seen: list[Any]) -> Any:
    """Builds a transport that records requests and plays back canned replies.

    Args:
        module: httpx or httpx2, matching the client the transport is for.
        replies: (status, JSON body) pairs, returned in order.
        seen: Receives each request.

    Returns:
        A MockTransport.
    """
    def handler(request: Any) -> Any:
        seen.append(request)
        status, body = replies.pop(0)
        return module.Response(status, json=body)

    return module.MockTransport(handler)


def test_anthropic_request_shape_retry_schema_and_workspace(
        fake_http: dict[str, Any], monkeypatch: pytest.MonkeyPatch) -> None:
    """The SDK gets the right body and headers; a 429 is retried.

    Args:
        fake_http: The fake transport table.
        monkeypatch: Sets a fake workspace variable.
    """
    anthropic = pytest.importorskip("anthropic")
    sdk_http = pytest.importorskip("httpx2" if int(anthropic.__version__.split(".")[0]) >= 1 else "httpx")
    message = {"id": "msg_1", "type": "message", "role": "assistant", "model": "m",
               "content": [{"type": "text", "text": '{"ok": true}'}],
               "stop_reason": "end_turn", "stop_sequence": None,
               "usage": {"input_tokens": 12, "output_tokens": 4}}
    limited = {"type": "error", "error": {"type": "rate_limit_error", "message": "slow"}}
    seen: list[Any] = []
    fake_http["anthropic"] = _transport(sdk_http, [(429, limited)] + [(200, message)] * 3, seen)
    haiku = {"provider": "anthropic", "model": "claude-haiku-4-5", "base_url": None,
             "api_key_env": "FAKE_PROVIDER_TEST_KEY", "workspace_env": None,
             "options": {"max_tokens": 55}}
    result = providers.complete(haiku, "SYSTEM", _HI, _SCHEMA)
    body = json.loads(seen[-1].content)
    assert len(seen) == 2, "the 429 should have been retried once"
    assert seen[-1].url.path == "/v1/messages"
    assert "fake-key-for-tests" in (seen[-1].headers.get("x-api-key", "")
                                    + seen[-1].headers.get("authorization", ""))
    assert (body["model"], body["system"], body["max_tokens"]) == ("claude-haiku-4-5", "SYSTEM", 55)
    assert "temperature" not in body and body["messages"] == _HI
    assert body["output_config"] == {"format": {"type": "json_schema", "schema": _SCHEMA}}
    assert json.loads(result.text) == {"ok": True} and result.usage["input_tokens"] == 12

    opus = {**haiku, "model": "claude-opus-5-5", "options": {"max_tokens": 4000, "effort": "low"}}
    providers.complete(opus, "S", _HI)
    assert json.loads(seen[-1].content)["output_config"] == {"effort": "low"}
    assert "anthropic-workspace-id" not in seen[-1].headers
    monkeypatch.setenv("FAKE_PROVIDER_TEST_WORKSPACE", "wrkspc_fake")
    providers.complete({**opus, "workspace_env": "FAKE_PROVIDER_TEST_WORKSPACE"}, "S", _HI)
    assert seen[-1].headers["anthropic-workspace-id"] == "wrkspc_fake"


def test_openai_compatible_retry_schema_fallback_and_errors(fake_http: dict[str, Any]) -> None:
    """A 503 is retried, a rejected schema is dropped once, and errors keep the server's message.

    Args:
        fake_http: The fake transport table.
    """
    chat = {"choices": [{"message": {"role": "assistant", "content": "Hello."}}],
            "usage": {"prompt_tokens": 9, "completion_tokens": 2}}
    compat = {"provider": "openai_compatible", "model": "some-model",
              "base_url": "https://example.invalid/v1/", "api_key_env": "FAKE_PROVIDER_TEST_KEY",
              "options": {"temperature": 1.0, "max_tokens": 60}}
    seen: list[Any] = []
    fake_http["openai_compatible"] = _transport(httpx, [(503, {"error": "busy"}), (200, chat)], seen)
    result = providers.complete(compat, "SYS", _HI, _SCHEMA)
    body = json.loads(seen[-1].content)
    assert len(seen) == 2 and str(seen[-1].url) == "https://example.invalid/v1/chat/completions"
    assert seen[-1].headers["authorization"] == "Bearer fake-key-for-tests"
    assert body["messages"][0] == {"role": "system", "content": "SYS"}
    assert (body["temperature"], body["max_tokens"]) == (1.0, 60)
    assert body["response_format"]["json_schema"]["schema"] == _SCHEMA
    assert result.text == "Hello." and result.usage["output_tokens"] == 2

    seen.clear()
    rejected = {"error": {"message": "Unsupported schema keyword: enum"}}
    fake_http["openai_compatible"] = _transport(httpx, [(400, rejected), (200, chat)], seen)
    providers._openai_http.cache_clear()  # pylint: disable=protected-access
    result = providers.complete(compat, "SYS", _HI, _SCHEMA)
    assert "response_format" in json.loads(seen[0].content)
    assert "response_format" not in json.loads(seen[1].content)
    assert result.usage["schema_dropped"] and result.text == "Hello."

    seen.clear()
    fake_http["openai_compatible"] = _transport(httpx, [(401, {"error": "bad key"})], seen)
    providers._openai_http.cache_clear()  # pylint: disable=protected-access
    with pytest.raises(httpx.HTTPStatusError, match="bad key"):
        providers.complete(compat, "SYS", _HI)
    assert len(seen) == 1, "a 401 is not retried"


def test_rate_limiter_spaces_requests() -> None:
    """Requests under an rpm cap are spread out, not sent in a burst."""
    limiter = providers.RateLimiter(rpm=1200)  # one request per 50 ms
    started = time.monotonic()
    for _ in range(3):
        limiter.wait()
    assert time.monotonic() - started >= 0.09


def test_run_jobs_stops_after_repeated_failures(tmp_path: Any) -> None:
    """A run that keeps failing (a bad key, a used-up quota) stops instead of trying every job.

    Args:
        tmp_path: Where the output and failure files go.
    """
    calls = []

    def fail(job: int) -> dict[str, Any]:
        calls.append(job)
        raise RuntimeError("quota exhausted")

    succeeded = store.run_jobs(fail, list(range(20)), concurrency=1, output=tmp_path / "out.jsonl",
                               failures=tmp_path / "failures.jsonl", label="test",
                               key=lambda job: {"id": job}, describe=str)
    assert succeeded == 0 and len(calls) == store.MAX_CONSECUTIVE_FAILURES
    assert len(store.read_jsonl(tmp_path / "failures.jsonl")) == store.MAX_CONSECUTIVE_FAILURES


def _scripted(role: dict[str, Any], system: str, messages: list[dict[str, str]],
              schema: dict[str, Any] | None = None) -> providers.Completion:
    """Stands in for providers.complete: a kind teacher, a reached Mel, two graders that disagree.

    Args:
        role: The role being called; its model picks the reply.
        system: Unused.
        messages: Unused.
        schema: Unused.

    Returns:
        A canned completion.
    """
    del system, messages, schema
    replies = {
        "fake-teacher": "I hear you, that sounds really hard.",
        "fake-mel": "<deescalated>Thank you. It's been a rotten week.",
        "fake-grader-yes": json.dumps({"reason": "Acknowledges her.", "category": "acknowledging",
                                       "verdict": "deescalated", "score": 3}),
        "fake-grader-no": json.dumps({"reason": "Filler.", "category": "filler",
                                      "verdict": "neutral", "score": 1}),
    }
    return providers.Completion(replies[role["model"]], 0.0, {})


def _fake_role(model: str) -> dict[str, Any]:
    """Builds an API-style role for a scripted model.

    Args:
        model: A model name _scripted knows.

    Returns:
        The role.
    """
    return {"provider": "openai_compatible", "model": model, "base_url": "https://example.invalid/v1",
            "api_key_env": None, "options": {}}


def test_generate_grade_and_export_end_to_end(tmp_path: Any, monkeypatch: pytest.MonkeyPatch) -> None:
    """Generation resumes, two graders' disagreement becomes a 0.5 label, and splits keep
    each conversation together.

    Args:
        tmp_path: The data folder.
        monkeypatch: Replaces every model call with _scripted.
    """
    monkeypatch.setattr(providers, "complete", _scripted)
    teacher, mel = _fake_role("fake-teacher"), _fake_role("fake-mel")
    assert generate.run(teacher, mel, 3, worker="w", data_dir=tmp_path) == 3
    assert generate.run(teacher, mel, 3, worker="w", data_dir=tmp_path) == 0  # resumes
    conversations = store.read_conversations(tmp_path)
    assert [(c["outcome"], c["turns_used"]) for c in conversations] == [("resolved", 5)] * 3

    grade.run([_fake_role("fake-grader-yes"), _fake_role("fake-grader-no")], tmp_path)
    assert len(store.read_grades(tmp_path)) == 2 * 15

    pairs = export.write(tmp_path)
    assert len(pairs) == 15
    assert pairs[0][0]["gold"]["reached_her"] == {
        "label": "false", "noul": 0.5, "probabilities": {"false": 0.5, "true": 0.5}, "confidence": 0.0}
    splits: dict[str, set[str]] = {}
    for _, entry in pairs:
        splits.setdefault(entry["conversation_id"], set()).add(entry["split"])
    assert all(len(s) == 1 for s in splits.values())
    on_disk = sum(len(store.read_jsonl(tmp_path / f"director_{s}.jsonl")) for s in ("train", "test"))
    assert on_disk == 15
