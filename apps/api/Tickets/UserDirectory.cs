using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Services;

namespace AiAgentStudio.Api.Tickets;

public sealed record DirectoryUser(string EmpNo, string Name);

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
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals("empName", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        return new DirectoryUser(empNo, p.Value.GetString()!);
                return null; // 回應沒有姓名視同查無此人
            }
            catch (Exception e) when ((e is HttpRequestException or JsonException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw last!;
    }
}
