using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using AiAgentStudio.Api.Services;

namespace AiAgentStudio.Api
{
    /// <summary>
    /// 請求日誌中介軟體：在請求完成時記錄請求資訊，已認證時帶入呼叫者 EmpID 與 AuthMethod，未認證時不帶 EmpID。
    /// </summary>
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context, ICurrentUserService currentUserService)
        {
            ArgumentNullException.ThrowIfNull(context);

            var stopwatch = Stopwatch.StartNew();
            var empId = TryGetEmpId(currentUserService);
            var authMethod = TryGetAuthMethod(currentUserService);

            IDisposable scope = null;
            if (!string.IsNullOrEmpty(empId))
            {
                scope = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["EmpID"] = empId,
                    ["AuthMethod"] = authMethod ?? "Unknown"
                });
            }

            Exception capturedException = null;
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                capturedException = ex;
                throw;
            }
            finally
            {
                try
                {
                    stopwatch.Stop();
                    var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                    var statusCode = capturedException != null
                        ? (context.Response.StatusCode == StatusCodes.Status200OK ? StatusCodes.Status500InternalServerError : context.Response.StatusCode)
                        : context.Response.StatusCode;
                    var method = context.Request?.Method ?? "UNKNOWN";
                    var path = context.Request?.Path.Value ?? string.Empty;

                    empId ??= TryGetEmpId(currentUserService);
                    authMethod ??= TryGetAuthMethod(currentUserService);

                    LogRequest(method, path, statusCode, elapsedMs, empId, authMethod, capturedException);
                }
                finally
                {
                    scope?.Dispose();
                }
            }
        }

        private void LogRequest(string method, string path, int statusCode, double elapsedMs, string empId, string authMethod, Exception exception)
        {
            var level = statusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Information;

            if (!string.IsNullOrEmpty(empId))
            {
                _logger.Log(
                    level,
                    exception,
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:0.00}ms | EmpID: {EmpID}, AuthMethod: {AuthMethod}",
                    method, path, statusCode, elapsedMs, empId, authMethod ?? "Unknown");
            }
            else
            {
                _logger.Log(
                    level,
                    exception,
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:0.00}ms",
                    method, path, statusCode, elapsedMs);
            }
        }

        private static string TryGetEmpId(ICurrentUserService currentUserService)
        {
            try
            {
                var id = currentUserService?.EmpId;
                return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetAuthMethod(ICurrentUserService currentUserService)
        {
            try
            {
                var method = currentUserService?.AuthMethod;
                return string.IsNullOrWhiteSpace(method) ? null : method.Trim();
            }
            catch
            {
                return null;
            }
        }
    }

    public static class RequestLoggingMiddlewareExtensions
    {
        public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<RequestLoggingMiddleware>();
        }
    }
}
