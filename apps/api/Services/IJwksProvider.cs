#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;

namespace AiAgentStudio.Api.Services
{
    /// <summary>
    /// JWKS 金鑰供應介面
    /// </summary>
    public interface IJwksProvider
    {
        /// <summary>
        /// 同步取得簽章金鑰清單（供 JwtBearer IssuerSigningKeyResolver 同步委派調用）
        /// </summary>
        /// <param name="kid">金鑰識別碼（可為 null）</param>
        /// <returns>簽章金鑰集合</returns>
        IEnumerable<SecurityKey> GetSigningKeys(string? kid = null);

        /// <summary>
        /// 非同步取得簽章金鑰清單
        /// </summary>
        /// <param name="kid">金鑰識別碼（可為 null）</param>
        /// <param name="cancellationToken">取消語彙基元</param>
        /// <returns>簽章金鑰集合</returns>
        Task<IEnumerable<SecurityKey>> GetSigningKeysAsync(string? kid = null, CancellationToken cancellationToken = default);
    }
}
