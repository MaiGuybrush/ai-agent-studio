using AiAgentStudio.Api.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiAgentStudio.Api.Tests;

public class CimOptionsTests
{
    static CimOptions Opts(params string[] p) => new() { DeptCodePrefixes = p };

    [Fact] public void Matches_prefix() => Assert.True(Opts("CIM").IsCimDept("CIM100"));
    [Fact] public void Prefix_is_case_sensitive() => Assert.False(Opts("CIM").IsCimDept("cim100"));
    [Fact] public void Non_prefix_does_not_match() => Assert.False(Opts("CIM").IsCimDept("XCIM100"));
    [Fact] public void Any_entry_can_match() => Assert.True(Opts("AAA", "CIM").IsCimDept("CIM1"));
    [Fact] public void Empty_list_means_nobody() => Assert.False(Opts().IsCimDept("CIM100"));
    [Fact] public void Missing_dept_is_not_cim() { Assert.False(Opts("CIM").IsCimDept(null)); Assert.False(Opts("CIM").IsCimDept("")); }
    [Fact] public void Empty_string_entry_is_invalid() => Assert.False(Opts("CIM", "").IsValid);
    [Fact] public void Whitespace_entry_is_invalid() => Assert.False(Opts(" ").IsValid);
    [Fact] public void Empty_list_is_valid() => Assert.True(Opts().IsValid);

    [Fact]
    public void Empty_string_in_config_binds_and_is_rejected()
    {
        // 確認 "" 真的會被綁進陣列，驗證才有意義
        var cfg = new ConfigurationBuilder().AddJsonStream(new MemoryStream("""{"Cim":{"DeptCodePrefixes":["CIM",""]}}"""u8.ToArray())).Build();
        var o = cfg.GetSection("Cim").Get<CimOptions>()!;
        Assert.False(o.IsValid);
    }
}

public class IdentityServiceTests
{
    class FakeHr : IHrClient
    {
        public string? Dept = "CIM100";
        public bool Fail;
        public int Calls;
        public string? LastEmpNo, LastJwt;
        public Task<string?> GetDeptCodeAsync(string empNo, string jwt, CancellationToken ct)
        {
            Calls++; LastEmpNo = empNo; LastJwt = jwt;
            return Fail ? throw new HttpRequestException("down") : Task.FromResult(Dept);
        }
    }

    static (IdentityService svc, FakeHr hr, FakeTimeProvider time) Make(params string[] prefixes)
    {
        var hr = new FakeHr();
        var time = new FakeTimeProvider();
        var opts = new OptionsMonitorStub(new CimOptions { DeptCodePrefixes = prefixes });
        return (new IdentityService(hr, opts, time, NullLogger<IdentityService>.Instance), hr, time);
    }

    class OptionsMonitorStub(CimOptions v) : IOptionsMonitor<CimOptions>
    {
        public CimOptions CurrentValue => v;
        public CimOptions Get(string? name) => v;
        public IDisposable? OnChange(Action<CimOptions, string?> listener) => null;
    }

    [Fact]
    public async Task Cim_dept_is_cim_engineer_and_hr_gets_users_own_jwt()
    {
        var (svc, hr, _) = Make("CIM");
        var id = await svc.GetAsync("E1", "jwt-1", default);
        Assert.True(id.IsCimEngineer);
        Assert.Equal("E1", hr.LastEmpNo);
        Assert.Equal("jwt-1", hr.LastJwt);
    }

    [Fact]
    public async Task Other_dept_is_not_cim_engineer()
    {
        var (svc, hr, _) = Make("CIM");
        hr.Dept = "MFG1";
        Assert.False((await svc.GetAsync("E1", "j", default)).IsCimEngineer);
    }

    [Fact]
    public async Task Empty_cim_list_means_nobody_is_cim_engineer()
    {
        var (svc, _, _) = Make();
        Assert.False((await svc.GetAsync("E1", "j", default)).IsCimEngineer);
    }

    [Fact]
    public async Task Dept_is_cached_for_10_minutes_then_refetched()
    {
        var (svc, hr, time) = Make("CIM");
        await svc.GetAsync("E1", "j", default);
        time.Advance(TimeSpan.FromMinutes(9));
        await svc.GetAsync("E1", "j", default);
        Assert.Equal(1, hr.Calls);
        time.Advance(TimeSpan.FromMinutes(1));
        await svc.GetAsync("E1", "j", default);
        Assert.Equal(2, hr.Calls);
    }

    [Fact]
    public async Task Cache_is_per_user()
    {
        var (svc, hr, _) = Make("CIM");
        await svc.GetAsync("E1", "j1", default);
        await svc.GetAsync("E2", "j2", default);
        Assert.Equal(2, hr.Calls);
    }

    [Fact]
    public async Task Dept_change_is_picked_up_after_expiry()
    {
        var (svc, hr, time) = Make("CIM");
        Assert.True((await svc.GetAsync("E1", "j", default)).IsCimEngineer);
        hr.Dept = "MFG1"; // 調離 CIM 部門
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False((await svc.GetAsync("E1", "j", default)).IsCimEngineer);
    }

    [Fact]
    public async Task Hr_failure_fails_closed_with_clear_error()
    {
        var (svc, hr, _) = Make("CIM");
        hr.Fail = true;
        var id = await svc.GetAsync("E1", "j", default);
        Assert.False(id.IsCimEngineer);
        Assert.False(string.IsNullOrWhiteSpace(id.Error));
    }

    [Fact]
    public async Task Missing_dept_fails_closed_with_clear_error()
    {
        var (svc, hr, _) = Make("CIM");
        hr.Dept = null;
        var id = await svc.GetAsync("E1", "j", default);
        Assert.False(id.IsCimEngineer);
        Assert.False(string.IsNullOrWhiteSpace(id.Error));
    }

    [Fact]
    public async Task Expired_cache_is_not_reused_when_refetch_fails()
    {
        var (svc, hr, time) = Make("CIM");
        Assert.True((await svc.GetAsync("E1", "j", default)).IsCimEngineer);
        time.Advance(TimeSpan.FromMinutes(10));
        hr.Fail = true;
        var id = await svc.GetAsync("E1", "j", default);
        Assert.False(id.IsCimEngineer);
        Assert.NotNull(id.Error);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var (svc, hr, _) = Make("CIM");
        hr.Fail = true;
        await svc.GetAsync("E1", "j", default);
        hr.Fail = false;
        Assert.True((await svc.GetAsync("E1", "j", default)).IsCimEngineer);
    }
}
