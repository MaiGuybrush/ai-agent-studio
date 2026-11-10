using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace AiAgentStudio.Api.Tests;

/// <summary>需求單測試共用設定：真 JwtBearer 管線 + 假 JWKS／HR／basic-info + EF InMemory（不需 DB、網路）。</summary>
public class TicketFactory : WebApplicationFactory<Program>
{
    public static readonly RsaSecurityKey Key = new(System.Security.Cryptography.RSA.Create(2048));
    public readonly Dictionary<string, string?> Depts = new()
    { ["CIM1"] = "CIM100", ["CIM2"] = "CIM200", ["BIZ1"] = "MFG1", ["SSO1"] = "MFG2" };
    public readonly Dictionary<string, string> Names = new()
    { ["CIM1"] = "工程師一", ["CIM2"] = "工程師二", ["BIZ1"] = "業務一", ["SSO1"] = "路人一" };
    readonly string dbName = Guid.NewGuid().ToString();

    class FakeJwks : IJwksProvider
    {
        public IEnumerable<SecurityKey> GetSigningKeys(string? kid = null) => [Key];
        public Task<IEnumerable<SecurityKey>> GetSigningKeysAsync(string? kid = null, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<SecurityKey>>([Key]);
    }
    class FakeHr(TicketFactory f) : IHrClient
    {
        public Task<string?> GetDeptCodeAsync(string empNo, string jwt, CancellationToken ct) => Task.FromResult(f.Depts.GetValueOrDefault(empNo));
    }
    class FakeDirectory(TicketFactory f) : IUserDirectory
    {
        public Task<DirectoryUser?> FindAsync(string empNo, CancellationToken ct) =>
            Task.FromResult(f.Names.TryGetValue(empNo, out var n) ? new DirectoryUser(empNo, n, "ad" + empNo) : null);
    }

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Cim:DeptCodePrefixes:0"] = "CIM" }));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<IJwksProvider>(); s.AddSingleton<IJwksProvider, FakeJwks>();
            s.RemoveAll<IHrClient>(); s.AddSingleton<IHrClient>(new FakeHr(this));
            s.RemoveAll<IUserDirectory>(); s.AddSingleton<IUserDirectory>(new FakeDirectory(this));
            s.RemoveAll<DbContextOptions<StudioDb>>(); foreach (var d in s.Where(d => d.ServiceType.IsGenericType && d.ServiceType.GenericTypeArguments.Contains(typeof(StudioDb))).ToList()) s.Remove(d);
            s.AddDbContext<StudioDb>(o => o.UseInMemoryDatabase(dbName));
        });
    }

    public static string Token(string empNo) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        claims: [new Claim("empNo", empNo)], expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)));

    public HttpClient As(string empNo)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(empNo));
        return c;
    }

    public async Task<HttpResponseMessage> Create(string who, string id = "T-1", string desc = "需求", string bu = "BIZ1") =>
        await As(who).PostAsJsonAsync("/api/tickets", new { id, description = desc, businessUserEmpNo = bu });

    public static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public List<TicketEvent> Events()
    {
        using var s = Services.CreateScope();
        return s.ServiceProvider.GetRequiredService<StudioDb>().TicketEvents.AsNoTracking().OrderBy(e => e.Id).ToList();
    }
}
