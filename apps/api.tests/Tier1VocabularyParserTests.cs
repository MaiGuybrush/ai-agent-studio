using AiAgentStudio.Api.Vocabulary;

namespace AiAgentStudio.Api.Tests;

/// <summary>#22：用 Roslyn 解析 Tier1Vocabulary.cs 原文，並套用與 Tier1VocabularyValidator 相同的規則。fixture 是 McpGateway.Core 的唯讀複本。</summary>
public class Tier1VocabularyParserTests
{
    static string Real => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tier1Vocabulary.cs.txt"));

    static string Src(params string[] entries) =>
        "internal static class Tier1Vocabulary { public static readonly GlossaryEntry[] Entries = new[] {\n" + string.Join("\n", entries) + "\n}; }";

    [Fact]
    public void Parses_every_entry_of_the_real_file()
    {
        var entries = Tier1VocabularyParser.Parse(Real);
        Assert.Equal(Real.Split("new GlossaryEntry(").Length - 1, entries.Count); // 原文每個 "new GlossaryEntry(" 就是一個詞條
        var eqpt = entries.Single(e => e.StandardCamel == "eqptId");
        Assert.Equal("EqptId", eqpt.StandardPascal);
        Assert.Contains("EQP_ID", eqpt.Aliases);
        Assert.StartsWith("主設備/機台識別碼", eqpt.Description);
    }

    [Fact]
    public void Entry_with_empty_alias_array_is_allowed()
    {
        var e = Tier1VocabularyParser.Parse(Src("new GlossaryEntry(\"a\", \"A\", \"d\", new string[] { }),")).Single();
        Assert.Empty(e.Aliases);
    }

    [Fact]
    public void Duplicate_standard_names_and_alias_clashes_are_rejected_like_the_core_validator()
    {
        var dup = Src("new GlossaryEntry(\"a\", \"A\", \"d\", new[] { \"x\" }),", "new GlossaryEntry(\"A\", \"A2\", \"d\", new[] { \"y\" }),");
        Assert.Contains("camelCase", Assert.Throws<VocabularyException>(() => Tier1VocabularyParser.Parse(dup)).Message);
        var clash = Src("new GlossaryEntry(\"a\", \"A\", \"d\", new[] { \"b\" }),", "new GlossaryEntry(\"b\", \"B\", \"d\", new string[0]),");
        Assert.Contains("撞上標準名稱", Assert.Throws<VocabularyException>(() => Tier1VocabularyParser.Parse(clash)).Message);
        var two = Src("new GlossaryEntry(\"a\", \"A\", \"d\", new[] { \"z\" }),", "new GlossaryEntry(\"b\", \"B\", \"d\", new[] { \"Z\" }),");
        Assert.Contains("同時屬於", Assert.Throws<VocabularyException>(() => Tier1VocabularyParser.Parse(two)).Message);
        var blank = Src("new GlossaryEntry(\"a\", \"A\", \"d\", new[] { \" \" }),");
        Assert.Contains("別名不可為空白", Assert.Throws<VocabularyException>(() => Tier1VocabularyParser.Parse(blank)).Message);
    }

    [Theory]
    [InlineData("internal class X {")]   // 語法錯誤
    [InlineData("class X { }")]          // 沒有任何詞條
    public void Broken_or_empty_source_is_rejected(string text) => Assert.Throws<VocabularyException>(() => Tier1VocabularyParser.Parse(text));

    [Fact]
    public void Non_literal_arguments_are_rejected() =>
        Assert.Throws<VocabularyException>(() => Tier1VocabularyParser.Parse(Src("new GlossaryEntry(Name, \"A\", \"d\", new string[0]),")));
}
