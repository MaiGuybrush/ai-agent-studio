"""#20：草稿工具只是 C# tool API 的薄包裝（規則都在 C#）。"""
import json

import httpx
from fastapi.testclient import TestClient
from pydantic_ai.messages import ModelMessagesTypeAdapter, ModelRequest, ToolReturnPart, UserPromptPart
from pydantic_ai.models.function import AgentInfo, DeltaToolCall, FunctionModel

from app.main import create_app

KEY = {"X-Agent-Key": "k"}


def body():
    msg = ModelRequest(parts=[UserPromptPart(content="你好")])
    return {
        "run_id": "r1",
        "messages": ModelMessagesTypeAdapter.dump_python([msg], mode="json"),
        "opening_context": "需求描述 X",
        "run_token": "tok",
        "tool_api_base_url": "http://127.0.0.1:5080",
        "draft": {"steps": [{"id": "s1", "description": "收單"}]},
        "draft_diff": [{"path": "steps.s1.description", "before": "A", "after": "收單"}],
    }


def events(resp):
    out, ev = [], None
    for line in resp.iter_lines():
        if line.startswith("event:"):
            ev = line[6:].strip()
        elif line.startswith("data:"):
            out.append((ev, json.loads(line[5:])))
    return out


def test_draft_tools_forward_to_csharp_with_run_token_and_return_rejection_to_model():
    calls, seen_instructions, tool_returns = [], [], []

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append((req.method, req.url.path, req.url.params.get("override_human_edit"), req.headers.get("x-run-token"), req.content))
        if req.method == "PUT":
            return httpx.Response(409, json={"rejected": [{"path": "steps.s1.description", "current": "人工"}]})
        return httpx.Response(200, json={"version": 3})

    async def script(messages, info: AgentInfo):
        seen_instructions.append(info.instructions)
        returns = [p for m in messages for p in getattr(m, "parts", []) if isinstance(p, ToolReturnPart)]
        if not returns:
            yield {
                0: DeltaToolCall(name="upsert_workflow_step", json_args=json.dumps({"step_id": "s1", "description": "改"})),
                1: DeltaToolCall(name="delete_workflow_step", json_args=json.dumps({"step_id": "s2", "override_human_edit": True})),
            }
            return
        tool_returns.extend(p.content for p in returns)
        yield "好"

    app = create_app(model=FunctionModel(stream_function=script), api_key="k", http_transport=httpx.MockTransport(handler))
    with TestClient(app).stream("POST", "/runs", json=body(), headers=KEY) as r:
        evs = events(r)
    assert evs[-1][0] == "done", evs[-1]

    put = next(c for c in calls if c[0] == "PUT")
    assert put[1] == "/internal/tools/workflow-steps/s1" and put[3] == "tok"
    assert json.loads(put[4]) == {"description": "改"}  # 只送有給的欄位
    dele = next(c for c in calls if c[0] == "DELETE")
    assert dele[1] == "/internal/tools/workflow-steps/s2" and dele[2] == "true" and dele[3] == "tok"
    assert any("人工" in str(t) and "steps.s1.description" in str(t) for t in tool_returns)  # 拒絕與目前的值要讓模型看到
    assert "steps.s1.description" in seen_instructions[0] and "收單" in seen_instructions[0]  # 草稿與人工 diff 在回合開始就給模型
