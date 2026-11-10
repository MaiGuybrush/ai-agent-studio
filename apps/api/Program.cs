using System.Runtime.InteropServices;
using AiAgentStudio.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// 設定監聽位址 http://*:5080
builder.WebHost.UseUrls("http://*:5080");

// 日誌配置
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder.Logging.AddEventLog();
}
builder.Logging.AddNLog("nlog.config");

// 服務註冊：應用程式服務與基礎設施服務
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

// Swagger 配置
app.UseSwagger(c =>
{
    c.RouteTemplate = "api-docs/{documentName}/swagger.json";
});
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("../api-docs/v1/swagger.json", "AiAgentStudio.Api");
    c.RoutePrefix = "swagger";
});

// 例外處理
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseHttpsRedirection();

app.UseRouting();

// CORS
app.UseCors("CorsPolicy");

// NoteMiddleware
app.UseNoteMiddleware();

// 顯式宣告 UseAuthentication 與 UseAuthorization (順序在 CORS 之後)
app.UseAuthentication();
app.UseRequestLogging();
app.UseAuthorization();

// Response Caching（刻意置於 UseAuthorization 之後，避免預設 FallbackPolicy 下未授權請求命中快取而繞過認證）
app.UseResponseCaching();

// HealthCheck 與 Controllers
app.MapHealthChecks("/health", new HealthCheckOptions
{
    AllowCachingResponses = false
}).AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program
{
}
