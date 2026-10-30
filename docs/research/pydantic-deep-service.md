# 研究：pydantic-deep 作為常駐 service 的整合方式

Ticket：#2（map #1）。日期：2026-10-30。

## 來源與版本

- 一手來源：已安裝套件原始碼（`C:\projects\ai-agent-worker\.venv\Lib\site-packages`），版本為
  `pydantic-deep==0.3.50`（2026-10-30 時 PyPI 最新版仍是 0.3.50）、`pydantic-ai-slim==2.57.0`（PyPI 最新 2.63.0）、`fastmcp-slim==4.1.1`、`mcp==2.3.0`。
  pydantic-deep 宣告的相依為 `pydantic-ai-slim>=2.52.0`（`pydantic_deep-0.3.50.dist-info/METADATA`）。
- 上游 repo：https://github.com/vstorm-co/pydantic-deepagents ；pydantic-ai 文件：https://ai.pydantic.dev/
- 先前 POC：`C:\projects\ai-agent-worker\worker.py`、`HANDOFF.md`（per-user container + 單一 session）。
- 下文標「未實測」的項目只讀過原始碼，沒有實際跑過。

## 結論摘要

1. **整個 process 共用一個 `Agent`，每次 run 傳入 session 專屬的 `workspace=`、`deps=` 和 `conversation=`。** pydantic-ai 2.x 的 `run_stream_events(...)` 可以逐次指定 workspace，而且 override 是用 `ContextVar` 實作，在不同 asyncio task 之間互不影響。
2. **建 agent 時有幾個預設值會讓所有 session 寫進同一個位置，共用 agent 前必須關掉**，否則會造成跨使用者資料外洩：`include_history_archive`、`include_memory`、`cost_tracking`/`cost_budget_usd`、`include_improve`，另外還有 `include_checkpoints` 用的全域 store。
3. **MCP 工具的核准一律用 `ApprovalRequiredToolset`（`toolset.approval_required(fn)`）處理。** 原始碼確認 `interrupt_on` 只會傳給內建的 console toolset（write/edit/execute）。
4. **核准要做成「run 結束 → 存檔 → 用新的 run 續跑」**，不要像 POC 那樣在 generator 裡用 `Future` 卡住等待。持久化單位用 `Conversation`，裡面已包含 `deferred_tool_requests`，service 重啟之後也能續跑。
5. **MCP server 從 `mcpServers` JSON 載入**（`pydantic_deep.mcp.parse_mcp_servers`，或 pydantic-ai 的 `load_mcp_toolsets`）。`MCPToolset` 以 reference count 管理連線，可以安全地被多個並行 run 共用。需要依使用者區分憑證的 server，則要每個 run 各自建一個 toolset。
6. **限制：`LocalWorkspaceBackend` 只支援 POSIX**，Windows 會丟出 `NotImplementedError`。常駐 service 要放在 Linux 上，否則只能用記憶體內的 `StateWorkspace`，或自己寫一個 backend。

## 1. 並行 session（userId + interviewId）

**一個 agent 可以服務多個 session**
- `create_deep_agent(...)` 回傳的是 `pydantic_ai.Agent`。pydantic-ai 的設計是 agent 建立一次後重複使用（https://ai.pydantic.dev/agents/ ）。
- `AbstractAgent.run_stream_events(..., deps=, conversation=, message_history=, deferred_tool_results=, toolsets=, capabilities=, workspace=, usage_limits=, cancellation_token=)`（`pydantic_ai/agent/abstract.py:1422`）。`workspace=` 的說明是 "a backend or `Workspace` to use as is"。
- 每個 run 的 workspace override 存在 `self._override_workspace: ContextVar`（`pydantic_ai/agent/__init__.py:832`），所以並行的 asyncio task 不會互相覆寫。
- 傳入 capability 物件會報錯，必須傳 backend，例如 `LocalWorkspaceBackend(path)`（`agent/__init__.py:1786-1790`）。
- `LocalWorkspace` capability 會拒絕把 history 裡的 `WorkspaceRef` 指向其他目錄（`pydantic_ai/capabilities/local_workspace.py`, `get_workspace`），因此別人的 history 不能把 run 導到別的目錄。
- 每個 session 自己的 `DeepAgentDeps()`（todos、uploads、checkpoint_store…，`pydantic_deep/deps.py:41`）。其中 `deps.checkpoint_store` 可以逐 session 覆寫全域的 store（`agent.py` docstring 對 `checkpoint_store` 的說明）。

