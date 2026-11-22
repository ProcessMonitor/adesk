using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AdeskPro.Tests;

public class McpTests
{
    private static (JsonArray Responses, Envelope? LastEnvelope) RunServer(params string[] lines)
    {
        var input = new StringReader(string.Join('\n', lines) + "\n");
        var output = new StringWriter();
        var oldIn = Console.In;
        var oldOut = Console.Out;
        Console.SetIn(input);
        Console.SetOut(output);
        try
        {
            AdeskPro.Mcp.McpServer.Run("test");
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
        }
        var responses = new JsonArray();
        Envelope? last = null;
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var node = JsonNode.Parse(line.Trim())!;
            responses.Add(node.DeepClone());
            var structured = node["result"]?["structuredContent"];
            if (structured is not null)
                last = structured.Deserialize<Envelope>(AdeskPro.EnvelopeJson.Options);
        }
        return (responses, last);
    }

    private static JsonObject Request(int id, string method, JsonObject? @params = null)
    {
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null) req["params"] = @params;
        return req;
    }

    [Fact]
    public void Initialize_negotiates_a_supported_protocol()
    {
        var (responses, _) = RunServer(
            Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }).ToJsonString());

        var result = responses[0]!["result"]!.AsObject();
        Assert.Equal("2025-03-26", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("adesk-pro", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public void Tools_list_covers_the_registry()
    {
        var (responses, _) = RunServer(Request(1, "tools/list").ToJsonString());
        var tools = responses[0]!["result"]!["tools"]!.AsArray();
        Assert.Equal(AdeskPro.Registry.Commands.Length, tools.Count);
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("snapshot", names);
        Assert.Contains("click", names);
        Assert.Contains("type", names);
    }

    [Fact]
    public void Status_call_returns_envelope_and_short_text()
    {
        var (responses, envelope) = RunServer(
            Request(1, "tools/call", new JsonObject { ["name"] = "status", ["arguments"] = new JsonObject() }).ToJsonString());

        var result = responses[0]!["result"]!.AsObject();
        Assert.False(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(text.Length < 200, "MCP text must be a short summary, not the envelope");
        Assert.NotNull(envelope);
        Assert.True(envelope!.Ok);
        Assert.Equal("status", envelope.Data!["command"]!.GetValue<string>());
        Assert.Equal("memory", envelope.Data["stateMode"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_method_is_a_rpc_error()
    {
        var (responses, _) = RunServer(Request(1, "no/such/method").ToJsonString());
        Assert.Equal(-32601, responses[0]!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Unknown_tool_is_a_tool_error()
    {
        var (responses, _) = RunServer(
            Request(1, "tools/call", new JsonObject { ["name"] = "nope", ["arguments"] = new JsonObject() }).ToJsonString());
        Assert.True(responses[0]!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void Notifications_get_no_reply()
    {
        var (responses, _) = RunServer(
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString(),
            Request(2, "tools/list").ToJsonString());
        Assert.Single(responses);   // only tools/list answered
        Assert.Equal(2, responses[0]!["id"]!.GetValue<int>());
    }

    [Fact]
    public void Summarize_stays_short_for_errors_and_snapshots()
    {
        var snap = Envelope.Good(new JsonObject
        {
            ["command"] = "snapshot",
            ["snapshotId"] = "sabc",
            ["refCount"] = 42,
            ["complete"] = true,
            ["tree"] = new JsonObject { ["role"] = "window" },
        });
        var summary = EnvelopeJson.Summarize(snap);
        Assert.Contains("sabc", summary);
        Assert.Contains("42", summary);
        Assert.True(summary.Length < 120);

        var fail = Envelope.Fail(ErrorCodes.StaleRef, "element is gone");
        Assert.StartsWith("error STALE_REF", EnvelopeJson.Summarize(fail));
    }
}
