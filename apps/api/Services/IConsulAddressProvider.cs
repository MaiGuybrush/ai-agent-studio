#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiAgentStudio.Api.Services
{
    /// <summary>
    /// Consul 服務位址供應介面（通用版）
    /// </summary>
    public interface IConsulAddressProvider
    {
        /// <summary>
        /// 依服務名稱非同步取得備援位址清單
        /// </summary>
        /// <param name="serviceName">服務名稱（不分大小寫）</param>
        /// <param name="cancellationToken">取消語彙基元</param>
        /// <returns>位址清單（空清單表示無法取得）</returns>
        Task<IReadOnlyList<string>> GetAddressesAsync(string serviceName, CancellationToken cancellationToken = default);

        /// <summary>
        /// 依服務名稱同步取得備援位址清單（供 JwtBearer 同步委派使用）
        /// </summary>
        /// <param name="serviceName">服務名稱（不分大小寫）</param>
        /// <returns>位址清單（空清單表示無法取得）</returns>
        IReadOnlyList<string> GetAddresses(string serviceName);
    }
}
