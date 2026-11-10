"""Agent service（spec 2.2）。v1 skeleton 只有 GET /health；/runs 在後續 ticket 實作。

只綁 127.0.0.1：uvicorn app.main:app --host 127.0.0.1 --port 8100
"""
from fastapi import FastAPI

app = FastAPI(title="ai-agent-studio-agent")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}
