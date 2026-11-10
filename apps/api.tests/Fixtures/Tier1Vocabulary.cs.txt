using System;
using System.Collections.Generic;
using System.Linq;

namespace McpGateway.Analyzers;

internal class GlossaryEntry
{
    public string StandardCamel { get; }
    public string StandardPascal { get; }
    public string Description { get; }
    public string[] Aliases { get; }

    public GlossaryEntry(string standardCamel, string standardPascal, string description, string[] aliases)
    {
        StandardCamel = standardCamel;
        StandardPascal = standardPascal;
        Description = description;
        Aliases = aliases;
    }
}

internal static class Tier1Vocabulary
{
    // 詞條依主題分段（Issue #14 §5.4）：分段順序即 mcp-tool-dto-builder Skill
    // 讀入詞庫時的比對順序，近義詞必須同段，Agent 才會確實辨析。
    // 新增詞條時請歸入對應段落；無對應段落的新領域才另開一段。
    public static readonly GlossaryEntry[] Entries = new[]
    {
        // 產品與版本
        new GlossaryEntry("productId", "ProductId", "產品料號或唯一代碼 (Product ID)", new[] { "productCode", "prodId", "itemNo", "partNo", "materialNo", "PROD_ID" }),
        new GlossaryEntry("productName", "ProductName", "產品品名或規格名稱", new[] { "prodName", "itemDesc", "PROD_DESC", "PROD_NAME" }),
        new GlossaryEntry("ecCode", "EcCode", "產品版本/工程變更代碼 (Engineering Change Code)", new[] { "engineeringChangeCode", "ecNo" }),

        // 設備與製程資源（階層：eqptId 主設備 → subEqptId 子設備/Unit → chamberId Chamber；portId 為裝卸埠）
        new GlossaryEntry("eqptId", "EqptId", "主設備/機台識別碼 (Equipment ID)，設備階層最上層。其下子設備用 subEqptId、Chamber 用 chamberId、裝卸埠用 portId；不可混用。", new[] { "equipmentId", "machineId", "toolId", "machNo", "eqpId", "EQP_ID" }),
        new GlossaryEntry("subEqptId", "SubEqptId", "主設備下的子設備/Unit ID（階層：eqptId → subEqptId）。若指 Chamber 製程腔體請用 chamberId，指裝卸埠請用 portId。", new[] { "subEquipmentId", "subMachineId", "unitId", "subToolId" }),
        new GlossaryEntry("chamberId", "ChamberId", "設備的 Chamber（製程腔體）識別碼，為 subEqptId 之下的更細層級。指整台主設備請用 eqptId，指 Unit 請用 subEqptId。", new[] { "chId", "chamberNo", "slotId" }),
        new GlossaryEntry("portId", "PortId", "設備裝卸埠/傳載埠（Load Port）代碼，是載具進出設備的位置，非設備本體。指設備用 eqptId，指 Chamber 用 chamberId。", new[] { "port_id", "portNo", "lpId" }),
        new GlossaryEntry("recipeId", "RecipeId", "設備配方/製程程式代碼，為設備運行的程式代號，非設備識別碼（設備用 eqptId）、非製程站點（站點用 stepId）。", new[] { "recipeName", "ppId", "recipeCode" }),

        // 載具與生產對象（包含階層：carrierId 載具 → lotId 批號 → glassId 玻璃 → panelId 切割面板；waferId 為晶圓片）
        new GlossaryEntry("lotId", "LotId", "生產批號，生產對象階層的追蹤主體（一個 lot 可含多片 glassId）。指載具用 carrierId，指工單用 workOrderId，指單片玻璃用 glassId。", new[] { "lotNo", "batchId", "batchNo", "LOT_ID" }),
        new GlossaryEntry("workOrderId", "WorkOrderId", "工單編號，計畫性生產的訂單主體。不同於 lotId（生產批號，實際流過製程的單位）與 processId（製程代碼）。", new[] { "wo", "woId", "moId", "orderNo", "shopOrder", "WO_ID" }),
        new GlossaryEntry("carrierId", "CarrierId", "載具（FOUP/晶舟/卡匣/CST）編號，用於搬送與儲存的實體容器。不同於 lotId（生產批號，邏輯追蹤單位）與 patiId（存放位置代碼）。", new[] { "foupId", "cassetteId", "cstId", "carrierNo" }),
        new GlossaryEntry("glassId", "GlassId", "面板玻璃基板 ID（未切割前的整片玻璃，一片 glassId 含多個 panelId）。指切割後面板請用 panelId，指晶圓請用 waferId。", new[] { "sheetId", "substrateId", "panelGlassId", "GLASS_ID" }),
        new GlossaryEntry("panelId", "PanelId", "切割後面板/合板 ID，為 glassId 切割後的單位。指未切割的整片玻璃請用 glassId。", new[] { "pnlId", "subPanelId", "unitPanelId", "PANEL_ID" }),
        new GlossaryEntry("waferId", "WaferId", "晶圓片 ID (Wafer ID)，晶圓製程的單片追蹤單位。面板製程請用 glassId（整片）或 panelId（切割後）。", new[] { "waferNo", "dieId", "WAFER_ID" }),

        // 製程站點與產線
        // 註：processId（製程代碼）與 stepId（站點/工序代碼）為不同概念，各自為獨立標準詞彙。
        // 舊版 CoreGlossary.cs 曾將 processId 列為 stepId 的別名，Issue #11 收斂時已排除，
        // 避免 FindByAlias 對同一別名產生兩個詞彙的歧義對應（由 Tier1VocabularyConsistencyTests 強制）。
        new GlossaryEntry("processId", "ProcessId", "製程/工藝代碼，描述一批貨要走的路線（製程流程本身）。指路線上的單一站點請用 stepId，兩者為不同概念、不得互為別名。", new[] { "procId", "processCode", "processName" }),
        new GlossaryEntry("stepId", "StepId", "製程站點/工序代碼，processId 路線上的單一作業站點。指整條製程路線請用 processId，指站點名稱請用 stepName。", new[] { "stageId", "operationId", "operId", "STEP_ID" }),
        new GlossaryEntry("stepName", "StepName", "製程站點/工序的顯示名稱（供人閱讀）。作為條件比對或回傳代碼時請用 stepId。", new[] { "operationName", "stageName" }),
        new GlossaryEntry("workCenter", "WorkCenter", "生產工作中心/工作站群代碼，為組織與排程用途的站點分組。不同於 stepId（製程工序代碼）與 productLine（線別）。", new[] { "wc", "workCenterCode", "stationGroup" }),
        new GlossaryEntry("productLine", "ProductLine", "生產線別代碼（如 TFT3/TFT6），指實體產線。不同於 workCenter（工作中心分組）與 eqptId（單台設備）。", new[] { "line", "productionLine", "lineNo", "lineId" }),
        new GlossaryEntry("ownerId", "OwnerId", "批貨擁有者類別代碼（如 PROD 正常貨、ENG 工程貨、INT 整合），為分類代碼而非人員識別。", new[] { "ownerType", "ownerCode" }),

        // 時間與持續時間
        // 註：本段各詞語意互斥，不得互為別名——持續時數（stayHours/startHours，小數小時）、
        // 一日内時刻（startTime，HH:mm:ss）、日期（startDate/endDate，yyyy-MM-dd）、絕對時間戳記（scheduledCompletion/generatedAt）。
        // 曾因 startTime 誤列為 startHours 的別名，導致下游時刻欄位被 MCP0010 誤建議改名，見 Issue #14。
        // 註：stayTime/dwellTime/targetDate/dueTime/createTime/reportTime 等 *Time/*Date 形式別名
        // 依 Issue #14 §6 #4 決議保留，由描述中的「若欄位為 X 請用 Y」辨析承擔判斷依據，不做移除。
        new GlossaryEntry("stayHours", "StayHours", "當前製程站的停留持續時間，小數小時（例 26.205），下游來源欄位常為 STAY_HRS。是持續時數而非時刻或日期；若欄位為一日内時刻請用 startTime，僅為日期請用 startDate。", new[] { "stayTime", "dwellTime", "stationStayHours" }),
        new GlossaryEntry("startHours", "StartHours", "自下線（上機）起算的持續時數，小數小時（例 304.969），下游來源欄位常為 START_HRS。是持續時數而非時刻或日期；若欄位為一日内時刻請用 startTime，僅為日期請用 startDate。", new[] { "downTime", "startDurationHours" }),
        new GlossaryEntry("startTime", "StartTime", "一日内時刻，僅含時分秒 HH:mm:ss（例 14:30:00），不含日期與持續時間。不同於 startHours/stayHours（小數時數）與 startDate（日期）。", new[] { "timeOfDay", "clockTime", "timeStr" }),
        new GlossaryEntry("startDate", "StartDate", "日曆日期，僅含年月日 yyyy-MM-dd（例 2026-10-22），常用於查詢區間起日。不同於 startTime（時刻）、generatedAt（時間戳記）與 startHours（時數）。", new[] { "beginDate", "fromDate", "dateStr" }),
        new GlossaryEntry("endDate", "EndDate", "日曆日期，僅含年月日 yyyy-MM-dd（例 2026-10-22），常用於查詢區間迄日，與 startDate 配對使用。不同於 scheduledCompletion（預計完工時間戳記）與 startTime（時刻）。", new[] { "finishDate", "toDate", "dateEnd" }),
        new GlossaryEntry("scheduledCompletion", "ScheduledCompletion", "預計完工的絕對日期時間（含日期與時刻）。若欄位僅為日期請用 startDate/endDate，僅為時刻請用 startTime。", new[] { "estimatedCompletion", "targetDate", "dueTime" }),
        new GlossaryEntry("generatedAt", "GeneratedAt", "報告/資料產出之 UTC 時間戳記（ISO 8601，例 2026-10-22T05:30:00Z）。若欄位僅為日期請用 startDate/endDate，僅為時刻請用 startTime。", new[] { "createTime", "reportTime", "generatedTime" }),

        // 計數與良率指標（計數主體各不相同；yieldRate 為比率 0.0~1.0，非數量）
        new GlossaryEntry("lotCnt", "LotCnt", "LOT（批號）筆數，計數主體為 lotId。不同於 sheetCnt（玻璃片數）、panelCnt（面板數）與 totalItems（查詢筆數）。", new[] { "lotCount", "batchCount" }),
        new GlossaryEntry("sheetCnt", "SheetCnt", "玻璃基板片數，計數主體為 glassId。不同於 lotCnt（LOT 筆數）、panelCnt（一片玻璃含多個 panel，兩者數值不同）與 totalItems（查詢筆數）。", new[] { "sheetCount", "substrateCount" }),
        new GlossaryEntry("panelCnt", "PanelCnt", "Panel 數量，計數主體為 panelId（切割後面板，單一 glassId 含多個 panel）。不同於 sheetCnt（玻璃片數）與 lotCnt（LOT 筆數）。", new[] { "panelCount", "unitPanelCount" }),
        new GlossaryEntry("quantityInProcess", "QuantityInProcess", "目前在製中（尚未完工）的數量。與 quantityCompleted（已完工數量）互斥，兩者不可相加為同一統計；為絕對數量，非比率。", new[] { "wipQty", "inProcessQty", "qtyInProcess" }),
        new GlossaryEntry("quantityCompleted", "QuantityCompleted", "該工單/項目已完工入庫的數量。與 quantityInProcess（在製數量）互斥；為絕對數量，非比率。", new[] { "completedQty", "doneQty", "finishQty" }),
        new GlossaryEntry("totalItems", "TotalItems", "符合查詢條件的項目總筆數（分頁/結果集計數），非生產數量。不同於 lotCnt/sheetCnt/panelCnt（生產計量）與 quantityInProcess（在製數量）。", new[] { "totalCount", "itemCount", "total" }),
        new GlossaryEntry("defectCount", "DefectCount", "缺陷/不良品的絕對數量。若需良率請用 yieldRate（比率 0.0~1.0），兩者不可互為換算替代。", new[] { "ngQty", "scrapQty", "failCount" }),
        new GlossaryEntry("yieldRate", "YieldRate", "生產/檢驗良率，為 0.0~1.0 的比率小數（例 0.985 表示 98.5%），非百分比整數、非絕對數量。若需不良數量請用 defectCount。", new[] { "yield", "yieldRatio", "passRate" }),

        // 搬送/倉儲位置 (Material Handling / AMHS Location；階層：zoneId 區域 → shelfId 貨架層位 → patiId 放置位置)
        new GlossaryEntry("zoneId", "ZoneId", "自動倉儲 (AMHS) 區域代碼，位置階層最粗層（zoneId → shelfId → patiId）。指細位請用下層詞條，指設備裝卸埠請用 portId。", new[] { "zoneNo", "zoneCode", "zone_id" }),
        new GlossaryEntry("shelfId", "ShelfId", "貨架層位代碼，AMHS 位置階層的中間層（zoneId 之下的 shelf）。指可放置的細位地址請用 patiId。", new[] { "shelfNo", "rackId", "shelf_id" }),
        new GlossaryEntry("patiId", "PatiId", "放置位置（Slot Address）代碼，AMHS 位置階層的最細層，載具實際停放點。指區域/層位請用 zoneId/shelfId。", new[] { "placeId", "slotAddressId", "pati_id" }),
        new GlossaryEntry("position", "Position", "載具/面板的目前位置代碼（即時位置，可能為設備、埠或存放點的組合值）。若需明確指定 AMHS 存放地址請用 patiId。", new[] { "posId", "locationCode" }),

        // 狀態與優先等級
        new GlossaryEntry("status", "Status", "目前狀態代碼或說明（如: In Progress, Completed）", new[] { "state", "statusCode" }),
        new GlossaryEntry("priority", "Priority", "生產優先等級（如: Normal, High, Urgent）", new[] { "priorityCode", "priLevel" }),
    };

    public static GlossaryEntry? FindByAlias(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        return Entries.FirstOrDefault(e =>
            e.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));
    }
}
