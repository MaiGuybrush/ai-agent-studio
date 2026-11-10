using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AiAgentStudio.Api.Tickets;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Workflow;

/// <summary>
/// 每張單每種文件一份草稿（spec 6.3、13）。steps：{"steps":[{id, description, tool_spec, inputs, outputs, needs_approval, condition}]}；
/// tool_specs：{"tool_specs":[{id, kind, purpose, ...}]}。欄位路徑：{doc}.{id}（整筆）、{doc}.{id}.{field}、order（順序）。
/// </summary>
public class WorkflowDraft
{
    public const string StepsDoc = "steps", ToolSpecsDoc = "tool_specs";
    public string TicketId { get; set; } = "";
    /// <summary>同一張單的哪一份草稿：steps（Workflow Definition）或 tool_specs（Tool Spec，#21）。同時是 JSON 集合名與欄位路徑前綴。</summary>
    public string Doc { get; set; } = StepsDoc;
    public int Version { get; set; }
    public string DraftJson { get; set; } = EmptyJson;
    /// <summary>agent 上次看過的草稿（含 agent 自己的寫入）；與目前草稿比對即「人工修改的 diff」。</summary>
    public string SeenJson { get; set; } = EmptyJson;
    /// <summary>人工修改過的欄位路徑（JSON 陣列），保留到 Interview 送出為止。</summary>
    public string HumanEditsJson { get; set; } = "[]";
    public static string Empty(string doc) => $"{{\"{doc}\":[]}}";
    public const string EmptyJson = "{\"steps\":[]}";
}

public sealed record DraftView(int Version, JsonArray Steps, string[] HumanEdits);
public sealed record Rejection(string Path, JsonNode? Current);
/// <summary>Status：Ok／Rejected（Rejected 帶目前的值）／NotFound。</summary>
public sealed record AgentWriteResult(string Status, int Version, IReadOnlyList<Rejection> Rejected);
public sealed record HumanSaveResult(bool Saved, DraftView Current);
public sealed record DraftChange(string Path, JsonNode? Before, JsonNode? After);

