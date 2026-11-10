using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace AiAgentStudio.Api.Tests;

/// <summary>端到端：真的 JwtBearer 管線 + 假 JWKS + 假 HR（不連 UAC、不需要 DB）。</summary>
public class MeEndpointTests : IClassFixture<MeEndpointTests.Factory>
{
    static readonly RsaSecurityKey Key = new(System.Security.Cryptography.RSA.Create(2048));

    class FakeJwks : IJwksProvider
    {
        public IEnumerable<SecurityKey> GetSigningKeys(string? kid = null) => [Key];
        public Task<IEnumerable<SecurityKey>> GetSigningKeysAsync(string? kid = null, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<SecurityKey>>([Key]);
    }

    public class FakeHr : IHrClient
    {
        public Dictionary<string, string?> Depts = new() { ["E1"] = "CIM100", ["E2"] = "MFG1", ["E3"] = null };
        public List<(string EmpNo, string Jwt)> Calls = [];
        public Task<string?> GetDeptCodeAsync(string empNo, string jwt, CancellationToken ct)
        {
            Calls.Add((empNo, jwt));
            return Task.FromResult(Depts.GetValueOrDefault(empNo));
        }
    }

    public class Factory : WebApplicationFactory<Program>
    {
        public FakeHr Hr = new();
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Cim:DeptCodePrefixes:0"] = "CIM" }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IJwksProvider>(); s.AddSingleton<IJwksProvider, FakeJwks>();
                s.RemoveAll<IHrClient>(); s.AddSingleton<IHrClient>(Hr);
            });
        }
    }

    readonly Factory f;
    public MeEndpointTests(Factory f) => this.f = f;

    static string Token(string empNo) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        claims: [new Claim("empNo", empNo)], expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)));

    async Task<HttpResponseMessage> Get(string url, string? token)
    {
        var c = f.CreateClient();
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.GetAsync(url);
    }

    static async Task<JsonElement> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>());

    [Fact] public async Task No_token_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/me", null)).StatusCode);

    [Fact]
    public async Task Bad_signature_is_401()
    {
        var other = new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048));
        var bad = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: [new Claim("empNo", "E1")],
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(other, SecurityAlgorithms.RsaSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/me", bad)).StatusCode);
    }

    [Fact]
    public async Task Cim_user_is_cim_engineer()
    {
        var r = await Get("/api/me", Token("E1"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("E1", j.GetProperty("empNo").GetString());
        Assert.True(j.GetProperty("isCimEngineer").GetBoolean());
    }

    [Fact]
    public async Task Other_dept_user_is_not_cim_engineer()
    {
        var j = await Json(await Get("/api/me", Token("E2")));
        Assert.False(j.GetProperty("isCimEngineer").GetBoolean());
    }

    [Fact]
    public async Task Missing_dept_is_fail_closed_with_error_but_still_200()
    {
        var r = await Get("/api/me", Token("E3"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.False(j.GetProperty("isCimEngineer").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(j.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Browser_supplied_dept_or_empNo_is_ignored()
    {
        // E2 非 CIM；瀏覽器試圖用 query / header 自稱 CIM 或別人
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("E2"));
        c.DefaultRequestHeaders.Add("X-Dept-Code", "CIM100");
        var j = await Json(await c.GetAsync("/api/me?deptCode=CIM100&empNo=E1&isCimEngineer=true"));
        Assert.Equal("E2", j.GetProperty("empNo").GetString());
        Assert.False(j.GetProperty("isCimEngineer").GetBoolean());
    }

    [Fact]
    public async Task Hr_is_called_with_the_callers_own_jwt()
    {
        var t = Token("E1");
        f.Hr.Calls.Clear();
        await Get("/api/me", t);
        Assert.All(f.Hr.Calls, c => { Assert.Equal("E1", c.EmpNo); Assert.Equal(t, c.Jwt); });
    }
}

public class StartupValidationTests
{
    [Fact]
    public void Empty_string_in_cim_list_fails_startup()
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Cim:DeptCodePrefixes:0"] = "CIM", ["Cim:DeptCodePrefixes:1"] = "" }));
        });
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }
}

public class HrClientTests
{
    class Handler : HttpMessageHandler
    {
        public HttpRequestMessage? Req;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = """{"empNo":"E1","deptCode":"CIM100"}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Req = r;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    class Factory(Handler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h); }

    static HrClient Make(Handler h) => new(new Factory(h), new StaticAddressProvider(["http://uac/"]));

    [Fact]
    public async Task Calls_hr_endpoint_with_bearer_and_parses_dept()
    {
        var h = new Handler();
        Assert.Equal("CIM100", await Make(h).GetDeptCodeAsync("E1", "tok", default));
        Assert.Equal("http://uac/HrEmployee/E1?includeDept=true", h.Req!.RequestUri!.ToString());
        Assert.Equal("Bearer tok", h.Req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Missing_or_empty_dept_returns_null()
    {
        var h = new Handler { Body = """{"empNo":"E1","deptCode":""}""" };
        Assert.Null(await Make(h).GetDeptCodeAsync("E1", "tok", default));
        h.Body = """{"empNo":"E1"}""";
        Assert.Null(await Make(h).GetDeptCodeAsync("E1", "tok", default));
    }

    [Fact]
    public async Task Http_error_throws()
    {
        var h = new Handler { Status = HttpStatusCode.Forbidden };
        await Assert.ThrowsAsync<HttpRequestException>(() => Make(h).GetDeptCodeAsync("E1", "tok", default));
    }
}
