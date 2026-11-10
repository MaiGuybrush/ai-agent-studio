using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Services;

namespace AiAgentStudio.Api.Tickets;

public sealed record DirectoryUser(string EmpNo, string Name, string? AdId = null);

/// <summary>以工號查姓名（UAC basic-info，免認證；只用來帶出資料，不能用於授權）。</summary>
public interface IUserDirectory
{
    /// <summary>查無此人回傳 null；呼叫失敗拋例外。</summary>
    Task<DirectoryUser?> FindAsync(string empNo, CancellationToken ct);
}

public sealed class UacUserDirectory(IHttpClientFactory http, IConsulAddressProvider addresses) : IUserDirectory
{
    public async Task<DirectoryUser?> FindAsync(string empNo, CancellationToken ct)
    {
        var bases = await addresses.GetAddressesAsync("UacApi", ct);
        if (bases.Count == 0) throw new HttpRequestException("No UAC API address available");
        var client = http.CreateClient("UacClient");
        Exception? last = null;
        foreach (var b in bases)
        {
            try
            {
                using var res = await client.GetAsync($"{b.TrimEnd('/')}/HrEmployee/{Uri.EscapeDataString(empNo)}/basic-info", ct);
                if (res.StatusCode == HttpStatusCode.NotFound) return null;
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                string? name = null, adId = null;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Value.GetString())) continue;
                    if (p.Name.Equals("empName", StringComparison.OrdinalIgnoreCase)) name = p.Value.GetString();
                    else if (p.Name.Equals("adId", StringComparison.OrdinalIgnoreCase)) adId = p.Value.GetString(); // spec 3.1：email = adId@網域
                }
                return name is null ? null : new DirectoryUser(empNo, name, adId); // 回應沒有姓名視同查無此人
            }
            catch (Exception e) when ((e is HttpRequestException or JsonException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw last!;
    }
}
