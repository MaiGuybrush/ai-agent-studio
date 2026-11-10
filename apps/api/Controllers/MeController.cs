using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiAgentStudio.Api.Controllers;

[ApiController]
[Route("api/me")]
public class MeController(ICurrentUserService user, IdentityService identity) : ControllerBase
{
    /// <summary>目前登入者身分。empNo 取自已驗證的 JWT；沒有任何輸入參數可影響判定。</summary>
    [HttpGet, Authorize]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (user.EmpNo is not { } empNo) return Forbid(); // 例如 API Key 登入，沒有 empNo
        var jwt = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var id = await identity.GetAsync(empNo, jwt, ct);
        return Ok(new { empNo = id.EmpNo, name = user.EmpName, isCimEngineer = id.IsCimEngineer, deptCode = id.DeptCode, error = id.Error });
    }
}
