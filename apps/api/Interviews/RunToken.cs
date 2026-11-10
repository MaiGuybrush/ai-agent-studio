using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace AiAgentStudio.Api.Interviews;

public sealed record RunTokenClaims(string TicketId, string EmpNo, DateTimeOffset ExpiresAt);

/// <summary>
/// 每個 run 簽發的短期 token（spec 2.3）：綁定需求單 id 與 CIM Engineer 工號，HMAC 簽章，到期失效。
/// 內部 tool API 只接受這個 token，且只能操作 token 內的那張單。
/// </summary>
public sealed class RunTokenService(TimeProvider time, string secret)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    readonly byte[] key = Encoding.UTF8.GetBytes(secret);

    string Sign(string payload) => WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));

    public string Issue(string ticketId, string empNo)
    {
        var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{ticketId}\n{empNo}\n{(time.GetUtcNow() + Ttl).ToUnixTimeSeconds()}"));
        return $"{payload}.{Sign(payload)}";
    }

    /// <summary>簽章錯誤、格式錯誤或已過期都回 null。</summary>
    public RunTokenClaims? Validate(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not [var payload, var sig]) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(Sign(payload)))) return null;
        try
        {
            var f = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(payload)).Split('\n');
            var exp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[2]));
            return exp <= time.GetUtcNow() ? null : new RunTokenClaims(f[0], f[1], exp);
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException) { return null; }
    }
}
