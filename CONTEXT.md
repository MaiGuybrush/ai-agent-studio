# AI Agent Studio

工廠 CIM 部門的內部網站：CIM 工程師與使用者在此與 agent 協作，把使用者的日常工作流程轉成「MCP 工具規格」與「skills」，供公司雲端 agent 平台註冊使用。

## Language

**Requirement Ticket**:
使用者在 eCIM 系統提出的 AI agent 需求單，以需求單號識別；Studio 內以該單號串起後續所有成品。由一位 CIM Engineer 建單並負責（可改派），綁定一位 Business User。
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
一個 MCP 工具的規格：用途、input/output 欄位、對應系統與 System Owner、是否有副作用；欄位名稱須通過 Naming Check。只屬於一張 Requirement Ticket。
_Avoid_: 規格書、API 文件

**Tool Spec Revision**:
送出 Interview 時凍結的一份 Tool Spec 內容，不可變；Feasibility Review 針對特定 revision。內容有任何改動（含說明文字）才產生新 revision。
_Avoid_: 版本、草稿

**Tool Spec Kind**:
Tool Spec 對照 MCP Server List 的分類：直接套用（Reuse）、擴增（Extend）或新增（New）。Reuse 不需 Feasibility Review 與實作。
_Avoid_: 工具類型

**MCP Server List**:
Studio 後端設定的 MCP server 清單；agent 由此列舉現有工具，判斷可直接套用、需擴增或需新增。
_Avoid_: 工具清單、Registry

**Naming Check**:
以 `Tier1Vocabulary` 檢查 Tool Spec 欄位名稱：與標準名稱完全一致才通過；僅大小寫不同或命中別名則提醒改為標準名稱；查無則視為新名詞交由使用者確認。
_Avoid_: lint、驗證

**Pending Term**:
經 CIM Engineer 確認、已提案但尚未收錄進 `Tier1Vocabulary` 的新名詞；Naming Check 視為已知，收錄後即移除。
_Avoid_: 自訂詞彙、Tier 2

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

**Requirement Ticket Status**:
Requirement Ticket 的進度；除「訪談中」「已取消」「已完成」由人觸發外，其餘由所屬 Tool Spec 的狀態推導。
_Avoid_: 工單狀態

**Cancellation**:
CIM Engineer 附原因終止一張 Requirement Ticket；可重開，重開後回到取消前狀態。
_Avoid_: 刪除、結案

**Interface Compliance Check**:
Tool Spec 標為已實作前的檢查：以 CIM Engineer 提供的 MCP server 與工具名稱取得已發佈工具介面，確認工具存在且 Tool Spec 欄位名稱與型別相符；通過時保存介面快照。
_Avoid_: 驗收、上線檢查