**建 agent 時就固定、會被所有 session 共用的東西（要關掉，或改成由 service 處理）**

| 參數（預設） | 問題 | 來源 |
|---|---|---|
| `include_history_archive=True` | 路徑在建 agent 時依 `workspace` capability 的 `working_dir` 決定（`.pydantic-deep/messages.json`），所有 session 會寫進同一個檔案，再透過 history search tool 被讀出來 | `pydantic_deep/agent.py:1318-1331` |
| `include_memory=True` | 筆記放在 workspace 內的 `memory_dir`。如果每個 session 用不同的 workspace，目錄會自然分開；但這是跨 run 的記憶，Interview 用不到，建議關掉（另有 `memory_namespace=lambda ctx: ...` 可以用來分隔） | `agent.py:897-908, 1133` |
| `cost_tracking=True` / `cost_budget_usd` | 累計的是「跨 run」的總額，所有使用者共用一份。Usage Quota 應由 service 依 `result.usage()` 自行計算 | `agent.py` docstring |
| `include_checkpoints` 用的 `checkpoint_store` | 預設是一個 `InMemoryCheckpointStore()`，所有 session 共用；要用的話改為逐 session 放在 `deps.checkpoint_store` | `agent.py:1228` |
| `include_improve` | session 目錄固定在 host 端 | `agent.py:1276-1286` |
| `web_search=True`, `web_fetch=True`, `include_monitoring=True`, `include_subagents/plan/todo/skills` | 預設全部開啟。Interview 用不到，建議關掉以縮小攻擊面（POC 也關掉了一部分） | `agent.py:673-750` 簽名 |

**建議的 session 狀態（由 service 自己管）**
- key：`(userId, interviewId)` → 目錄 `workspaces/{userId}/{interviewId}/`，放 `conversation.json` 和 Interview Transcript。
- `conversation.json`：`ConversationTypeAdapter.dump_json(result.conversation)`。`Conversation` 包含 `messages`、`usage`、`conversation_id`、`deferred_tool_requests`。官方說明只用 `message_history=` 的話，會遺失 usage 與 pending 核准（`pydantic_ai/conversation.py:48-93`）。POC 用的是 `ModelMessagesTypeAdapter`，會漏掉這兩項。
- 同一個 session 同一時間只能有一個 run：用 `dict[key, asyncio.Lock]` 或 `running` 旗標擋下，回 409（POC 已經這樣做，只是改成逐 session）。
- 不同 session 之間可以完全並行。整個 service 是單一 event loop，要避免在 tool 裡做同步阻塞的 I/O。多 worker process（`uvicorn --workers N`）時，同一個 session 必須固定路由到同一個 process，或者在核准/續跑之間只依賴磁碟上的 `conversation.json`（見第 3 節的設計，這種方式不需要 sticky routing，只需要檔案鎖）。

## 2. SSE 事件串流與斷線重連

- `run_stream_events` 在 2.x 是 async context manager，會在背景 task 執行 run，事件包含 `PartDeltaEvent`/`FunctionToolCallEvent`/`FunctionToolResultEvent`/`AgentRunResultEvent`（`pydantic_ai/run.py:1086`）。說明文字寫的是 "Must be used as an async context manager so the background run task is deterministically cleaned up"（`abstract.py:1447`起）。
- 也就是說，**run 的生命週期不能綁在 HTTP 連線上**。POC 的作法是對的：`pump()` 在獨立的 task 中執行並把事件存進 log，客戶端用 `GET /events?after=N`（或 `Last-Event-ID`）重新連上。要改的是把 log 從全域變數改成逐 session。若想讓 service 重啟後還能回放，可以把 log 以 JSONL 追加到 session 目錄，順便也當成 Interview Transcript 的原始資料。
- 中止：`cancellation_token=` 參數（`pydantic_ai/_cancel.py`），可以對應到 `POST /sessions/{id}/cancel`。
- pydantic-ai 內建 `pydantic_ai/ui/vercel_ai`、`ui/ag_ui` 兩種 UI event stream adapter。如果 Dev Frontend 打算用 Vercel AI SDK 或 AG-UI，可以直接採用；C# 後端只是轉送的話，POC 那套自訂的 `text/tool_call/tool_result/approval/done/error` 事件比較簡單。
- 已知細節（HANDOFF.md）：核准後續跑時 `tool_call` 會再送一次，前端要依 `id` 去重；`httpx.ASGITransport` 測 SSE 會 deadlock，測試要用真的 uvicorn。

## 3. MCP 工具的人工核准

