using System.Text.Json.Nodes;
using AiAgentStudio.Api.Vocabulary;

namespace AiAgentStudio.Api.Tests;

/// <summary>#22：比對規則（spec 7.2）與巢狀欄位走訪（spec 6.1）。</summary>
public class NamingCheckerTests
{
    static readonly IReadOnlyList<GlossaryEntry> Vocab =
    [
        new("eqptId", "EqptId", "主設備", ["equipmentId", "machineId", "EQP_ID"]),
        new("status", "Status", "狀態", ["state"]),
    ];

    [Theory]
    [InlineData("eqptId", "pass", null)]                 // 1. 完全一致
    [InlineData("EqptID", "rename", "eqptId")]           // 2. 只差大小寫 → 標準寫法
    [InlineData("EQPTID", "rename", "eqptId")]
    [InlineData("equipmentId", "rename", "eqptId")]      // 3. 別名
    [InlineData("MACHINEID", "rename", "eqptId")]        //    別名不分大小寫
    [InlineData("eqp_id", "rename", "eqptId")]           //    EQP_ID 別名的大小寫變體
    [InlineData("wafer_thickness", "new", null)]         // 4. 都沒有 → 新名詞
    public void Rules_apply_in_order(string name, string status, string? suggestion)
    {
        var r = NamingChecker.Check(Vocab, name);
        Assert.Equal(status, r.Status);
        Assert.Equal(suggestion, r.Suggestion);
    }

    [Fact]
    public void Pascal_context_uses_pascal_standard_name()
    {
        Assert.Equal("pass", NamingChecker.Check(Vocab, "EqptId", pascal: true).Status);
        var r = NamingChecker.Check(Vocab, "eqptId", pascal: true); // camel 在 Pascal 情境只差大小寫
        Assert.Equal(("rename", "EqptId"), (r.Status, r.Suggestion));
        Assert.Equal("EqptId", NamingChecker.Check(Vocab, "machineId", pascal: true).Suggestion);
    }

    [Fact]
    public void Schema_walk_checks_nested_properties_and_array_items()
    {
        var schema = JsonNode.Parse("""
        { "type": "object", "properties": {
            "equipmentId": { "type": "string" },
            "lots": { "type": "array", "items": { "type": "object", "properties": { "state": { "type": "string" } } } },
            "detail": { "type": "object", "properties": { "eqptId": { "type": "string" }, "foo": { "type": "number" } } } } }
        """)!;
        var r = NamingChecker.CheckSchema(Vocab, schema, "inputs");
        Assert.Equal(
            ["inputs.detail=new", "inputs.detail.eqptId=pass", "inputs.detail.foo=new", "inputs.equipmentId=rename:eqptId", "inputs.lots=new", "inputs.lots[].state=rename:status"], // 容器欄位本身也要檢查
            r.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value.Status}" + (x.Value.Suggestion is { } s ? ":" + s : "")).ToArray());
    }
}
