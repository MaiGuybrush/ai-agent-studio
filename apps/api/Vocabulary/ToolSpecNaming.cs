using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Submissions;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Workflow;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>Vocabulary 為 null 時看 Error（完全沒有詞彙快取，Naming Check 被擋下）。</summary>
public sealed record NamingRefresh(VocabularySnapshot? Vocabulary, string? Error);

/// <summary>
/// 對一張單的 Tool Spec 草稿跑 Naming Check，結果（每個欄位的狀態＋詞彙 sha）寫進各 spec 的 naming_check（spec 7.3）。
/// 草稿開啟與存檔時呼叫；詞彙 sha 落後就重跑。新命中只會改欄位狀態，不會動單的狀態。
/// </summary>
public class ToolSpecNaming(VocabularyService vocab, WorkflowDraftService drafts, PendingTermService pendingTerms, StudioDb db)
{
    public static string NoCache(string why) => $"無法取得 Tier1Vocabulary，Naming Check 暫停：{why}";

    public async Task<NamingRefresh> RefreshAsync(string ticketId, CancellationToken ct = default)
    {
        VocabularySnapshot v;
        try { v = await vocab.GetAsync(ct); }
        catch (VocabularyException e) { return new(null, NoCache(e.Message)); }

        var pending = (await pendingTerms.ActiveAsync(v.Entries, ct)).Select(x => x.Term).ToList();
        var all = (await drafts.GetAsync(ticketId, WorkflowDraft.ToolSpecsDoc)).Steps.OfType<JsonObject>().ToList();
        var frozen = await FrozenSpecs.IdsAsync(db, ticketId, all, ct); // 已實作且沒改動：以 interface_snapshot 為準，不重檢（spec 7.3）
        var specs = all.Where(s => !frozen.Contains(s["id"]!.GetValue<string>()));
        var results = specs.ToDictionary(s => s["id"]!.GetValue<string>(), s => (JsonNode)new JsonObject
        {
            ["sha"] = v.Sha,
            ["fields"] = Fields(v, s, pending),
        });
        await drafts.AnnotateAsync(ticketId, WorkflowDraft.ToolSpecsDoc, "naming_check", results);
        return new(v, null);
    }

    /// <summary>修改後重評用：只檢查「相對 baseline 新增（或改名）的欄位路徑」。回傳 (欄位路徑 → 結果, 錯誤)；完全沒有詞彙時 Error 有值。</summary>
    public async Task<(JsonObject? Fields, string? Error)> CheckChangedAsync(JsonObject baseline, JsonObject next, CancellationToken ct = default)
    {
        VocabularySnapshot v;
        try { v = await vocab.GetAsync(ct); }
        catch (VocabularyException e) { return (null, NoCache(e.Message)); }
        var pending = (await pendingTerms.ActiveAsync(v.Entries, ct)).Select(x => x.Term).ToList();
        var before = Fields(v, baseline, pending);
        var changed = new JsonObject();
        foreach (var (path, r) in Fields(v, next, pending))
            if (!before.ContainsKey(path)) changed[path] = r?.DeepClone();
        return (changed, null);
    }

    static JsonObject Fields(VocabularySnapshot v, JsonObject spec, IReadOnlyCollection<string> pending)
    {
        var o = new JsonObject();
        foreach (var root in new[] { "inputs", "outputs" })
            foreach (var (path, r) in NamingChecker.CheckSchema(v.Entries, spec[root], root, pending))
                o[path] = new JsonObject { ["status"] = r.Status, ["suggestion"] = r.Suggestion };
        return o;
    }
}
