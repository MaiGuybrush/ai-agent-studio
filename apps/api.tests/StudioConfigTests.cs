using AiAgentStudio.Api.Studio;

namespace AiAgentStudio.Api.Tests;

public class StudioConfigTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    const string Json = """
    {
      "mcpServers": {
        "gw": { "type": "http", "url": "http://gw/cim/mcp", "headers": { "X-Api-Key": "${GW_KEY}" } },
        "local": { "type": "stdio", "command": "node", "args": ["a.js", "${GW_KEY}"], "env": { "TOKEN": "${GW_KEY}" } }
      },
      "systems": { "MES": "10000001" },
      "cimDeptCodes": ["CIM100"]
    }
    """;

    StudioConfigProvider Provider(string? key = "secret") =>
        new(path, n => n == "GW_KEY" ? key : null);

    [Fact]
    public void Parses_mcpServers_and_substitutes_env_vars()
    {
        File.WriteAllText(path, Json);
        var c = Provider().Current;

        var gw = c.McpServers["gw"];
        Assert.Equal("http", gw.Type);
        Assert.Equal("http://gw/cim/mcp", gw.Url);
        Assert.Equal("secret", gw.Headers["X-Api-Key"]);
        var local = c.McpServers["local"];
        Assert.Equal("node", local.Command);
        Assert.Equal(["a.js", "secret"], local.Args);
        Assert.Equal("secret", local.Env["TOKEN"]);
        Assert.Equal("10000001", c.Systems["MES"]);
        Assert.Equal(["CIM100"], c.CimDeptCodes);
    }

    [Fact]
    public void Missing_env_var_is_an_error_not_an_empty_string()
    {
        File.WriteAllText(path, Json);
        var e = Assert.Throws<InvalidDataException>(() => Provider(key: null).Current);
        Assert.Contains("GW_KEY", e.Message);
    }

    [Fact]
    public void Sse_and_unknown_types_are_rejected()
    {
        File.WriteAllText(path, """{ "mcpServers": { "x": { "type": "sse", "url": "http://x" } } }""");
        var e = Assert.Throws<InvalidDataException>(() => Provider().Current);
        Assert.Contains("sse", e.Message);
    }

    [Fact]
    public void Editing_the_file_reloads_without_restart()
    {
        File.WriteAllText(path, Json);
        var p = Provider();
        Assert.Equal("10000001", p.Current.Systems["MES"]);

        File.WriteAllText(path, Json.Replace("10000001", "20000002 "));
        Assert.Equal("20000002 ", p.Current.Systems["MES"]);
    }
}