- `interrupt_on` 只用在 `_build_console_toolset`：`require_write_approval = interrupt_on.get("write_file") or interrupt_on.get("edit_file")`，`require_execute_approval = interrupt_on.get("execute", ...)`（`pydantic_deep/agent.py:445-472`）。`mcp_servers` 只是被原封不動地 `all_toolsets.extend(mcp_servers)`（`agent.py:1223`）。**確認 `interrupt_on` 不涵蓋 MCP 工具。**
- 正確做法：`ApprovalRequiredToolset(approval_required_func=(ctx, tool_def, args) -> bool)`，對應的方法是 `toolset.approval_required(fn)`。它在 `call_tool` 時，若 `not ctx.tool_call_approved and fn(...)` 就 `raise ApprovalRequired`（`pydantic_ai/toolsets/approval_required.py`）。`fn` 拿得到 `ctx.deps`，可以依使用者、角色或參數決定要不要核准。
- 有 `tool_prefix` 時，`fn` 收到的 `tool_def.name` 是否已加上前綴，要看包裝順序：`approval_required` 包在 `PrefixedToolset` 外層就會看到加過前綴的名稱。**未實測**，建議在 `MCP Server List` 設定裡逐 server 列出 `approve: ["tool_a", ...]`，在包上 prefix 之前就先套用核准。
- output 必須包含 `DeferredToolRequests`（`output_type=[str, DeferredToolRequests]`）。`create_deep_agent` 只有在 `interrupt_on` 有值時才會自動加上（`agent.py:1305-1315`），只用 MCP 核准時要自己傳，POC 已經這樣做。
- **建議流程（不要讓等待卡在 process 記憶體裡）：**
  1. run 結束，output 是 `DeferredToolRequests` → 存 `conversation.json`，發出 `approval` 事件（`id/name/args`），session 狀態改為 `awaiting_approval`，SSE 結束。
  2. `POST /sessions/{id}/approvals {tool_call_id, ok}` → 讀 `conversation.json`，組 `DeferredToolResults(approvals={id: ok})`，開一個新的 run：`run_stream_events(None, conversation=conv, deferred_tool_results=results, ...)`。
  3. 好處：service 重啟或核准拖了很久都沒有影響；POC 的「核准一直不回 → container 永遠活著」問題（HANDOFF.md）也不會發生。
  POC 在同一個 generator 中用 `asyncio.Future` 等待，等待期間只存在記憶體裡。

## 4. 從設定載入多個 MCP server（HTTP / stdio）

- 格式：標準的 `mcpServers` JSON（Claude Desktop/Code 同款）。
  - `pydantic_deep.mcp.parse_mcp_servers(dict) -> list[MCPServerConfig]`：有 `command` 就是 stdio；有 `url` 就是 http（`type: sse` 時改為 sse）；ws 和格式錯誤的項目會記 warning 後略過；`${VAR}` / `${VAR:-default}` 會展開（`pydantic_deep/mcp/loader.py`）。
  - `MCPRegistry(configs).build_active(on_degraded=...)`：只建 enabled 且 auth 已滿足的 server，並以 `make_resilient` 包起來，**連不上的 server 不提供工具，但不會讓整個 run 失敗**；`on_degraded(name, reason)` 可以把這件事轉成 SSE 事件告訴使用者（`pydantic_deep/mcp/registry.py:415-604`）。
  - 另一個選擇是 pydantic-ai 的 `load_mcp_toolsets(path)`，會自動以 server 名稱加 prefix；未定義的 env var 直接丟錯（`pydantic_ai/mcp.py:2164`）。
- `MCPServerConfig` 欄位：`transport`、`command/args/env`、`url/headers`、`tool_prefix`、`auth`（`none/bearer/header/env/oauth`）、`init_timeout`（預設 5s，啟動慢的 server 要調高）、`include_resources`、`include_skills`（`mcp/config.py`）。
- stdio 子程序**不會繼承**父程序的環境變數，只拿到 MCP SDK 的安全預設值（PATH、HOME…）再加上 `config.env`，所以 model key 不會外洩給第三方 server（`build_mcp_server` docstring）。stdio 的 stderr 會導到 log 檔（`_stdio_log_file`）。
- **並行共用：** `MCPToolset.__aenter__/__aexit__` 用 `anyio.Lock` 加上 `_running_count` 做 reference count，第一個進入的建立連線，最後一個離開的關閉連線（`pydantic_ai/mcp.py:1194-1298`）。所以多個 session 並行 run 會共用**同一條**連線、同一個 stdio 子程序。工具清單有快取（`cache_tools=True`，收到 `list_changed` 時失效）。
  - 建議在 FastAPI lifespan 裡 `async with agent:`（`agent/__init__.py:4196`，會先啟動所有 MCP toolset），讓連線常駐，不必每個 run 重新 spawn stdio 子程序或重新握手。
  - 代價：共用的 MCP server 看不出是哪個使用者在呼叫。如果某個 server 需要**逐使用者的憑證**，就不要放進 agent，而是每個 run 自己建 `MCPToolset` 並用 `toolsets=[...]` 傳入（run 結束就關閉；stdio 的話等於每個 run spawn 一次）。
  - 一個 `anyio.Lock` 會綁定第一次使用它的 event loop（`_enter_lock` 註解），所以 toolset 必須在 service 的 event loop 內第一次使用，不能跨 thread 或跨 loop 共用。
