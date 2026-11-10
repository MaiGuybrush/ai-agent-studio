#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace AiAgentStudio.Api.Services
{
    /// <summary>
    /// UAC JWKS 公鑰供應實作
    /// </summary>
    public sealed class UacJwksProvider : IJwksProvider, IDisposable
    {
        private const string ServiceName = "UacApi";
        private const string HttpClientName = "UacClient";
        private const string JwksPath = "/.well-known/jwks.json";
        private const int DefaultCacheHours = 24;
        private const int BackoffMinutes = 3;

        private readonly IConsulAddressProvider _addressProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<UacJwksProvider> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly int _cacheHours;

        // 使用單一 SemaphoreSlim 協調同步與非同步呼叫，避免重複對 UAC 發送請求
        private readonly SemaphoreSlim _lock = new(1, 1);
        private volatile JwksSnapshot? _snapshot;
        private bool _disposed;

        private sealed record JwksSnapshot(
            IReadOnlyList<SecurityKey> Keys,
            DateTimeOffset CacheExpiresAt,
            DateTimeOffset BackoffUntil,
            bool HasSucceeded);

        public UacJwksProvider(
            IConsulAddressProvider addressProvider,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<UacJwksProvider> logger,
            TimeProvider? timeProvider = null)
        {
            _addressProvider = addressProvider ?? throw new ArgumentNullException(nameof(addressProvider));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? TimeProvider.System;

            var cacheHoursStr = configuration?["Authentication:Uac:JwksCacheHours"];
            if (!int.TryParse(cacheHoursStr, out var cacheHours) || cacheHours <= 0)
            {
                cacheHours = DefaultCacheHours;
            }
            _cacheHours = cacheHours;
        }

        /// <inheritdoc />
        public IEnumerable<SecurityKey> GetSigningKeys(string? kid = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();

            // 1. 快速路徑（無鎖）：若 JWKS 快取仍然有效，直接過濾金鑰回傳
            var snapshot = _snapshot;
            if (snapshot != null)
            {
                if (now < snapshot.CacheExpiresAt)
                {
                    return FilterKeys(snapshot.Keys, kid);
                }

                // 2. 退避期間：不重複請求 UAC 節點，曾成功則沿用金鑰，否則回傳空金鑰
                if (now < snapshot.BackoffUntil)
                {
                    return snapshot.HasSucceeded ? FilterKeys(snapshot.Keys, kid) : Array.Empty<SecurityKey>();
                }
            }

            // JWKS 在快取失效時同步阻塞呼叫，是刻意接受的取捨（見 ADR-0001）：JwtBearer 的 IssuerSigningKeyResolver 為同步委派，JWKS 快取失效時會短暫阻塞一個請求執行緒。
            _lock.Wait();
            try
            {
                now = _timeProvider.GetUtcNow();
                snapshot = _snapshot;
                if (snapshot != null)
                {
                    if (now < snapshot.CacheExpiresAt)
                    {
                        return FilterKeys(snapshot.Keys, kid);
                    }
                    if (now < snapshot.BackoffUntil)
                    {
                        return snapshot.HasSucceeded ? FilterKeys(snapshot.Keys, kid) : Array.Empty<SecurityKey>();
                    }
                }

                var addresses = _addressProvider.GetAddresses(ServiceName);
                if (addresses.Count == 0)
                {
                    HandleFailure(now, "No UAC API addresses available from Consul");
                    return _snapshot!.HasSucceeded ? FilterKeys(_snapshot.Keys, kid) : Array.Empty<SecurityKey>();
                }

                var httpClient = _httpClientFactory.CreateClient(HttpClientName);
                foreach (var address in addresses)
                {
                    var baseUrl = address.TrimEnd('/');
                    var jwksUrl = $"{baseUrl}{JwksPath}";

                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, jwksUrl);
                        using var response = httpClient.Send(request);

                        if (response.IsSuccessStatusCode)
                        {
                            using var streamReader = new System.IO.StreamReader(response.Content.ReadAsStream());
                            var content = streamReader.ReadToEnd();
                            var jwks = new JsonWebKeySet(content);
                            var keys = jwks.GetSigningKeys().ToList();
                            if (keys.Count == 0)
                            {
                                _logger.LogWarning("JWKS from UAC node '{JwksUrl}' contains no valid signing keys. Trying next node if available.", jwksUrl);
                                continue;
                            }

                            _snapshot = new JwksSnapshot(
                                Keys: keys,
                                CacheExpiresAt: now.AddHours(_cacheHours),
                                BackoffUntil: DateTimeOffset.MinValue,
                                HasSucceeded: true);

                            _logger.LogInformation("Successfully fetched JWKS from UAC node '{JwksUrl}'. Cached for {CacheHours} hours.", jwksUrl, _cacheHours);
                            return FilterKeys(keys, kid);
                        }

                        _logger.LogWarning("Failed to fetch JWKS from UAC node '{JwksUrl}': HTTP status code {StatusCode}. Trying next node if available.", jwksUrl, (int)response.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Exception occurred while fetching JWKS from UAC node '{JwksUrl}'. Trying next node if available.", jwksUrl);
                    }
                }

                HandleFailure(now, "Failed to fetch JWKS from all available UAC nodes");
                return _snapshot!.HasSucceeded ? FilterKeys(_snapshot.Keys, kid) : Array.Empty<SecurityKey>();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<SecurityKey>> GetSigningKeysAsync(string? kid = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();

            var snapshot = _snapshot;
            if (snapshot != null)
            {
                if (now < snapshot.CacheExpiresAt)
                {
                    return FilterKeys(snapshot.Keys, kid);
                }
                if (now < snapshot.BackoffUntil)
                {
                    return snapshot.HasSucceeded ? FilterKeys(snapshot.Keys, kid) : Array.Empty<SecurityKey>();
                }
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = _timeProvider.GetUtcNow();
                snapshot = _snapshot;
                if (snapshot != null)
                {
                    if (now < snapshot.CacheExpiresAt)
                    {
                        return FilterKeys(snapshot.Keys, kid);
                    }
                    if (now < snapshot.BackoffUntil)
                    {
                        return snapshot.HasSucceeded ? FilterKeys(snapshot.Keys, kid) : Array.Empty<SecurityKey>();
                    }
                }

                var addresses = await _addressProvider.GetAddressesAsync(ServiceName, cancellationToken).ConfigureAwait(false);
                if (addresses.Count == 0)
                {
                    HandleFailure(now, "No UAC API addresses available from Consul");
                    return _snapshot!.HasSucceeded ? FilterKeys(_snapshot.Keys, kid) : Array.Empty<SecurityKey>();
                }

                var httpClient = _httpClientFactory.CreateClient(HttpClientName);
                foreach (var address in addresses)
                {
                    var baseUrl = address.TrimEnd('/');
                    var jwksUrl = $"{baseUrl}{JwksPath}";

                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, jwksUrl);
                        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                        if (response.IsSuccessStatusCode)
                        {
                            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            var jwks = new JsonWebKeySet(content);
                            var keys = jwks.GetSigningKeys().ToList();
                            if (keys.Count == 0)
                            {
                                _logger.LogWarning("JWKS from UAC node '{JwksUrl}' contains no valid signing keys. Trying next node if available.", jwksUrl);
                                continue;
                            }

                            _snapshot = new JwksSnapshot(
                                Keys: keys,
                                CacheExpiresAt: now.AddHours(_cacheHours),
                                BackoffUntil: DateTimeOffset.MinValue,
                                HasSucceeded: true);

                            _logger.LogInformation("Successfully fetched JWKS from UAC node '{JwksUrl}'. Cached for {CacheHours} hours.", jwksUrl, _cacheHours);
                            return FilterKeys(keys, kid);
                        }

                        _logger.LogWarning("Failed to fetch JWKS from UAC node '{JwksUrl}': HTTP status code {StatusCode}. Trying next node if available.", jwksUrl, (int)response.StatusCode);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Exception occurred while fetching JWKS from UAC node '{JwksUrl}'. Trying next node if available.", jwksUrl);
                    }
                }

                HandleFailure(now, "Failed to fetch JWKS from all available UAC nodes");
                return _snapshot!.HasSucceeded ? FilterKeys(_snapshot.Keys, kid) : Array.Empty<SecurityKey>();
            }
            finally
            {
                _lock.Release();
            }
        }

        private void HandleFailure(DateTimeOffset now, string reason)
        {
            var current = _snapshot;
            var backoffUntil = now.AddMinutes(BackoffMinutes);

            if (current != null && current.HasSucceeded)
            {
                _logger.LogWarning(
                    "Unable to refresh JWKS for service '{ServiceName}': {Reason}. Reusing previous keys. Backing off for {BackoffMinutes} minutes until {BackoffUntil}.",
                    ServiceName,
                    reason,
                    BackoffMinutes,
                    backoffUntil);

                _snapshot = new JwksSnapshot(
                    Keys: current.Keys,
                    CacheExpiresAt: DateTimeOffset.MinValue,
                    BackoffUntil: backoffUntil,
                    HasSucceeded: true);
            }
            else
            {
                _logger.LogError(
                    "Unable to fetch JWKS for service '{ServiceName}': {Reason}. No previous keys exist. Backing off for {BackoffMinutes} minutes until {BackoffUntil}.",
                    ServiceName,
                    reason,
                    BackoffMinutes,
                    backoffUntil);

                _snapshot = new JwksSnapshot(
                    Keys: Array.Empty<SecurityKey>(),
                    CacheExpiresAt: DateTimeOffset.MinValue,
                    BackoffUntil: backoffUntil,
                    HasSucceeded: false);
            }
        }

        private static IEnumerable<SecurityKey> FilterKeys(IReadOnlyList<SecurityKey> keys, string? kid)
        {
            if (string.IsNullOrEmpty(kid))
            {
                return keys;
            }

            var matchingKeys = keys.Where(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal)).ToList();
            return matchingKeys.Count > 0 ? matchingKeys : keys;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _lock.Dispose();
            }
        }
    }
}
