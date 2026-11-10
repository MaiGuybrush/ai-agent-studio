"""Agent service（spec 2.2）：POST /runs（SSE）、POST /runs/{run_id}/cancel、GET /health。

只綁 127.0.0.1：uvicorn app.main:app --host 127.0.0.1 --port 8100
不連 DB、不保存狀態（只保留執行中的 run）。
"""
import asyncio
import json
import secrets
from pathlib import Path

from fastapi import Depends, FastAPI, Header, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel
from pydantic_ai import Agent
from pydantic_ai.exceptions import ModelHTTPError
from pydantic_ai.messages import ModelMessagesTypeAdapter
from pydantic_ai.models import Model

from app.config import Config, load_config

INSTRUCTIONS = (Path(__file__).parent / "instructions.md").read_text(encoding="utf-8")


class RunRequest(BaseModel):
    run_id: str
    messages: list[dict]  # pydantic-ai ModelMessage 的 JSON 形式，最後一則是使用者剛送出的訊息
    opening_context: str = ""
    run_token: str = ""  # 之後呼叫 C# tool API 用；此票尚未掛工具
    tool_api_base_url: str = ""


def sse(event: str, data: dict) -> str:
    return f"event: {event}\ndata: {json.dumps(data, ensure_ascii=False)}\n\n"


def build_model(cfg: Config) -> Model | None:
    if not cfg.model_name:
        return None
    from pydantic_ai.models.openai import OpenAIChatModel
    from pydantic_ai.providers.openai import OpenAIProvider

    return OpenAIChatModel(cfg.model_name, provider=OpenAIProvider(base_url=cfg.model_base_url, api_key=cfg.model_api_key))


def create_app(model: Model | None = None, api_key: str | None = None) -> FastAPI:
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
        agent = Agent(model, instructions=INSTRUCTIONS)
        q: asyncio.Queue[str | None] = asyncio.Queue()

        async def drive() -> None:
            try:
                async with agent.run_stream(
                    None, message_history=history, instructions=f"需求描述（建單時貼上）：\n{req.opening_context}"
                ) as result:
                    async for delta in result.stream_text(delta=True):
                        await q.put(sse("text", {"text": delta}))
                    u = result.usage
                    await q.put(sse("done", {
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
