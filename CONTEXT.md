# AI Agent Studio

工廠 CIM 部門的內部網站：CIM 工程師與使用者在此與 agent 協作，把使用者的日常工作流程轉成「MCP 工具規格」與「skills」，供公司雲端 agent 平台註冊使用。

## Language

**Requirement Ticket**:
使用者在 eCIM 系統提出的 AI agent 需求單，以需求單號識別；Studio 內以該單號串起後續所有成品。
_Avoid_: 工單、case

**CIM Engineer**:
被指派訪談使用者、開發 MCP 工具的 CIM 工程師；在 Studio 中主導 Interview。
_Avoid_: 開發者、IT

**Business User**:
了解系統功能、提出需求並撰寫 skills 的使用者；不負責串接系統資料。
_Avoid_: 客戶、end user

**System Owner**:
某個 MCP 工具所涉及系統的負責人；對該工具做 Feasibility Review。
_Avoid_: 管理員、維運

**Interview**:
CIM Engineer 與 agent 以 grill 方式逐題追問，釐清一張 Requirement Ticket 的需求，直到產出 Workflow Definition 與 Tool Spec 的一次協作。
_Avoid_: 問卷、chat

**Workflow Definition**:
Business User 日常任務的步驟序列，含各步使用的工具、輸入輸出與需人工核准之處。
_Avoid_: 流程圖、pipeline

**Tool Spec**:
一個 MCP 工具的規格：用途、input/output 欄位、對應系統與 System Owner；欄位名稱須通過 Naming Check。
_Avoid_: 規格書、API 文件

**MCP Server List**:
Studio 後端設定的 MCP server 清單；agent 由此列舉現有工具，判斷可直接套用、需擴增或需新增。
_Avoid_: 工具清單、Registry

**Naming Check**:
以 `Tier1Vocabulary` 檢查 Tool Spec 欄位名稱：命中別名則提醒改為標準名稱，查無則視為新名詞交由使用者確認。
_Avoid_: lint、驗證

**Feasibility Review**:
System Owner 對單一 Tool Spec 回覆的評估，結果為可行、不可行或附條件，並記錄原因與預估工時。
_Avoid_: 審核、簽核

**Skill**:
Business User 撰寫的任務說明，指示雲端 agent 如何使用 MCP 工具完成 Workflow Definition。
_Avoid_: prompt、腳本

**Skill Authoring**:
Business User 與 agent 在 Studio 協作產出 Skill 的過程；僅在所有 Tool Spec 實作完成後進行。

**Cloud Agent Platform**:
公司的雲端 agent 平台；Skill 與 MCP 工具完成後在此手動註冊才能使用，不在 Studio 範圍內。
_Avoid_: 正式環境
