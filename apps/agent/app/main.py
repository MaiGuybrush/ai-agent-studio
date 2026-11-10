"""Agent service（spec 2.2）：POST /runs（SSE）、POST /runs/{run_id}/cancel、GET /health。

只綁 127.0.0.1：uvicorn app.main:app --host 127.0.0.1 --port 8100
不連 DB、不保存狀態（只保留執行中的 run）。
"""
import asyncio
import json
import secrets
from dataclasses import dataclass
from pathlib import Path

import httpx
from fastapi import Depends, FastAPI, Header, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel
from pydantic_ai import Agent, RunContext
from pydantic_ai.exceptions import ModelHTTPError
from pydantic_ai.messages import ModelMessagesTypeAdapter
from pydantic_ai.models import Model

from app.config import Config, load_config

INSTRUCTIONS = (Path(__file__).parent / "instructions.md").read_text(encoding="utf-8")


class RunRequest(BaseModel):
    run_id: str
    messages: list[dict]  # pydantic-ai ModelMessage 的 JSON 形式，最後一則是使用者剛送出的訊息
    opening_context: str = ""
    run_token: str = ""  # 呼叫 C# tool API 用
    tool_api_base_url: str = ""
    draft: dict | None = None  # 回合開始時的最新 Workflow Definition 草稿
    draft_diff: list[dict] = []  # 自 agent 上次看過後，CIM Engineer 的人工修改


@dataclass
class Deps:
    token: str
    base_url: str
    transport: httpx.AsyncBaseTransport | None


async def call_tool(ctx: RunContext[Deps], method: str, step_id: str, **kw) -> str:
    """草稿工具只是薄包裝：規則（人工修改優先、版本）都在 C#；把 C# 的回應（含拒絕與目前的值）原樣交給模型。"""
    async with httpx.AsyncClient(base_url=ctx.deps.base_url, transport=ctx.deps.transport, headers={"X-Run-Token": ctx.deps.token}) as c:
        r = await c.request(method, f"/internal/tools/workflow-steps/{step_id}", **kw)
    return f"HTTP {r.status_code} {r.text}"


async def upsert_workflow_step(
    ctx: RunContext[Deps],
    step_id: str,
    description: str | None = None,
    tool_spec: str | None = None,
    inputs: list[dict] | None = None,
    outputs: list[str] | None = None,
    needs_approval: bool | None = None,
    condition: str | None = None,
    override_human_edit: bool = False,
) -> str:
    """新增或修改 Workflow Definition 的一步（只傳要改的欄位）。
    tool_spec 為使用的 Tool Spec，人工步驟不傳；inputs 每項 {name, from_user:true} 或 {name, from_step, from_field}；
    被 CIM Engineer 人工修改過的欄位會被拒絕並回傳目前的值，需對話中徵得同意才能帶 override_human_edit=true。"""
    fields = {k: v for k, v in dict(description=description, tool_spec=tool_spec, inputs=inputs, outputs=outputs,
                                    needs_approval=needs_approval, condition=condition).items() if v is not None}
    if override_human_edit:
        fields["override_human_edit"] = True
    return await call_tool(ctx, "PUT", step_id, json=fields)


async def delete_workflow_step(ctx: RunContext[Deps], step_id: str, override_human_edit: bool = False) -> str:
    """刪除 Workflow Definition 的一步；人工修改過的步驟需徵得同意後帶 override_human_edit=true。"""
    return await call_tool(ctx, "DELETE", step_id, params={"override_human_edit": "true"} if override_human_edit else {})


def draft_context(req: "RunRequest") -> str:
    draft = json.dumps(req.draft, ensure_ascii=False)
    diff = json.dumps(req.draft_diff, ensure_ascii=False)
    return "\n".join([
        "需求描述（建單時貼上）：", req.opening_context, "",
        "目前的 Workflow Definition 草稿：", draft, "",
        "CIM Engineer 自上回合以來的人工修改（以此為準，不要覆蓋）：", diff,
    ])


def sse(event: str, data: dict) -> str:
    return f"event: {event}\ndata: {json.dumps(data, ensure_ascii=False)}\n\n"


def build_model(cfg: Config) -> Model | None:
    if not cfg.model_name:
        return None
    from pydantic_ai.models.openai import OpenAIChatModel
    from pydantic_ai.providers.openai import OpenAIProvider

    return OpenAIChatModel(cfg.model_name, provider=OpenAIProvider(base_url=cfg.model_base_url, api_key=cfg.model_api_key))


def create_app(model: Model | None = None, api_key: str | None = None, http_transport: httpx.AsyncBaseTransport | None = None) -> FastAPI:
    cfg = load_config()
    model = model or build_model(cfg)
    api_key = cfg.api_key if api_key is None else api_key
    app = FastAPI(title="ai-agent-studio-agent")
    runs: dict[str, asyncio.Task] = {}

    def auth(x_agent_key: str = Header(default="")) -> None:
        if not api_key or not secrets.compare_digest(x_agent_key, api_key):
            raise HTTPException(401, "invalid api key")

    @app.get("/health")
    def health() -> dict[str, str]:
        return {"status": "ok"}

    @app.post("/runs", dependencies=[Depends(auth)])
    async def start_run(req: RunRequest) -> StreamingResponse:
        if model is None:
            raise HTTPException(503, "model not configured")
        history = ModelMessagesTypeAdapter.validate_python(req.messages)
        agent = Agent(model, instructions=INSTRUCTIONS, deps_type=Deps, tools=[upsert_workflow_step, delete_workflow_step])
        q: asyncio.Queue[str | None] = asyncio.Queue()

        async def drive() -> None:
            try:
                async with agent.run_stream(
                    None, message_history=history, instructions=draft_context(req),
                    deps=Deps(req.run_token, req.tool_api_base_url, http_transport),
                ) as result:
                    async for delta in result.stream_text(delta=True):
                        await q.put(sse("text", {"text": delta}))
                    u = result.usage
                    await q.put(sse("done", {
                        "model": model.model_name,
                        "messages": json.loads(result.new_messages_json()),
                        "usage": {"requests": u.requests, "input_tokens": u.input_tokens, "output_tokens": u.output_tokens},
                    }))
            except asyncio.CancelledError:
                await q.put(sse("error", {"code": "cancelled", "message": "已中止"}))
            except Exception as e:  # 模型端點錯誤（含 429／配額）一律轉成明確的錯誤事件
                busy = isinstance(e, ModelHTTPError) and e.status_code == 429
                await q.put(sse("error", {"code": "model_busy" if busy else "error", "message": str(e)}))
            finally:
                await q.put(None)

        task = asyncio.create_task(drive())
        runs[req.run_id] = task

        async def gen():
            try:
                while (chunk := await q.get()) is not None:
                    yield chunk
            finally:  # C# 斷線（generator 被關閉）時中止 run
                task.cancel()
                runs.pop(req.run_id, None)

        return StreamingResponse(gen(), media_type="text/event-stream")

    @app.post("/runs/{run_id}/cancel", dependencies=[Depends(auth)])
    async def cancel_run(run_id: str) -> dict[str, str]:
        task = runs.get(run_id)
        if task is None:
            raise HTTPException(404, "run not found")
        task.cancel()
        return {"status": "cancelling"}

    return app


app = create_app()
