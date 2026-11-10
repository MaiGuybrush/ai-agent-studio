using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Vocabulary;
using AiAgentStudio.Api.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Submissions;

/// <summary>阻擋送出的一項待處理事項。Code：no_tool_specs／missing_field／owner／new_term／rename_reason／naming_unavailable。</summary>
public sealed record Blocker(string Code, string Message, string? SpecId = null, string? Path = null);

/// <summary>送出前檢查與送出（spec 8.5、6.5）。業務規則只在這裡；agent 沒有對應工具，也沒有 run token 能呼叫的路由。</summary>
public class SubmissionService(StudioDb db, WorkflowDraftService drafts, ToolSpecNaming naming, StudioConfigProvider config, IUserDirectory directory, TimeProvider time)
{
    static readonly System.Text.Json.JsonSerializerOptions J = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    const string Specs = WorkflowDraft.ToolSpecsDoc;

    /// <summary>目前的阻擋清單；會先重跑 Naming Check（詞彙 sha 落後才真的重算），所以結果以最新詞彙為準。</summary>
    public async Task<List<Blocker>> BlockersAsync(string ticketId, CancellationToken ct)
    {
        var refresh = await naming.RefreshAsync(ticketId, ct);
        var specs = (await drafts.GetAsync(ticketId, Specs)).Steps.OfType<JsonObject>().ToList();
        var res = new List<Blocker>();
        if (specs.Count == 0) return [new("no_tool_specs", "還沒有任何 Tool Spec。")];
        if (refresh.Error is not null) res.Add(new("naming_unavailable", refresh.Error));

        foreach (var s in specs)
        {
            var id = s["id"]!.GetValue<string>();
            var missing = ToolSpecRules.Missing(s).Where(k => k != "system_owner").ToArray();
            if (missing.Length > 0) res.Add(new("missing_field", $"{id}：缺少必填欄位 {string.Join("、", missing)}。", id));
            if (s["kind"]?.GetValue<string>() != "Reuse")
            {
                var owner = s["system_owner"] is JsonValue v && v.TryGetValue<string>(out var o) ? o : null;
                if (string.IsNullOrWhiteSpace(owner)) res.Add(new("owner", $"{id}：尚未指定 System Owner。", id));
                else if (await directory.FindAsync(owner.Trim(), ct) is null) res.Add(new("owner", $"{id}：查無 System Owner 工號 {owner}。", id));
            }
            if (s["naming_check"]?["fields"] is not JsonObject fields) continue;
            foreach (var (path, r) in fields)
            {
                var status = r?["status"]?.GetValue<string>();
                if (status == "new") res.Add(new("new_term", $"{id}：{path} 是新名詞，請確認並填寫說明。", id, path));
                else if (status == "rename" && string.IsNullOrWhiteSpace(Reason(s, path)))
                    res.Add(new("rename_reason", $"{id}：{path} 建議改成 {r?["suggestion"]}，不改請填理由。", id, path));
            }
        }
        return res;
    }

