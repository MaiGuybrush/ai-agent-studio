using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace AiAgentStudio.Api.Interviews;

/// <summary>Python /runs 的事件：text／done／error。</summary>
public sealed record AgentEvent(string Type, JsonElement Data);

public sealed record AgentRunRequest(string RunId, JsonElement[] Messages, string OpeningContext, string RunToken, string ToolApiBaseUrl, JsonNode? Draft = null, JsonNode? DraftDiff = null);

/// <summary>C# → Python agent service（spec 2.2、2.3）。測試時換成假的。</summary>
public interface IAgentClient
{
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest req, CancellationToken ct);
    Task CancelAsync(string runId);
}

public sealed class HttpAgentClient(IHttpClientFactory http, IConfiguration cfg) : IAgentClient
{
    HttpClient Client()
    {
        var c = http.CreateClient("Agent");
        c.BaseAddress = new Uri(cfg["Agent:BaseUrl"] ?? "http://127.0.0.1:8100");
        c.DefaultRequestHeaders.Add("X-Agent-Key", cfg["Agent:ApiKey"] ?? "");
        return c;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new { run_id = req.RunId, messages = req.Messages, opening_context = req.OpeningContext, run_token = req.RunToken, tool_api_base_url = req.ToolApiBaseUrl, draft = req.Draft, draft_diff = req.DraftDiff };
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/runs") { Content = JsonContent.Create(body) };
        using var res = await Client().SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using var rd = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        string type = "message";
        while (await rd.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event:")) type = line[6..].Trim();
            else if (line.StartsWith("data:"))
            {
                yield return new AgentEvent(type, JsonDocument.Parse(line[5..]).RootElement.Clone());
                type = "message";
            }
        }
    }

    public async Task CancelAsync(string runId)
    {
        try { (await Client().PostAsync($"/runs/{Uri.EscapeDataString(runId)}/cancel", null)).Dispose(); }
        catch (HttpRequestException) { } // 盡力而為：連線中斷時 Python 本來就會中止
    }
}
