"""#21：Tool Spec 工具只是 C# tool API 的薄包裝（規則都在 C#）。"""
import json

import httpx
from fastapi.testclient import TestClient
from pydantic_ai.messages import ToolReturnPart
from pydantic_ai.models.function import AgentInfo, DeltaToolCall, FunctionModel

from app.main import create_app
from tests.test_draft_tools import KEY, body, events


def test_tool_spec_tools_forward_to_csharp_with_run_token_and_show_results_to_model():
    calls, tool_returns, instructions = [], [], []

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append((req.method, req.url.path, dict(req.url.params), req.headers.get("x-run-token"), req.content))
        if req.method == "GET":
            return httpx.Response(200, json=[{"ticketId": "T-2", "id": "x1", "purpose": "查詢工單進度"}])
        if req.method == "PUT":
            return httpx.Response(200, json={"version": 2, "missing": ["system_owner"]})
        return httpx.Response(200, json={"version": 3})

    async def script(messages, info: AgentInfo):
        instructions.append(info.instructions)
        returns = [p for m in messages for p in getattr(m, "parts", []) if isinstance(p, ToolReturnPart)]
        if not returns:
            yield {
                0: DeltaToolCall(name="upsert_tool_spec", json_args=json.dumps({
                    "spec_id": "ts1", "kind": "Extend", "purpose": "查工單", "side_effect": "read",
                    "ref": {"server": "gw", "tool": "get_part"}, "override_human_edit": True})),
                1: DeltaToolCall(name="delete_tool_spec", json_args=json.dumps({"spec_id": "ts2"})),
                2: DeltaToolCall(name="search_tool_specs", json_args=json.dumps({"keyword": "工單"})),
            }
            return
        tool_returns.extend(str(p.content) for p in returns)
        yield "好"

    app = create_app(model=FunctionModel(stream_function=script), api_key="k", tool_transport=httpx.MockTransport(handler))
    with TestClient(app).stream("POST", "/runs", json=body(), headers=KEY) as r:
        evs = events(r)
    assert evs[-1][0] == "done", evs[-1]

    put = next(c for c in calls if c[0] == "PUT")
    assert put[1] == "/internal/tools/tool-specs/ts1" and put[3] == "tok"
    assert json.loads(put[4]) == {"kind": "Extend", "purpose": "查工單", "side_effect": "read",
                                  "ref": {"server": "gw", "tool": "get_part"}, "override_human_edit": True}
    dele = next(c for c in calls if c[0] == "DELETE")
    assert dele[1] == "/internal/tools/tool-specs/ts2" and dele[3] == "tok"
    get = next(c for c in calls if c[0] == "GET")
    assert get[1] == "/internal/tools/tool-specs" and get[2] == {"keyword": "工單"} and get[3] == "tok"
    assert any("system_owner" in t for t in tool_returns)  # 缺哪些必填欄位要讓模型看到
    assert any("查詢工單進度" in t for t in tool_returns)
    assert "tool_specs" in instructions[0]  # 草稿的 tool_specs 一併在回合開始給模型