    /// <summary>理由記在欄位所屬 spec 的 naming_reasons[欄位路徑]。</summary>
    static string? Reason(JsonObject spec, string path) =>
        spec["naming_reasons"] is JsonObject o && o[path] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>送出；呼叫端已確認權限。有阻擋事項時不動任何資料，回傳阻擋清單。</summary>
    public async Task<(int? Number, List<Blocker> Blockers)> SubmitAsync(RequirementTicket t, string empNo, CancellationToken ct)
    {
        var blockers = await BlockersAsync(t.Id, ct);
        if (blockers.Count > 0) return (null, blockers);

        var workflow = (await drafts.GetAsync(t.Id)).Steps;
        var specs = (await drafts.GetAsync(t.Id, Specs)).Steps.OfType<JsonObject>().ToList();
        await drafts.ClearHumanEditsAsync(t.Id);

        var number = (await db.Submissions.Where(x => x.TicketId == t.Id).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        db.Submissions.Add(new Submission { TicketId = t.Id, Number = number, WorkflowJson = workflow.ToJsonString(J), SubmittedByEmpNo = empNo, At = time.GetUtcNow() });

        var records = await db.ToolSpecRecords.Where(x => x.TicketId == t.Id).ToListAsync(ct);
        var revisions = await db.ToolSpecRevisions.Where(x => x.TicketId == t.Id).ToListAsync(ct);
        foreach (var gone in records.Where(r => !specs.Any(s => s["id"]!.GetValue<string>() == r.SpecId))) db.ToolSpecRecords.Remove(gone); // 草稿已刪除的 spec
        foreach (var s in specs)
        {
            var id = s["id"]!.GetValue<string>();
            var content = (JsonObject)s.DeepClone();
            content.Remove("naming_check"); // 衍生欄位，不屬於內容
            if (content["system_owner"] is JsonValue ov && ov.TryGetValue<string>(out var ownerNo) && !string.IsNullOrWhiteSpace(ownerNo))
                content["system_owner_name"] = (await directory.FindAsync(ownerNo.Trim(), ct))?.Name;
            var rec = records.FirstOrDefault(r => r.SpecId == id);
            var last = rec is null ? null : revisions.FirstOrDefault(r => r.SpecId == id && r.Revision == rec.CurrentRevision);
            if (rec is not null && last is not null && JsonNode.DeepEquals(JsonNode.Parse(last.ContentJson), content)) continue; // 沒改動：保留原 revision 與評估結果
            var kind = s["kind"]!.GetValue<string>();
            rec ??= db.ToolSpecRecords.Add(new ToolSpecRecord { TicketId = t.Id, SpecId = id }).Entity;
            rec.Kind = kind;
            rec.CurrentRevision = (rec.CurrentRevision) + 1;
            rec.State = kind == "Reuse" ? ToolSpecState.Implemented : ToolSpecState.Pending; // Reuse 不需要 Feasibility Review
            db.ToolSpecRevisions.Add(new ToolSpecRevision { TicketId = t.Id, SpecId = id, Revision = rec.CurrentRevision, SubmissionNumber = number, ContentJson = content.ToJsonString(J) });
        }
        db.TicketEvents.Add(new TicketEvent { TicketId = t.Id, Type = "Submit", ActorEmpNo = empNo, At = time.GetUtcNow(), FromValue = t.Status, ToValue = TicketStatus.Submitted, Reason = $"submission #{number}" });
        t.Status = TicketStatus.Submitted;
        await db.SaveChangesAsync(ct);
        return (number, []);
    }

    /// <summary>需求單的顯示狀態；一次查多張單的 Tool Spec 狀態。</summary>
    public static async Task<Dictionary<string, string>> StatusesAsync(StudioDb db, IReadOnlyCollection<RequirementTicket> tickets, CancellationToken ct)
    {
        var ids = tickets.Where(t => t.Status == TicketStatus.Submitted).Select(t => t.Id).ToList();
        var states = (await db.ToolSpecRecords.AsNoTracking().Where(r => ids.Contains(r.TicketId)).Select(r => new { r.TicketId, r.State }).ToListAsync(ct))
            .ToLookup(x => x.TicketId, x => x.State);
        return tickets.ToDictionary(t => t.Id, t => TicketStatusDeriver.Derive(t.Status, states[t.Id]));
    }

    /// <summary>系統對照表的建議 System Owner。用清單而非字典回傳：JSON 序列化會改動字典的 key 大小寫。</summary>
    public async Task<List<OwnerSuggestion>> OwnerSuggestionsAsync(CancellationToken ct)
    {
        var res = new List<OwnerSuggestion>();
        foreach (var (system, empNo) in config.Current.Systems.Where(p => p.Value != ""))
            res.Add(new(system, empNo, (await directory.FindAsync(empNo, ct))?.Name));
        return res;
    }
}

public sealed record OwnerSuggestion(string System, string EmpNo, string? Name);
