using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>詞彙來源（公司內部 Gitea 上 McpGateway.Core 的 Tier1Vocabulary.cs，main 最新版）。介面存在是為了測試能換成假的。</summary>
public interface IVocabularySource
{
    /// <summary>main 分支目前的 commit sha。</summary>
    Task<string> GetHeadShaAsync(CancellationToken ct);
    /// <summary>指定 commit 的 Tier1Vocabulary.cs 原文。</summary>
    Task<string> GetFileAsync(string sha, CancellationToken ct);
}

/// <summary>詞彙同步失敗時通知 CIM Engineer。</summary>
public interface IVocabularyAlert
{
    Task NotifyAsync(string message, CancellationToken ct);
}

/// <summary>Warning 不為 null 表示這是沿用的舊版本（最新版抓取／解析／驗證失敗），畫面要標示 sha 與時間。</summary>
public sealed record VocabularySnapshot(string Sha, DateTimeOffset FetchedAt, IReadOnlyList<GlossaryEntry> Entries, string? Warning);

/// <summary>快取以 commit sha 為 key、TTL 5 分鐘，過期才比對 sha（spec 7.1）。</summary>
public sealed class VocabularyService(IVocabularySource source, TimeProvider time, IVocabularyAlert alert)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    readonly SemaphoreSlim gate = new(1, 1);
    VocabularySnapshot? cache;
    DateTimeOffset checkedAt;

    /// <summary>完全沒有快取又失敗時丟 <see cref="VocabularyException"/>（擋下 Naming Check）。</summary>
    public async Task<VocabularySnapshot> GetAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (cache is not null && now - checkedAt < Ttl) return cache;
            checkedAt = now; // 失敗也重新起算，避免每次呼叫都打 Gitea、重複通知
            try
            {
                var sha = await source.GetHeadShaAsync(ct);
                if (cache is null || cache.Sha != sha)
                    cache = new(sha, now, Tier1VocabularyParser.Parse(await source.GetFileAsync(sha, ct)), null);
                else if (cache.Warning is not null) cache = cache with { Warning = null };
                return cache;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var why = e is VocabularyException ? e.Message : $"無法從 Gitea 取得 Tier1Vocabulary.cs：{e.Message}";
                if (cache is null) { checkedAt = default; throw e as VocabularyException ?? new VocabularyException(why); }
                cache = cache with { Warning = $"詞彙更新失敗，沿用 {cache.Sha}（{cache.FetchedAt:u}）：{why}" };
                await alert.NotifyAsync(cache.Warning, ct);
                return cache;
            }
        }
        finally { gate.Release(); }
    }
}
