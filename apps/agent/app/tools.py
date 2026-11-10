"""agent 的唯讀工具（spec 8.3）：全是薄包裝，以 HTTP 轉給 C# 的內部 tool API，只帶 run token。

不做任何業務判斷；權限（只能看該 run 的那張單）由 C# 依 token 把關。
"""
from collections.abc import Callable

import httpx


def make_readonly_tools(base_url: str, run_token: str, transport: httpx.AsyncBaseTransport | None = None) -> list[Callable]:
    async def call(path: str):
        try:
            async with httpx.AsyncClient(base_url=base_url, transport=transport, timeout=30,
                                         headers={"X-Run-Token": run_token}) as c:
                r = await c.get(f"/internal/tools/{path}")
                r.raise_for_status()
                return r.json()
        except httpx.HTTPError as e:  # 讓模型看到失敗原因，而不是整個 run 當掉
            return {"error": f"tool API 失敗：{e}"}

    async def list_mcp_tools():
        """列出 MCP Server List 中各 server 現有的工具（名稱、說明、input/output schema）。用來判斷 Reuse／Extend／New。"""
        return await call("mcp-tools")

    async def list_systems():
        """列出系統對照表：系統名稱與其 System Owner 工號。"""
        return await call("systems")

    async def get_requirement_ticket():
        """取得這張需求單（描述、狀態、CIM Engineer、Business User）。"""
        return await call("requirement-ticket")

    return [list_mcp_tools, list_systems, get_requirement_ticket]
