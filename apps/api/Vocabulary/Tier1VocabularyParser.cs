using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>對應 McpGateway.Core 的 GlossaryEntry（spec 7.1）。</summary>
public sealed record GlossaryEntry(string StandardCamel, string StandardPascal, string Description, IReadOnlyList<string> Aliases);

/// <summary>抓取、解析或驗證詞彙失敗。</summary>
public class VocabularyException(string message) : Exception(message);

/// <summary>
/// 用 Roslyn 從 Tier1Vocabulary.cs 原文抽出每個 <c>new GlossaryEntry(camel, Pascal, description, aliases[])</c>（spec 7.1），
/// 再套用與 McpGateway.Core 的 Tier1VocabularyValidator 相同的三條規則。引數必須是字串常值；任何無法靜態讀出的寫法都視為解析失敗。
/// </summary>
public static class Tier1VocabularyParser
{
    public static IReadOnlyList<GlossaryEntry> Parse(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        if (tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } bad)
            throw new VocabularyException($"Tier1Vocabulary.cs 語法錯誤：{bad.GetMessage()}");

        var entries = tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(n => n.Type is IdentifierNameSyntax { Identifier.ValueText: "GlossaryEntry" })
            .Select(ToEntry).ToList();
        if (entries.Count == 0) throw new VocabularyException("Tier1Vocabulary.cs 找不到任何 GlossaryEntry。");

        var errors = Validate(entries);
        if (errors.Count > 0) throw new VocabularyException("詞彙驗證失敗：" + string.Join("；", errors));
        return entries;
    }

    static GlossaryEntry ToEntry(ObjectCreationExpressionSyntax n)
    {
        var args = n.ArgumentList?.Arguments ?? default;
        if (args.Count != 4) throw new VocabularyException($"GlossaryEntry 需要 4 個引數（{n.GetLocation().GetLineSpan().StartLinePosition}）。");
        string Lit(ExpressionSyntax e) => e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)
            ? l.Token.ValueText
            : throw new VocabularyException($"GlossaryEntry 的引數必須是字串常值（{e.GetLocation().GetLineSpan().StartLinePosition}）。");
        InitializerExpressionSyntax? init = args[3].Expression switch
        {
            ArrayCreationExpressionSyntax a => a.Initializer,
            ImplicitArrayCreationExpressionSyntax a => a.Initializer,
            _ => throw new VocabularyException($"GlossaryEntry 的別名必須是字串陣列（{args[3].GetLocation().GetLineSpan().StartLinePosition}）。"),
        };
        var aliases = init is null ? [] : init.Expressions.Select(Lit).ToList(); // new string[0] 沒有 initializer
        return new(Lit(args[0].Expression), Lit(args[1].Expression), Lit(args[2].Expression), aliases);
    }

    /// <summary>與 Tier1VocabularyValidator.Validate 相同：1) camel／Pascal 標準名各自不得重複；2) 別名不得撞上任何標準名；3) 同一別名不得屬於兩條詞條。</summary>
    static List<string> Validate(List<GlossaryEntry> entries)
    {
        var errors = new List<string>();
        void CheckDuplicates(Func<GlossaryEntry, string> sel, string label)
        {
            foreach (var g in entries.Select(sel).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add($"標準名稱（{label}）重複：'{g.Key}' 出現 {g.Count()} 次");
        }
        CheckDuplicates(e => e.StandardCamel, "camelCase");
        CheckDuplicates(e => e.StandardPascal, "PascalCase");

        var standard = new HashSet<string>(entries.SelectMany(e => new[] { e.StandardCamel, e.StandardPascal }), StringComparer.OrdinalIgnoreCase);
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            foreach (var alias in e.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) errors.Add($"{e.StandardCamel}：別名不可為空白");
                else if (standard.Contains(alias)) errors.Add($"別名 '{alias}'（屬 {e.StandardCamel}）撞上標準名稱");
                else if (owner.TryGetValue(alias, out var prev)) errors.Add($"別名 '{alias}' 同時屬於 {prev} 與 {e.StandardCamel}");
                else owner[alias] = e.StandardCamel;
            }
        return errors;
    }
}
