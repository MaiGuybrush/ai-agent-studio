using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Services;

namespace AiAgentStudio.Api.Identity;

public interface IHrClient
{
    /// <summary>以使用者自己的 JWT 查部門代碼（UAC 只允許查自己）。查無部門回傳 null；呼叫失敗拋例外。</summary>
    Task<string?> GetDeptCodeAsync(string empNo, string jwt, CancellationToken ct);
}

/// <summary>
/// 呼叫 UAC 的 GET /HrEmployee/{empNo}?includeDept=true。
/// 假設（spec 第 14 節待確認）：HR API 與 UAC 同一個 base address；回應為 JSON，部門代碼在頂層欄位 "deptCode"。
/// </summary>
public sealed class HrClient(IHttpClientFactory http, IConsulAddressProvider addresses) : IHrClient
{
    public async Task<string?> GetDeptCodeAsync(string empNo, string jwt, CancellationToken ct)
    {
        var bases = await addresses.GetAddressesAsync("UacApi", ct);
        if (bases.Count == 0) throw new HttpRequestException("No UAC API address available");
        var client = http.CreateClient("UacClient");
        Exception? last = null;
        foreach (var b in bases)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{b.TrimEnd('/')}/HrEmployee/{Uri.EscapeDataString(empNo)}?includeDept=true");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                using var res = await client.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals("deptCode", StringComparison.OrdinalIgnoreCase))
                        return p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())
                            ? p.Value.GetString() : null;
                return null;
            }
            catch (Exception e) when ((e is HttpRequestException or JsonException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                last = e; // 試下一個節點
            }
        }
        throw last!;
    }
}

/// <summary>固定位址的 UAC 位址供應（Authentication:Uac:BaseUrls），供本機搭配 mock UAC，不需要 Consul。</summary>
public sealed class StaticAddressProvider(string[] urls) : IConsulAddressProvider
{
    public Task<IReadOnlyList<string>> GetAddressesAsync(string serviceName, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(urls);

    public IReadOnlyList<string> GetAddresses(string serviceName) => urls;
}
