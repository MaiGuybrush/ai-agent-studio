using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiAgentStudio.Api.Identity;

/// <param name="Error">查不到部門時的明確錯誤；此時 IsCimEngineer 一定是 false（fail closed）。</param>
public sealed record UserIdentity(string EmpNo, bool IsCimEngineer, string? DeptCode, string? Error);

/// <summary>
/// 判定是否為 CIM Engineer（spec 3.2）。部門代碼只來自 C# 帶使用者 JWT 查 HR，永不信任瀏覽器傳來的值。
/// 部門代碼記憶體快取 10 分鐘；過期後重查，重查失敗也不沿用舊值；失敗結果不快取。
/// </summary>
public sealed class IdentityService(IHrClient hr, IOptionsMonitor<CimOptions> options, TimeProvider time, ILogger<IdentityService> log)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    // ponytail: 以 empNo 為 key 且只在同一人再次查詢時淘汰過期項目；使用者數量有限，成長後再加清理。
    readonly ConcurrentDictionary<string, (string Dept, DateTimeOffset Expires)> cache = new();

    public async Task<UserIdentity> GetAsync(string empNo, string jwt, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        string? dept;
        if (cache.TryGetValue(empNo, out var hit) && now < hit.Expires) dept = hit.Dept;
        else
        {
            cache.TryRemove(empNo, out _); // 過期就丟掉，絕不沿用
            try { dept = await hr.GetDeptCodeAsync(empNo, jwt, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(e, "HR lookup failed for {EmpNo}", empNo);
                return new(empNo, false, null, "無法取得部門資訊（HR 服務查詢失敗），暫時無法確認 CIM Engineer 身分；唯讀功能不受影響。");
            }
            if (dept is null) return new(empNo, false, null, "查不到您的部門資訊，無法確認 CIM Engineer 身分；唯讀功能不受影響。");
            cache[empNo] = (dept, now + CacheTtl);
        }
        return new(empNo, options.CurrentValue.IsCimDept(dept), dept, null);
    }
}