public class WorkflowDraftService(StudioDb db, TimeProvider time)
{
    static readonly System.Text.Json.JsonSerializerOptions J = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // 存中文不轉成 XXXX
    /// <summary>系統算出的衍生欄位，不參與人工修改的 diff。</summary>
    const string DerivedKey = "naming_check";
    static JsonArray Items(JsonObject doc, string coll) => doc[coll]!.AsArray();
    static JsonObject? Find(JsonArray steps, string id) => steps.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.GetValue<string>() == id);
    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
    static string[] Edits(WorkflowDraft d) => JsonNode.Parse(d.HumanEditsJson)!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    async Task<WorkflowDraft> Load(string ticketId, string coll) =>
        await db.WorkflowDrafts.FirstOrDefaultAsync(x => x.TicketId == ticketId && x.Doc == coll)
        ?? db.WorkflowDrafts.Local.FirstOrDefault(x => x.TicketId == ticketId && x.Doc == coll) // 同一個 request 內先前新增、尚未存檔的
        ?? db.WorkflowDrafts.Add(new WorkflowDraft { TicketId = ticketId, Doc = coll, DraftJson = WorkflowDraft.Empty(coll), SeenJson = WorkflowDraft.Empty(coll) }).Entity;

    static DraftView View(WorkflowDraft d) => new(d.Version, Items(Parse(d.DraftJson), d.Doc), Edits(d));

    public async Task<DraftView> GetAsync(string ticketId, string coll = WorkflowDraft.StepsDoc) => View(await Load(ticketId, coll));

    /// <summary>比對兩份草稿，列出被新增／刪除／修改的路徑。</summary>
    public static List<DraftChange> Diff(JsonObject before, JsonObject after, string coll = WorkflowDraft.StepsDoc)
    {
        var res = new List<DraftChange>();
        var b = Items(before, coll); var a = Items(after, coll);
        foreach (var s in a.OfType<JsonObject>())
        {
            var id = s["id"]!.GetValue<string>();
            if (Find(b, id) is not { } old)
            {
                res.Add(new($"{coll}.{id}", null, s.DeepClone()));
                res.AddRange(s.Where(p => p.Key != "id").Select(p => new DraftChange($"{coll}.{id}.{p.Key}", null, p.Value?.DeepClone())));
                continue;
            }
            foreach (var k in s.Select(p => p.Key).Concat(old.Select(p => p.Key)).Where(k => k != "id" && k != DerivedKey).Distinct())
                if (!JsonNode.DeepEquals(old[k], s[k])) res.Add(new($"{coll}.{id}.{k}", old[k]?.DeepClone(), s[k]?.DeepClone()));
        }
        foreach (var s in b.OfType<JsonObject>())
            if (Find(a, s["id"]!.GetValue<string>()) is null) res.Add(new($"{coll}.{s["id"]}", s.DeepClone(), null));
        string[] Ids(JsonArray x, JsonArray other) => x.OfType<JsonObject>().Select(s => s["id"]!.GetValue<string>()).Where(i => Find(other, i) != null).ToArray();
        if (!Ids(b, a).SequenceEqual(Ids(a, b)))
            res.Add(new("order", new JsonArray(Ids(b, a).Select(i => (JsonNode)i).ToArray()), new JsonArray(Ids(a, b).Select(i => (JsonNode)i).ToArray())));
        return res;
    }

    /// <summary>CIM Engineer 存檔：版本號不符就不存，回傳最新版供畫面提示衝突。</summary>
    public async Task<HumanSaveResult> HumanSaveAsync(string ticketId, int version, JsonArray steps, string coll = WorkflowDraft.StepsDoc)
    {
        var d = await Load(ticketId, coll);
        if (d.Version != version) return new(false, View(d));
        var cur = Parse(d.DraftJson);
        var next = new JsonObject { [coll] = steps.DeepClone() };
        var changes = Diff(cur, next, coll);
        if (changes.Count == 0) return new(true, View(d));
        d.DraftJson = next.ToJsonString(J);
        d.HumanEditsJson = new JsonArray(Edits(d).Union(changes.Select(c => c.Path)).Select(p => (JsonNode)p).ToArray()).ToJsonString(J);
        d.Version++;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new(false, View(await Load(ticketId, coll))); } // 同時有人存檔
        return new(true, View(d));
    }

    /// <summary>agent 新增或修改一步（只改有給的欄位）。碰到人工修改過的欄位 → 整個請求拒絕並回傳目前的值，除非 override。</summary>
    public async Task<AgentWriteResult> AgentUpsertAsync(string ticketId, string empNo, string stepId, JsonObject fields, bool overrideHumanEdit, string coll = WorkflowDraft.StepsDoc)
    {
        var d = await Load(ticketId, coll);
        fields.Remove("id");
        var cur = Parse(d.DraftJson);
        var existing = Find(Items(cur, coll), stepId);
        var conflicts = new List<Rejection>();
        var marked = Edits(d);
        if (existing is null)
        {
            if (marked.Contains($"{coll}.{stepId}")) conflicts.Add(new($"{coll}.{stepId}", null));
        }
        else
            foreach (var f in fields)
                if (marked.Contains($"{coll}.{stepId}.{f.Key}") && !JsonNode.DeepEquals(existing[f.Key], f.Value)) conflicts.Add(new($"{coll}.{stepId}.{f.Key}", existing[f.Key]?.DeepClone()));
        if (conflicts.Count > 0 && !overrideHumanEdit) return new("Rejected", d.Version, conflicts);

        var changed = existing is null || fields.Any(f => !JsonNode.DeepEquals(existing[f.Key], f.Value));
        if (!changed) return new("Ok", d.Version, []);
        Apply(cur, coll, stepId, fields); d.DraftJson = cur.ToJsonString(J);
        var seen = Parse(d.SeenJson); Apply(seen, coll, stepId, fields); d.SeenJson = seen.ToJsonString(J); // agent 自己的寫入不算人工 diff
        d.Version++;
        foreach (var c in conflicts)
            db.TicketEvents.Add(new TicketEvent
            {
                TicketId = ticketId, Type = "OverrideHumanEdit", ActorEmpNo = empNo, At = time.GetUtcNow(), Reason = c.Path,
                FromValue = c.Current?.ToJsonString(J),
                ToValue = (c.Path == $"{coll}.{stepId}" ? (JsonNode?)fields : fields[c.Path[(c.Path.LastIndexOf('.') + 1)..]])?.ToJsonString(J),
            });
        await db.SaveChangesAsync();
        return new("Ok", d.Version, []);
    }

    static void Apply(JsonObject doc, string coll, string stepId, JsonObject fields)
    {
        var steps = Items(doc, coll);
        var s = Find(steps, stepId);
        if (s is null) { s = new JsonObject { ["id"] = stepId }; steps.Add(s); }
        foreach (var f in fields) s[f.Key] = f.Value?.DeepClone();
    }

    public async Task<AgentWriteResult> AgentDeleteAsync(string ticketId, string empNo, string stepId, bool overrideHumanEdit, string coll = WorkflowDraft.StepsDoc)
    {
        var d = await Load(ticketId, coll);
        var cur = Parse(d.DraftJson);
        if (Find(Items(cur, coll), stepId) is not { } step) return new("NotFound", d.Version, []);
        var hit = Edits(d).Where(p => p == $"{coll}.{stepId}" || p.StartsWith($"{coll}.{stepId}.")).ToList();
        if (hit.Count > 0 && !overrideHumanEdit) return new("Rejected", d.Version, [new($"{coll}.{stepId}", step.DeepClone())]);
        Items(cur, coll).Remove(step); d.DraftJson = cur.ToJsonString(J);
        var seen = Parse(d.SeenJson); if (Find(Items(seen, coll), stepId) is { } s2) Items(seen, coll).Remove(s2); d.SeenJson = seen.ToJsonString(J);
        d.Version++;
        if (hit.Count > 0)
            db.TicketEvents.Add(new TicketEvent { TicketId = ticketId, Type = "OverrideHumanEdit", ActorEmpNo = empNo, At = time.GetUtcNow(), Reason = $"{coll}.{stepId}", FromValue = step.ToJsonString(J) });
        await db.SaveChangesAsync();
        return new("Ok", d.Version, []);
    }

    /// <summary>
    /// 把系統算出的衍生欄位（如 naming_check）寫進每個 spec；不算人工修改、不遞增版本（畫面上的版本號仍有效），內容沒變就不寫。
    /// </summary>
    public async Task AnnotateAsync(string ticketId, string coll, string key, IReadOnlyDictionary<string, JsonNode> byId)
    {
        var d = await Load(ticketId, coll);
        var cur = Parse(d.DraftJson); var seen = Parse(d.SeenJson);
        var changed = false;
        foreach (var doc in new[] { cur, seen })
            foreach (var s in Items(doc, coll).OfType<JsonObject>())
                if (byId.TryGetValue(s["id"]!.GetValue<string>(), out var v) && !JsonNode.DeepEquals(s[key], v)) { s[key] = v.DeepClone(); changed = true; }
        if (!changed) return;
        d.DraftJson = cur.ToJsonString(J); d.SeenJson = seen.ToJsonString(J);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); } // 同時有人存檔；下次開啟會再算
    }

    /// <summary>run 開始時：取最新草稿（steps 與 tool_specs）與「自 agent 上次看過後的人工修改」，並標記為已看過。</summary>
    public async Task<(JsonObject Draft, JsonArray Diff)> BeginRunAsync(string ticketId)
    {
        var draft = new JsonObject(); var diff = new JsonArray();
        foreach (var coll in new[] { WorkflowDraft.StepsDoc, WorkflowDraft.ToolSpecsDoc })
        {
            var d = await Load(ticketId, coll);
            var cur = Parse(d.DraftJson);
            draft[coll] = Items(cur, coll).DeepClone();
            foreach (var c in Diff(Parse(d.SeenJson), cur, coll))
                diff.Add(new JsonObject { ["path"] = c.Path, ["before"] = c.Before, ["after"] = c.After });
            if (d.SeenJson != d.DraftJson) d.SeenJson = d.DraftJson;
        }
        await db.SaveChangesAsync();
        return (draft, diff);
    }

    /// <summary>Interview 送出時呼叫：清除人工修改標記（steps 與 tool_specs 都清）。</summary>
    public async Task ClearHumanEditsAsync(string ticketId)
    {
        foreach (var d in await db.WorkflowDrafts.Where(x => x.TicketId == ticketId).ToListAsync()) d.HumanEditsJson = "[]";
        await db.SaveChangesAsync();
    }
}
