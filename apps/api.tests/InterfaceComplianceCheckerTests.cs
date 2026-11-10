using System.Text.Json.Nodes;
using AiAgentStudio.Api.Submissions;
using AiAgentStudio.Api.Vocabulary;

namespace AiAgentStudio.Api.Tests;

/// <summary>#29：Interface Compliance Check 比對規則（spec 6.4）。純函式，直接以公開介面測。</summary>
public class InterfaceComplianceCheckerTests
{
    static JsonNode J(string s) => JsonNode.Parse(s)!;
    static readonly JsonNode NoOut = J("""{"type":"object","properties":{}}""");
    static readonly GlossaryEntry[] Vocab = [new("eqptId", "EqptId", "d", ["equipmentId"])];

    static ComplianceResult Run(string specIn, string actIn, string kind = "New", string? specOut = null, string? actOut = null, GlossaryEntry[]? vocab = null) =>
        InterfaceComplianceChecker.Check(kind, J(specIn), specOut is null ? NoOut : J(specOut), J(actIn), actOut is null ? NoOut : J(actOut), vocab);

    [Fact]
    public void Identical_interface_passes()
    {
        var s = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""";
        var r = Run(s, s);
        Assert.True(r.Passed); Assert.Empty(r.Failures); Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Missing_input_field_fails()
    {
        var r = Run("""{"type":"object","properties":{"id":{"type":"string"}}}""", """{"type":"object","properties":{}}""");
        Assert.False(r.Passed); Assert.Contains(r.Failures, f => f.Contains("id"));
    }

    [Fact]
    public void Input_type_mismatch_fails()
    {
        var r = Run("""{"type":"object","properties":{"id":{"type":"string"}}}""", """{"type":"object","properties":{"id":{"type":"boolean"}}}""");
        Assert.False(r.Passed);
    }

    [Theory]
    [InlineData("""["id"]""", "[]")]   // Tool Spec 必填、實際選填
    [InlineData("[]", """["id"]""")]   // Tool Spec 選填、實際必填
    public void Input_required_mismatch_fails(string specReq, string actReq)
    {
        var r = Run($$$"""{"type":"object","properties":{"id":{"type":"string"}},"required":{{{specReq}}}}""", $$$"""{"type":"object","properties":{"id":{"type":"string"}},"required":{{{actReq}}}}""");
        Assert.False(r.Passed);
    }

    [Fact]
    public void Nested_object_and_array_items_are_compared_recursively()
    {
        var spec = """{"type":"object","properties":{"lots":{"type":"array","items":{"type":"object","properties":{"qty":{"type":"integer"}},"required":["qty"]}}}}""";
        Assert.True(Run(spec, spec).Passed);
        var bad = """{"type":"object","properties":{"lots":{"type":"array","items":{"type":"object","properties":{"qty":{"type":"string"}},"required":["qty"]}}}}""";
        var r = Run(spec, bad);
        Assert.False(r.Passed); Assert.Contains(r.Failures, f => f.Contains("lots[].qty"));
    }

    [Fact]
    public void Extra_required_input_blocks_for_new_but_not_for_extend()
    {
        var spec = """{"type":"object","properties":{"id":{"type":"string"}}}""";
        var act = """{"type":"object","properties":{"id":{"type":"string"},"token":{"type":"string"}},"required":["token"]}""";
        var r = Run(spec, act);
        Assert.False(r.Passed); Assert.Contains(r.Failures, f => f.Contains("token"));
        Assert.True(Run(spec, act, kind: "Extend").Passed); // Extend 只比對有定義的欄位
    }

    [Fact]
    public void Extra_optional_input_only_warns_when_it_hits_an_alias()
    {
        var spec = """{"type":"object","properties":{}}""";
        var hit = Run(spec, """{"type":"object","properties":{"equipmentId":{"type":"string"}}}""", vocab: Vocab);
        Assert.True(hit.Passed); Assert.Single(hit.Warnings); Assert.Contains("eqptId", hit.Warnings[0]);
        var miss = Run(spec, """{"type":"object","properties":{"whatever":{"type":"string"}}}""", vocab: Vocab);
        Assert.True(miss.Passed); Assert.Empty(miss.Warnings);
    }

    [Fact]
    public void Extra_output_fields_only_warn_on_alias_and_extend_skips_them()
    {
        var act = """{"type":"object","properties":{"equipmentId":{"type":"string"}}}""";
        var r = Run("""{"type":"object","properties":{}}""", """{"type":"object","properties":{}}""", actOut: act, vocab: Vocab);
        Assert.True(r.Passed); Assert.Single(r.Warnings);
        Assert.Empty(Run("""{"type":"object","properties":{}}""", """{"type":"object","properties":{}}""", kind: "Extend", actOut: act, vocab: Vocab).Warnings);
    }

    [Fact]
    public void Output_checks_name_and_type_but_not_required()
    {
        var spec = """{"type":"object","properties":{"status":{"type":"string"}},"required":["status"]}""";
        var none = """{"type":"object","properties":{}}""";
        Assert.True(Run(none, none, specOut: spec, actOut: """{"type":"object","properties":{"status":{"type":"string"}}}""").Passed);
        Assert.False(Run(none, none, specOut: spec, actOut: none).Passed);
        Assert.False(Run(none, none, specOut: spec, actOut: """{"type":"object","properties":{"status":{"type":"integer"}}}""").Passed);
    }

    [Fact]
    public void Missing_output_schema_fails_when_the_spec_defines_outputs()
    {
        var r = InterfaceComplianceChecker.Check("New", J("""{"type":"object","properties":{}}"""),
            J("""{"type":"object","properties":{"status":{"type":"string"}}}"""), J("""{"type":"object","properties":{}}"""), null);
        Assert.False(r.Passed);
    }

    [Theory]
    [InlineData("""["a","b"]""", """["a","b","c"]""", true)]
    [InlineData("""["a","b"]""", """["a"]""", false)]
    public void Enum_actual_must_contain_all_spec_values(string spec, string actual, bool ok) =>
        Assert.Equal(ok, Run("""{"type":"object","properties":{"m":{"type":"string","enum":""" + spec + "}}}", """{"type":"object","properties":{"m":{"type":"string","enum":""" + actual + "}}}").Passed);
    [InlineData("integer", "number", false)]


    static string Q(string t) => "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"" + t + "\"}}}";

    [Theory]
    [InlineData("number", "integer", true)]

    public void Number_spec_accepts_integer_but_not_the_reverse(string spec, string actual, bool ok) =>
        Assert.Equal(ok, Run(Q(spec), Q(actual)).Passed);

    [Fact]
    public void Nullable_actual_type_is_compared_by_its_non_null_type() =>
        Assert.True(Run("""{"type":"object","properties":{"q":{"type":"string"}}}""", """{"type":"object","properties":{"q":{"type":["string","null"]}}}""").Passed);
}