- 這也對應 CONTEXT.md 的 **MCP Server List**：Studio 後端設定一份 `mcpServers` JSON，agent 從這裡列出現有工具。列出工具可以用 `probe_mcp_server(server)`，它會連線並 `list_tools`，有 timeout 且不會丟例外（`registry.py:402`）。

## 5. C# 後端呼叫的最小 HTTP 介面

依 POC 的 worker 介面改為逐 session，auth 由 C# 後端處理，Python service 只聽 localhost 或內網，並以 service token 驗證：

| Method | Path | 說明 |
|---|---|---|
| `POST` | `/sessions/{userId}/{interviewId}/runs` body `{prompt}` | 開始一個 run，回應即為 SSE（`text/event-stream`）。同一個 session 正在 run 或等待核准時回 409 |
| `GET` | `/sessions/{userId}/{interviewId}/events?after=N`（或 `Last-Event-ID`） | 重連/回放 SSE；`id:` 為序號 |
| `POST` | `/sessions/{userId}/{interviewId}/approvals` body `{tool_call_id, ok}` | 回覆核准，觸發續跑（新的 run），回應 SSE 或 202 再由 `/events` 讀 |
| `GET` | `/sessions/{userId}/{interviewId}` | `{state: idle|running|awaiting_approval, pending: [...], usage}` |
| `POST` | `/sessions/{userId}/{interviewId}/cancel` | `cancellation_token.cancel()` |

SSE 事件：`text{delta}`、`tool_call{id,name,args}`、`tool_result{id,content}`、`approval{id,name,args}`、`mcp_degraded{server,reason}`、`done{output,usage}`、`error{message}`，外加每 15 秒一次的 `: ping`。

C# 端（.NET）用 `HttpClient` 加上 `HttpCompletionOption.ResponseHeadersRead` 讀串流即可（.NET 9+ 也有 `System.Net.ServerSentEvents.SseParser`）。這部分是 .NET 的知識，不在本研究的一手來源範圍內。

## 6. 限制與風險

- **Windows**：`LocalWorkspaceBackend.__init__` 在 `os.name != 'posix'` 時丟出 `NotImplementedError`（`pydantic_ai/workspaces/local.py:119-125`）。不用容器的話，service 必須跑在 Linux（或 WSL）；否則只能用 `StateWorkspace`（記憶體內，重啟就消失），或自己實作 `WorkspaceBackend`。
- **`LocalWorkspace` 沒有隔離**：tool 能存取 process 權限範圍內的整個 host（`local_workspace.py` docstring）。多人共用的 process 沒有 per-user 容器這道邊界，所以要關掉 `include_execute`；檔案工具會被 confine 在 working_dir 內，但路徑逃逸的防護**未實測**；也要避免把機密放在 service 的工作目錄樹下。
- **版本變動快**：pydantic-deep 0.3.x 幾乎每週發版，README 和 PyPI 不同步（HANDOFF.md）；pydantic-ai 2.x 也是每週更新（2.57 → 2.63）。要固定版本，升級時重新驗證本文提到的行號與行為。
- 單一 process 要承受所有 session 的 model 呼叫，屬於 I/O bound，asyncio 足以應付。CPU 密集的 tool 或同步阻塞呼叫會拖慢所有人。
- 共用的 stdio MCP server 一旦 crash，所有 session 都會受影響；`make_resilient` 會在下一個 run 重試連線（`for_run` 會重設 `_failed`）。
- 本文對並行與 per-run workspace 的結論都是讀原始碼得來，**沒有做過兩個 session 並行的實測**；建議實作時先寫一個以 `FunctionModel` 跑兩個 session 並行的測試，驗證 workspace 與 history 不會互相洩漏。
