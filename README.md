# ai-agent-studio

規格見 [docs/spec/v1.md](docs/spec/v1.md)，名詞見 [CONTEXT.md](CONTEXT.md)。

| 目錄 | 內容 | 預設埠 |
|---|---|---|
| `apps/web` | Angular（部門 template） | 4200 |
| `apps/api` | C#（部門 ApiTemplate），**唯一能連 DB 的應用** | 5080 |
| `apps/agent` | Python（pydantic-ai + uvicorn），只綁 127.0.0.1，不連 DB | 8100 |
| `apps/api.tests` | C# 測試（xunit，預設不需 DB、不需網路；設 `AAS_PG` 才連真 PostgreSQL，見〈測試〉） | |
| `tools/mock-uac.mjs` | 本機 mock UAC（含 HrEmployee、basic-info） | 5090 |

## 步驟 0：本機 PostgreSQL（podman；只有 C# 連線）

API 啟動時需要 DB（需求單、`ticket_event` 等；EF Core migrations 在 `apps/api/Tickets/Migrations`，API 啟動時自動套用）。**先起 PostgreSQL，再啟動 API**，否則 API 連不上 DB。下列指令為單行，PowerShell 與 bash 都能直接貼上。

1. 安裝 [podman](https://podman.io/)。Windows／macOS 需先建立並啟動 VM：
   ```
   podman machine init     # 只需做一次
   podman machine start    # 每次重開機後都要
   ```
2. 首次建立容器：
   ```
   podman run -d --name aas-pg -p 127.0.0.1:5432:5432 -e POSTGRES_USER=studio -e POSTGRES_PASSWORD=studio -e POSTGRES_DB=studio -v aas-pg-data:/var/lib/postgresql/data docker.io/library/postgres:16
   ```
   已建立過則 `podman start aas-pg`。
3. 確認已啟動：`podman ps --filter name=aas-pg`（STATUS 為 `Up`）；`podman exec aas-pg pg_isready -U studio` 回 `accepting connections`。

連線字串（`apps/api/appsettings.Development.json` 已設好；放 C# 設定 `ConnectionStrings:Studio`，**不要**給 agent 或 web）：
`Host=localhost;Port=5432;Database=studio;Username=studio;Password=studio`。

常見問題：
- `port is already allocated` / 5432 被占用：本機已有其他 PostgreSQL。停掉它，或改 `-p 127.0.0.1:5433:5432` 並同步修改連線字串的 `Port`。
- `Cannot connect to Podman`：還沒 `podman machine start`。
- 停止：`podman stop aas-pg`；清空重來：`podman rm -f aas-pg && podman volume rm aas-pg-data`。

## 本機啟動（四個終端機，另有選用的 mock MCP）

先完成〈步驟 0〉。

```
node tools/mock-uac.mjs                       # 5090；mock 重啟後金鑰會變，API 需一起重啟
dotnet run --project apps/api                 # 5080，Development 設定已指向 mock UAC，Cim:DeptCodePrefixes = ["CIM"]
cd apps/agent && python -m venv .venv && .venv/Scripts/python -m pip install -r requirements.txt \
  && .venv/Scripts/python -m uvicorn app.main:app --host 127.0.0.1 --port 8100
cd apps/web && pnpm install && pnpm start     # 4200，需 pnpm
node .\tools\mock-mcp.mjs                     # 選項, 若無mcp server時，可使用mock
```

打開 http://localhost:4200 ，在 mock 登入頁選使用者（見 `tools/mock-users.json`）：
`10000001`（CIM100，是 CIM Engineer）、`10000002`（MFG200，否）、`10000003`（無部門，fail closed 並顯示錯誤）。

## Interview（#17）

- Python agent：複製 `apps/agent/config.example.toml` 為 `config.toml`（不進 git），填 C# 呼叫用的 `api_key` 與公司核准的模型端點；agent 指令在 `apps/agent/app/instructions.md`（git 管理）。啟動後綁 `127.0.0.1:8100`。
- C# 設定：`Agent:BaseUrl`（預設 `http://127.0.0.1:8100`）、`Agent:ApiKey`（須等於 Python 的 `api_key`）、`Agent:ToolApiBaseUrl`（Python 回呼的 tool API，預設 `http://127.0.0.1:5080`）、`Agent:RunTokenSecret`（未設則每次啟動隨機）。
- API：`GET/POST /api/tickets/{id}/interview[/messages|/events|/cancel]`；`POST messages` 的回應即 SSE 串流，斷線用 `GET events` + `Last-Event-ID` 重連。內部 tool API：`/internal/tools/*`，只認 `X-Run-Token`。
- 畫面：`#/tickets`（清單、建單）、`#/tickets/{id}`（取消／重開／改派）、`#/tickets/{id}/interview`（三欄）。

## 測試

```
dotnet test apps/api.tests                              # 身分／權限／需求單（EF InMemory，不需 DB）
# 連真 PostgreSQL 一併驗證 migrations（需先完成〈步驟 0〉）。注意：會對 `studio` 庫寫入測試資料，結束時自動清除；開發中的資料不受影響。
#   bash：      AAS_PG="Host=localhost;Port=5432;Database=studio;Username=studio;Password=studio" dotnet test apps/api.tests
#   PowerShell：$env:AAS_PG="Host=localhost;Port=5432;Database=studio;Username=studio;Password=studio"; dotnet test apps/api.tests
# 新增 migration：dotnet tool restore && dotnet ef migrations add <Name> --project apps/api --output-dir Tickets/Migrations
node --test tools/mock-uac.test.mjs                     # mock UAC
cd apps/web && pnpm exec ng test --watch=false          # 前端
cd apps/agent && .venv/Scripts/python -m pytest -q      # agent /health
```

## 設定

- `apps/api/appsettings.json`：`Cim:DeptCodePrefixes`（正式環境 CIM 部門代碼前綴；預設空 = 無人是 CIM Engineer；含空字串啟動失敗）。
- `Authentication:Uac:BaseUrls`：有值時直接使用（本機 mock），否則由 Consul 取得 UAC 位址。
- `apps/web/src/assets/config.json`：`authApiUrl`、`apiBaseUrl`；部署時覆寫。

## 系統設定檔（#19）

- `apps/api/studio.config.json`（路徑可用 `Studio:ConfigPath` 改）：`mcpServers`（`type` 只支援 `http`／`stdio`；秘密寫 `${VAR}` 從環境變數帶入）、`systems`（系統 → System Owner 工號）、`cimDeptCodes`。改檔後下次讀取自動重載，環境變數未設會報錯。
- MCP 工具清單記憶體快取 5 分鐘，key 含 server 連線設定（即憑證身分）；跨 server 同名工具以 `{server}__{tool}` 區分。
