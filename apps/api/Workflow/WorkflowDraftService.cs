using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AiAgentStudio.Api.Tickets;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Workflow;

/// <summary>
/// 每張單一份 Workflow Definition 草稿（spec 6.3、13）。DraftJson = {"steps":[{id, description, tool_spec, inputs, outputs, needs_approval, condition}]}。
/// 欄位路徑：steps.{id}（整步）、steps.{id}.{field}、order（步驟順序）。
/// </summary>
public class WorkflowDraft
{
    public string TicketId { get; set; } = "";
    public int Version { get; set; }
    public string DraftJson { get; set; } = EmptyJson;
    /// <summary>agent 上次看過的草稿（含 agent 自己的寫入）；與目前草稿比對即「人工修改的 diff」。</summary>
    public string SeenJson { get; set; } = EmptyJson;
    /// <summary>人工修改過的欄位路徑（JSON 陣列），保留到 Interview 送出為止。</summary>
    public string HumanEditsJson { get; set; } = "[]";
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
    static JsonArray Steps(JsonObject doc) => doc["steps"]!.AsArray();
    static JsonObject? Find(JsonArray steps, string id) => steps.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.GetValue<string>() == id);
    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
    static string[] Edits(WorkflowDraft d) => JsonNode.Parse(d.HumanEditsJson)!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    async Task<WorkflowDraft> Load(string ticketId) =>
        await db.WorkflowDrafts.FirstOrDefaultAsync(x => x.TicketId == ticketId)
        ?? db.WorkflowDrafts.Add(new WorkflowDraft { TicketId = ticketId }).Entity;

    static DraftView View(WorkflowDraft d) => new(d.Version, Steps(Parse(d.DraftJson)), Edits(d));

    public async Task<DraftView> GetAsync(string ticketId) => View(await Load(ticketId));

    /// <summary>比對兩份草稿，列出被新增／刪除／修改的路徑。</summary>
    public static List<DraftChange> Diff(JsonObject before, JsonObject after)
    {
        var res = new List<DraftChange>();
        var b = Steps(before); var a = Steps(after);
        foreach (var s in a.OfType<JsonObject>())
        {
            var id = s["id"]!.GetValue<string>();
            if (Find(b, id) is not { } old)
            {
                res.Add(new($"steps.{id}", null, s.DeepClone()));
                res.AddRange(s.Where(p => p.Key != "id").Select(p => new DraftChange($"steps.{id}.{p.Key}", null, p.Value?.DeepClone())));
                continue;
            }
            foreach (var k in s.Select(p => p.Key).Concat(old.Select(p => p.Key)).Where(k => k != "id").Distinct())
                if (!JsonNode.DeepEquals(old[k], s[k])) res.Add(new($"steps.{id}.{k}", old[k]?.DeepClone(), s[k]?.DeepClone()));
        }
        foreach (var s in b.OfType<JsonObject>())
            if (Find(a, s["id"]!.GetValue<string>()) is null) res.Add(new($"steps.{s["id"]}", s.DeepClone(), null));
        string[] Ids(JsonArray x, JsonArray other) => x.OfType<JsonObject>().Select(s => s["id"]!.GetValue<string>()).Where(i => Find(other, i) != null).ToArray();
        if (!Ids(b, a).SequenceEqual(Ids(a, b)))
            res.Add(new("order", new JsonArray(Ids(b, a).Select(i => (JsonNode)i).ToArray()), new JsonArray(Ids(a, b).Select(i => (JsonNode)i).ToArray())));
        return res;
    }

    /// <summary>CIM Engineer 存檔：版本號不符就不存，回傳最新版供畫面提示衝突。</summary>
    public async Task<HumanSaveResult> HumanSaveAsync(string ticketId, int version, JsonArray steps)
    {
        var d = await Load(ticketId);
        if (d.Version != version) return new(false, View(d));
        var cur = Parse(d.DraftJson);
        var next = new JsonObject { ["steps"] = steps.DeepClone() };
        var changes = Diff(cur, next);
        if (changes.Count == 0) return new(true, View(d));
        d.DraftJson = next.ToJsonString(J);
        d.HumanEditsJson = new JsonArray(Edits(d).Union(changes.Select(c => c.Path)).Select(p => (JsonNode)p).ToArray()).ToJsonString(J);
        d.Version++;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new(false, View(await Load(ticketId))); } // 同時有人存檔
        return new(true, View(d));
    }

    /// <summary>agent 新增或修改一步（只改有給的欄位）。碰到人工修改過的欄位 → 整個請求拒絕並回傳目前的值，除非 override。</summary>
    public async Task<AgentWriteResult> AgentUpsertAsync(string ticketId, string empNo, string stepId, JsonObject fields, bool overrideHumanEdit)
    {
        var d = await Load(ticketId);
        fields.Remove("id");
        var cur = Parse(d.DraftJson);
        var existing = Find(Steps(cur), stepId);
        var conflicts = new List<Rejection>();
        var marked = Edits(d);
        if (existing is null)
        {
            if (marked.Contains($"steps.{stepId}")) conflicts.Add(new($"steps.{stepId}", null));
        }
        else
            foreach (var f in fields)
                if (marked.Contains($"steps.{stepId}.{f.Key}") && !JsonNode.DeepEquals(existing[f.Key], f.Value)) conflicts.Add(new($"steps.{stepId}.{f.Key}", existing[f.Key]?.DeepClone()));
        if (conflicts.Count > 0 && !overrideHumanEdit) return new("Rejected", d.Version, conflicts);

        var changed = existing is null || fields.Any(f => !JsonNode.DeepEquals(existing[f.Key], f.Value));
        if (!changed) return new("Ok", d.Version, []);
        Apply(cur, stepId, fields); d.DraftJson = cur.ToJsonString(J);
        var seen = Parse(d.SeenJson); Apply(seen, stepId, fields); d.SeenJson = seen.ToJsonString(J); // agent 自己的寫入不算人工 diff
        d.Version++;
        foreach (var c in conflicts)
            db.TicketEvents.Add(new TicketEvent
            {
                TicketId = ticketId, Type = "OverrideHumanEdit", ActorEmpNo = empNo, At = time.GetUtcNow(), Reason = c.Path,
                FromValue = c.Current?.ToJsonString(J),
                ToValue = (c.Path == $"steps.{stepId}" ? (JsonNode?)fields : fields[c.Path[(c.Path.LastIndexOf('.') + 1)..]])?.ToJsonString(J),
            });
        await db.SaveChangesAsync();
        return new("Ok", d.Version, []);
    }

    static void Apply(JsonObject doc, string stepId, JsonObject fields)
    {
        var steps = Steps(doc);
        var s = Find(steps, stepId);
        if (s is null) { s = new JsonObject { ["id"] = stepId }; steps.Add(s); }
        foreach (var f in fields) s[f.Key] = f.Value?.DeepClone();
    }

    public async Task<AgentWriteResult> AgentDeleteAsync(string ticketId, string empNo, string stepId, bool overrideHumanEdit)
    {
        var d = await Load(ticketId);
        var cur = Parse(d.DraftJson);
        if (Find(Steps(cur), stepId) is not { } step) return new("NotFound", d.Version, []);
        var hit = Edits(d).Where(p => p == $"steps.{stepId}" || p.StartsWith($"steps.{stepId}.")).ToList();
        if (hit.Count > 0 && !overrideHumanEdit) return new("Rejected", d.Version, [new($"steps.{stepId}", step.DeepClone())]);
        Steps(cur).Remove(step); d.DraftJson = cur.ToJsonString(J);
        var seen = Parse(d.SeenJson); if (Find(Steps(seen), stepId) is { } s2) Steps(seen).Remove(s2); d.SeenJson = seen.ToJsonString(J);
        d.Version++;
        if (hit.Count > 0)
            db.TicketEvents.Add(new TicketEvent { TicketId = ticketId, Type = "OverrideHumanEdit", ActorEmpNo = empNo, At = time.GetUtcNow(), Reason = $"steps.{stepId}", FromValue = step.ToJsonString(J) });
        await db.SaveChangesAsync();
        return new("Ok", d.Version, []);
    }

    /// <summary>run 開始時：取最新草稿與「自 agent 上次看過後的人工修改」，並標記為已看過。</summary>
    public async Task<(JsonObject Draft, JsonArray Diff)> BeginRunAsync(string ticketId)
    {
        var d = await Load(ticketId);
        var cur = Parse(d.DraftJson);
        var diff = new JsonArray(Diff(Parse(d.SeenJson), cur).Select(c => (JsonNode)new JsonObject
        { ["path"] = c.Path, ["before"] = c.Before, ["after"] = c.After }).ToArray());
        if (d.SeenJson != d.DraftJson) d.SeenJson = d.DraftJson;
        await db.SaveChangesAsync();
        return (cur, diff);
    }

    /// <summary>Interview 送出時呼叫：清除人工修改標記。</summary>
    public async Task ClearHumanEditsAsync(string ticketId)
    {
        var d = await Load(ticketId);
        d.HumanEditsJson = "[]";
        await db.SaveChangesAsync();
    }
}
