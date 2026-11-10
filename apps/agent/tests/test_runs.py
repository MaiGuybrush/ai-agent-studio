import asyncio
import json
import threading
import time

from fastapi.testclient import TestClient
from pydantic_ai.messages import ModelMessagesTypeAdapter, ModelRequest, UserPromptPart
from pydantic_ai.models.function import AgentInfo, FunctionModel
from pydantic_ai.models.test import TestModel

from app.main import create_app

KEY = {"X-Agent-Key": "k"}


def body(run_id="r1", text="你好"):
    msg = ModelRequest(parts=[UserPromptPart(content=text)])
    return {
        "run_id": run_id,
        "messages": ModelMessagesTypeAdapter.dump_python([msg], mode="json"),
        "opening_context": "需求描述 X",
        "run_token": "tok",
        "tool_api_base_url": "http://127.0.0.1:5080",
    }


def events(resp):
    out, ev = [], None
    for line in resp.iter_lines():
        if line.startswith("event:"):
            ev = line[6:].strip()
        elif line.startswith("data:"):
            out.append((ev, json.loads(line[5:])))
    return out


def client(model=None):
    return TestClient(create_app(model=model or TestModel(custom_output_text="哈囉"), api_key="k"))


def test_runs_requires_api_key():
    r = client().post("/runs", json=body())
    assert r.status_code == 401


def test_run_streams_text_then_done_with_new_messages_and_usage():
    with client().stream("POST", "/runs", json=body(), headers=KEY) as r:
        assert r.status_code == 200
        evs = events(r)
    assert "".join(d["text"] for e, d in evs if e == "text") == "哈囉"
    last_ev, last = evs[-1]
    assert last_ev == "done"
    # 只回本次新增的 messages：agent 的回覆（user 訊息 C# 已經存了）
    assert [m["kind"] for m in last["messages"]] == ["response"]
    assert last["model"] == "test"
    assert last["usage"]["requests"] == 1
    assert last["usage"]["input_tokens"] > 0


def test_model_failure_becomes_error_event():
    async def boom(messages, info: AgentInfo):
        yield "x"
        raise RuntimeError("quota exceeded 429")

    with client(FunctionModel(stream_function=boom)).stream("POST", "/runs", json=body(), headers=KEY) as r:
        evs = events(r)
    assert evs[-1][0] == "error"
    assert "429" in evs[-1][1]["message"]


def test_cancel_unknown_run_is_404():
    assert client().post("/runs/nope/cancel", headers=KEY).status_code == 404


def test_cancel_running_run_ends_with_cancelled_error_event():
    async def slow(messages, info: AgentInfo):
        yield "開始"
        await asyncio.sleep(10)
        yield "不會到"

    c = client(FunctionModel(stream_function=slow))
    got = []

    def consume():
        with c.stream("POST", "/runs", json=body("r9"), headers=KEY) as r:
            got.extend(events(r))

    t = threading.Thread(target=consume)
    t.start()
    for _ in range(50):
        if c.post("/runs/r9/cancel", headers=KEY).status_code == 200:
            break
        time.sleep(0.1)
    t.join(5)
    assert not t.is_alive()
    assert got[-1][0] == "error" and got[-1][1]["code"] == "cancelled"


def test_model_429_becomes_model_busy_error_event():
    from pydantic_ai.exceptions import ModelHTTPError

    async def limited(messages, info: AgentInfo):
        raise ModelHTTPError(429, "test", {"error": "rate limit"})
        yield "x"

    with client(FunctionModel(stream_function=limited)).stream("POST", "/runs", json=body(), headers=KEY) as r:
        evs = events(r)
    assert evs[-1][0] == "error" and evs[-1][1]["code"] == "model_busy"


def test_client_disconnect_aborts_the_run():
    stopped = threading.Event()

    async def slow(messages, info: AgentInfo):
        yield "開始"
        try:
            await asyncio.sleep(10)
        except asyncio.CancelledError:
            stopped.set()
            raise
        yield "不會到"

    c = client(FunctionModel(stream_function=slow))
    with c.stream("POST", "/runs", json=body("r10"), headers=KEY) as r:
        next(r.iter_lines())  # 收到第一行後直接斷線
    assert stopped.wait(5)
