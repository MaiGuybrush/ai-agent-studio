#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Consul;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AiAgentStudio.Api.Services
{
    /// <summary>
    /// Consul 服務位址供應實作（通用版）
    /// </summary>
    public sealed class ConsulAddressProvider : IConsulAddressProvider, IDisposable
    {
        private const string ApiUrlsKey = "ApiUrls";
        private const int DefaultCacheMinutes = 5;
        private const int BackoffMinutes = 3;

        private readonly IConsulClient _consulClient;
        private readonly ILogger<ConsulAddressProvider> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly int _cacheMinutes;

        private readonly SemaphoreSlim _consulLock = new(1, 1);
        private readonly ConcurrentDictionary<string, ServiceState> _serviceStates =
            new(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        private sealed record ServiceSnapshot(
            IReadOnlyList<string> Addresses,
            DateTimeOffset CacheExpiresAt,
            DateTimeOffset BackoffUntil,
            bool HasSucceeded);

        private sealed class ServiceState
        {
            public volatile ServiceSnapshot Snapshot = new(
                Addresses: Array.Empty<string>(),
                CacheExpiresAt: DateTimeOffset.MinValue,
                BackoffUntil: DateTimeOffset.MinValue,
                HasSucceeded: false);
        }

        public ConsulAddressProvider(
            IConsulClient consulClient,
            IConfiguration configuration,
            ILogger<ConsulAddressProvider> logger,
            TimeProvider? timeProvider = null)
        {
            _consulClient = consulClient ?? throw new ArgumentNullException(nameof(consulClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? TimeProvider.System;

            var cacheMinutesStr = configuration?["Consul:CacheMinutes"];
            if (!int.TryParse(cacheMinutesStr, out var cacheMinutes) || cacheMinutes <= 0)
            {
                cacheMinutes = DefaultCacheMinutes;
            }
            _cacheMinutes = cacheMinutes;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> GetAddressesAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrWhiteSpace(serviceName))
            {
                return Array.Empty<string>();
            }

            serviceName = serviceName.Trim();
            var now = _timeProvider.GetUtcNow();
            var state = _serviceStates.GetOrAdd(serviceName, _ => new ServiceState());

            // 1. 快速路徑（無鎖）：快取仍在有效期間，直接回傳
            var snapshot = state.Snapshot;
            if (now < snapshot.CacheExpiresAt)
            {
                return snapshot.Addresses;
            }

            // 2. 退避期間：不呼叫 Consul，曾成功則回傳最後成功值，否則回傳空清單
            if (now < snapshot.BackoffUntil)
            {
                return snapshot.HasSucceeded ? snapshot.Addresses : Array.Empty<string>();
            }

            // 3. 慢速路徑：取得鎖以避免多個執行緒同時向 Consul 重複請求
            await _consulLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Double-checked locking 檢查快取與退避狀態
                now = _timeProvider.GetUtcNow();
                snapshot = state.Snapshot;
                if (now < snapshot.CacheExpiresAt)
                {
                    return snapshot.Addresses;
                }
                if (now < snapshot.BackoffUntil)
                {
                    return snapshot.HasSucceeded ? snapshot.Addresses : Array.Empty<string>();
                }

                // 向 Consul 請求 ApiUrls
                QueryResult<KVPair>? queryResult;
                try
                {
                    queryResult = await _consulClient.KV.Get(ApiUrlsKey, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 呼叫端主動取消不得觸發退避
                    throw;
                }
                catch (Exception ex)
                {
                    HandleFailure(serviceName, state, now, ex, "Consul K/V request threw an exception.");
                    return state.Snapshot.Addresses;
                }

                if (queryResult?.Response?.Value == null || queryResult.Response.Value.Length == 0)
                {
                    HandleFailure(serviceName, state, now, null, $"Key '{ApiUrlsKey}' does not exist or value is empty in Consul.");
                    return state.Snapshot.Addresses;
                }

                Dictionary<string, List<string>>? servicesDict;
                try
                {
                    var json = Encoding.UTF8.GetString(queryResult.Response.Value);
                    servicesDict = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (Exception ex)
                {
                    HandleFailure(serviceName, state, now, ex, $"Failed to parse '{ApiUrlsKey}' value as JSON from Consul.");
                    return state.Snapshot.Addresses;
                }

                if (servicesDict == null)
                {
                    HandleFailure(serviceName, state, now, null, $"Parsed '{ApiUrlsKey}' JSON was null.");
                    return state.Snapshot.Addresses;
                }

                // 成功解析 JSON，批次更新所有服務之快取
                var cacheExpiresAt = now.AddMinutes(_cacheMinutes);
                foreach (var entry in servicesDict)
                {
                    var entryKey = entry.Key?.Trim();
                    if (string.IsNullOrEmpty(entryKey))
                    {
                        continue;
                    }

                    var addrs = entry.Value?
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Select(url => url.Trim())
                        .ToList();

                    if (addrs != null && addrs.Count > 0)
                    {
                        var entryState = _serviceStates.GetOrAdd(entryKey, _ => new ServiceState());
                        entryState.Snapshot = new ServiceSnapshot(
                            Addresses: addrs,
                            CacheExpiresAt: cacheExpiresAt,
                            BackoffUntil: DateTimeOffset.MinValue,
                            HasSucceeded: true);
                    }
                }

                // 服務名稱比對不分大小寫
                var matchingEntry = servicesDict.FirstOrDefault(kv => string.Equals(kv.Key?.Trim(), serviceName, StringComparison.OrdinalIgnoreCase));
                if (matchingEntry.Key == null || matchingEntry.Value == null)
                {
                    HandleFailure(serviceName, state, now, null, $"Service '{serviceName}' was not found in Consul '{ApiUrlsKey}'.");
                    return state.Snapshot.Addresses;
                }

                // 略過空白位址
                var validAddresses = matchingEntry.Value
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Select(url => url.Trim())
                    .ToList();

                if (validAddresses.Count == 0)
                {
                    HandleFailure(serviceName, state, now, null, $"Service '{serviceName}' has an empty address list in Consul.");
                    return state.Snapshot.Addresses;
                }

                state.Snapshot = new ServiceSnapshot(
                    Addresses: validAddresses,
                    CacheExpiresAt: cacheExpiresAt,
                    BackoffUntil: DateTimeOffset.MinValue,
                    HasSucceeded: true);

                return validAddresses;
            }
            finally
            {
                _consulLock.Release();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetAddresses(string serviceName)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (string.IsNullOrWhiteSpace(serviceName))
            {
                return Array.Empty<string>();
            }

            serviceName = serviceName.Trim();
            var now = _timeProvider.GetUtcNow();
            var state = _serviceStates.GetOrAdd(serviceName, _ => new ServiceState());

            // 快速路徑：若仍在快取或退避期間，不需非同步執行
            var snapshot = state.Snapshot;
            if (now < snapshot.CacheExpiresAt)
            {
                return snapshot.Addresses;
            }
            if (now < snapshot.BackoffUntil)
            {
                return snapshot.HasSucceeded ? snapshot.Addresses : Array.Empty<string>();
            }

            // 慢速路徑：供 JwtBearer IssuerSigningKeyResolver 同步委派調用
            return Task.Run(() => GetAddressesAsync(serviceName, CancellationToken.None)).GetAwaiter().GetResult();
        }

        private void HandleFailure(
            string serviceName,
            ServiceState state,
            DateTimeOffset now,
            Exception? exception,
            string message)
        {
            var current = state.Snapshot;
            var backoffUntil = now.AddMinutes(BackoffMinutes);

            if (current.HasSucceeded)
            {
                // 曾成功則沿用最後成功值並記錄 Warning，進入 3 分鐘退避
                _logger.LogWarning(
                    exception,
                    "Failed to fetch addresses for service '{ServiceName}' from Consul: {Message}. Reusing last known addresses. Backing off for {BackoffMinutes} minutes until {BackoffUntil}.",
                    serviceName,
                    message,
                    BackoffMinutes,
                    backoffUntil);

                state.Snapshot = new ServiceSnapshot(
                    Addresses: current.Addresses,
                    CacheExpiresAt: DateTimeOffset.MinValue,
                    BackoffUntil: backoffUntil,
                    HasSucceeded: true);
            }
            else
            {
                // 從未成功則回空清單並記錄 Error（fail closed），進入 3 分鐘退避
                _logger.LogError(
                    exception,
                    "Failed to fetch addresses for service '{ServiceName}' from Consul: {Message}. No previous addresses exist. Backing off for {BackoffMinutes} minutes until {BackoffUntil}.",
                    serviceName,
                    message,
                    BackoffMinutes,
                    backoffUntil);

                state.Snapshot = new ServiceSnapshot(
                    Addresses: Array.Empty<string>(),
                    CacheExpiresAt: DateTimeOffset.MinValue,
                    BackoffUntil: backoffUntil,
                    HasSucceeded: false);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _consulLock.Dispose();
            }
        }
    }
}
