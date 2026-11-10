using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Workflow;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>Vocabulary 為 null 時看 Error（完全沒有詞彙快取，Naming Check 被擋下）。</summary>
public sealed record NamingRefresh(VocabularySnapshot? Vocabulary, string? Error);

/// <summary>
/// 對一張單的 Tool Spec 草稿跑 Naming Check，結果（每個欄位的狀態＋詞彙 sha）寫進各 spec 的 naming_check（spec 7.3）。
/// 草稿開啟與存檔時呼叫；詞彙 sha 落後就重跑。新命中只會改欄位狀態，不會動單的狀態。
/// </summary>
public class ToolSpecNaming(VocabularyService vocab, WorkflowDraftService drafts, PendingTermService pendingTerms)
{
    public static string NoCache(string why) => $"無法取得 Tier1Vocabulary，Naming Check 暫停：{why}";

    public async Task<NamingRefresh> RefreshAsync(string ticketId, CancellationToken ct = default)
    {
        VocabularySnapshot v;
        try { v = await vocab.GetAsync(ct); }
        catch (VocabularyException e) { return new(null, NoCache(e.Message)); }

        var pending = (await pendingTerms.ActiveAsync(v.Entries, ct)).Select(x => x.Term).ToList();
        var specs = (await drafts.GetAsync(ticketId, WorkflowDraft.ToolSpecsDoc)).Steps.OfType<JsonObject>();
        var results = specs.ToDictionary(s => s["id"]!.GetValue<string>(), s => (JsonNode)new JsonObject
        {
            ["sha"] = v.Sha,
            ["fields"] = Fields(v, s, pending),
        });
        await drafts.AnnotateAsync(ticketId, WorkflowDraft.ToolSpecsDoc, "naming_check", results);
        return new(v, null);
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
