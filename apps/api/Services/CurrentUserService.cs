using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace AiAgentStudio.Api.Services
{
    /// <summary>
    /// 當前呼叫者身分服務實作
    /// </summary>
    public class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        {
            ArgumentNullException.ThrowIfNull(httpContextAccessor);
            _httpContextAccessor = httpContextAccessor;
        }

        private ClaimsPrincipal AuthenticatedUser
        {
            get
            {
                var user = _httpContextAccessor.HttpContext?.User;
                return user?.Identity != null && user.Identity.IsAuthenticated ? user : null;
            }
        }

        public string EmpId =>
            FindClaimValue(AuthenticatedUser, "empID")
            ?? FindClaimValue(AuthenticatedUser, ClaimTypes.NameIdentifier);

        public string EmpNo =>
            FindClaimValue(AuthenticatedUser, "empNo");

        public string EmpName =>
            FindClaimValue(AuthenticatedUser, "empName")
            ?? FindClaimValue(AuthenticatedUser, ClaimTypes.Name);

        public string AuthMethod =>
            FindClaimValue(AuthenticatedUser, "authMethod")
            ?? AuthenticatedUser?.Identity?.AuthenticationType;

        private static string FindClaimValue(ClaimsPrincipal user, string claimType)
        {
            if (user == null)
            {
                return null;
            }

            var value = user.Claims.FirstOrDefault(c => string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
