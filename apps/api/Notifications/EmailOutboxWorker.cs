using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiAgentStudio.Api.Notifications;

/// <summary>定時寄出 email_outbox 並清除過期資料。邏輯都在 <see cref="EmailOutboxDispatcher"/>（有測試）；這裡只負責排程。</summary>
public sealed class EmailOutboxWorker(IServiceScopeFactory scopes, IOptions<EmailOptions> options, IHostEnvironment env, ILogger<EmailOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (env.IsEnvironment("Testing")) return; // 測試直接呼叫 dispatcher，避免背景執行造成不確定性
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var d = scope.ServiceProvider.GetRequiredService<EmailOutboxDispatcher>();
                await d.DispatchDueAsync(stop);
                await d.PurgeAsync(stop);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                log.LogError(e, "email_outbox 處理失敗，下個週期再試");
            }
        } while (!stop.IsCancellationRequested && await timer.WaitForNextTickAsync(stop).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result, CancellationToken.None));
    }
}
