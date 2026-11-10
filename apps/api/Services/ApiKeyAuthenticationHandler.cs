#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiAgentStudio.Api.Services
{
    /// <summary>
    /// API Key 驗證選項
    /// </summary>
    public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
    {
        public const string DefaultScheme = "ApiKey";

        /// <summary>
        /// 專案所屬之 System Scope 名稱（程式碼不設備援值）
        /// </summary>
        public string? SystemName { get; set; }

        /// <summary>
        /// 快取時間（分鐘），預設 30 分鐘
        /// </summary>
        public int ApiKeyCacheMinutes { get; set; } = 30;
    }

    /// <summary>
    /// UAC 回傳之 API Key 身分資訊
    /// </summary>
    public class ApiKeyIdentity
    {
        [JsonPropertyName("empId")]
        public string EmpId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("empName")]
        public string? EmpName { get; set; }

        [JsonPropertyName("empNo")]
        public string? EmpNo { get; set; }
    }

    /// <summary>
    /// API Key 認證處理器
    /// 支援 X-API-Key 標頭或非三段式 JWT 之 Bearer 認證
    /// </summary>
    public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
    {
        private const string ServiceName = "UacApi";
        private const string HttpClientName = "UacClient";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConsulAddressProvider _consulAddressProvider;
        private readonly IMemoryCache _memoryCache;
        private readonly IConfiguration _configuration;
        private readonly TimeProvider _timeProvider;

        public ApiKeyAuthenticationHandler(
            IOptionsMonitor<ApiKeyAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IHttpClientFactory httpClientFactory,
            IConsulAddressProvider consulAddressProvider,
            IMemoryCache memoryCache,
            IConfiguration configuration,
            TimeProvider? timeProvider = null)
            : base(options, logger, encoder)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _consulAddressProvider = consulAddressProvider ?? throw new ArgumentNullException(nameof(consulAddressProvider));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // 1. 取得 API Key（優先檢查 X-API-Key，次之為 Bearer）
            string? apiKey = null;
            if (Request.Headers.TryGetValue("X-API-Key", out var xApiKey) && !string.IsNullOrWhiteSpace(xApiKey))
            {
                apiKey = xApiKey.ToString().Trim();
            }
            else if (Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                var authStr = authHeader.ToString();
                if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var token = authStr["Bearer ".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(token) && !ServiceCollectionExtensions.IsThreePartJwt(token))
                    {
                        apiKey = token;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return AuthenticateResult.NoResult();
            }

            // 2. 以 SHA-256 雜湊作為快取鍵（apikey:{HEX}，絕不以明文為鍵）
            var keyHash = ComputeSha256Hex(apiKey);
            var cacheKey = $"apikey:{keyHash}";

            // 3. 檢查快取
            if (_memoryCache.TryGetValue<ApiKeyIdentity>(cacheKey, out var cachedIdentity) && cachedIdentity != null)
            {
                Logger.LogDebug("API Key cache hit for hash {KeyHash}", keyHash);
                return AuthenticateResult.Success(CreateTicket(cachedIdentity));
            }

            // 4. 取得 SystemName（程式碼不設備援值）
            var systemName = Options.SystemName;
            if (string.IsNullOrWhiteSpace(systemName))
            {
                systemName = _configuration["Authentication:Uac:SystemName"];
            }

            if (string.IsNullOrWhiteSpace(systemName))
            {
                Logger.LogError("SystemName is not configured for UAC API Key validation.");
                return AuthenticateResult.Fail("Authentication:Uac:SystemName is required but not configured.");
            }

            systemName = systemName.Trim();

            // 5. 透過 ConsulAddressProvider 取得 UacApi 位址清單
            IReadOnlyList<string> addresses;
            try
            {
                addresses = await _consulAddressProvider.GetAddressesAsync(ServiceName, Context.RequestAborted);
            }
            catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to get {ServiceName} addresses from Consul.", ServiceName);
                return AuthenticateResult.Fail("UAC API Key verification unavailable");
            }

            if (addresses == null || addresses.Count == 0)
            {
                Logger.LogError("UAC API Key verification unavailable: no addresses returned for {ServiceName}.", ServiceName);
                return AuthenticateResult.Fail("UAC API Key verification unavailable");
            }

            // 6. 依序向節點請求 GET /api-keys/identity?system={SystemName}
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var cacheMinutes = Options.ApiKeyCacheMinutes > 0 ? Options.ApiKeyCacheMinutes : 30;

            foreach (var baseAddress in addresses)
            {
                if (string.IsNullOrWhiteSpace(baseAddress))
                {
                    continue;
                }

                var uri = $"{baseAddress.Trim().TrimEnd('/')}/api-keys/identity?system={Uri.EscapeDataString(systemName)}";

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);

                    using var response = await client.SendAsync(request, Context.RequestAborted);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(Context.RequestAborted);
                        var identity = JsonSerializer.Deserialize<ApiKeyIdentity>(content, SerializerOptions);

                        if (identity == null || string.IsNullOrWhiteSpace(identity.EmpId))
                        {
                            Logger.LogWarning("UAC node {BaseAddress} returned invalid or empty identity payload.", baseAddress);
                            continue;
                        }

                        // 驗證成功，寫入快取（使用 TimeProvider 決定絕對過期時間）
                        _memoryCache.Set(cacheKey, identity, _timeProvider.GetUtcNow().AddMinutes(cacheMinutes));
                        Logger.LogInformation("API Key authenticated successfully for EmpId: {EmpId}", identity.EmpId);
                        return AuthenticateResult.Success(CreateTicket(identity));
                    }
                    else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        // UAC 回 401 或 403 立即拒絕且不寫入快取，不換下一個節點
                        Logger.LogWarning("UAC node {BaseAddress} rejected API Key with status code {StatusCode}.", baseAddress, response.StatusCode);
                        return AuthenticateResult.Fail($"API Key rejected by UAC: {response.StatusCode}");
                    }
                    else
                    {
                        // 伺服器錯誤換下一個節點
                        Logger.LogWarning("UAC node {BaseAddress} returned HTTP {StatusCode}. Trying next node...", baseAddress, response.StatusCode);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 逾時或其他網路錯誤換下一個節點
                    Logger.LogWarning(ex, "Failed to connect to UAC node {BaseAddress}. Trying next node...", baseAddress);
                    continue;
                }
            }

            // 全部失敗回 401（UAC API Key verification unavailable）
            Logger.LogError("UAC API Key verification unavailable: all nodes failed or timed out.");
            return AuthenticateResult.Fail("UAC API Key verification unavailable");
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                Response.Headers.Append("WWW-Authenticate", "ApiKey");
            }
            return Task.CompletedTask;
        }

        private AuthenticationTicket CreateTicket(ApiKeyIdentity identity)
        {
            var empId = identity.EmpId.Trim();
            var name = !string.IsNullOrWhiteSpace(identity.EmpName)
                ? identity.EmpName.Trim()
                : (!string.IsNullOrWhiteSpace(identity.Name) ? identity.Name.Trim() : empId);

            var claims = new List<Claim>
            {
                new Claim("empID", empId),
                new Claim("empName", name),
                new Claim("authMethod", "ApiKey"),
                new Claim(ClaimTypes.NameIdentifier, empId),
                new Claim(ClaimTypes.Name, name)
            };

            if (!string.IsNullOrWhiteSpace(identity.EmpNo))
            {
                claims.Add(new Claim("empNo", identity.EmpNo.Trim()));
            }

            var claimsIdentity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(claimsIdentity);
            return new AuthenticationTicket(principal, Scheme.Name);
        }

        private static string ComputeSha256Hex(string input)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexStringLower(hash);
        }
    }
}
