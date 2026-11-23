using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdeskPro.Ui;

namespace AdeskPro.Mcp;

/// <summary>
/// Stdio MCP server. Every tool call dispatches IN PROCESS through the same Registry as the CLI —
/// no subprocess per call, no global file lock; the ref store lives in server memory.
/// </summary>
public static class McpServer
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Run(Platform.IPlatform platform, string sessionName)
    {
        var session = new Session
        {
            Name = sessionName,
            InMemory = true,
            Refs = new RefStore(null),
            Platform = platform,
        };
        var stdout = Console.Out;
        while (Console.In.ReadLine() is { } line)
        {
            if (line.Length > MaxMessageBytes) { Reply(stdout, RpcError(null, -32600, "message exceeds limit")); continue; }
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException ex) { Reply(stdout, RpcError(null, -32700, $"parse error: {ex.Message}")); continue; }
            if (message is null) { Reply(stdout, RpcError(null, -32600, "expected one JSON-RPC object")); continue; }

            var method = message["method"]?.GetValue<string>() ?? "";
            var id = message["id"];
            if (method.StartsWith("notifications/") || id is null) continue;   // notifications get no reply

            switch (method)
            {
                case "initialize":
                    Reply(stdout, RpcResult(id, InitializeResult(message)));
                    break;
                case "ping":
                    Reply(stdout, RpcResult(id, new JsonObject()));
                    break;
                case "tools/list":
                    Reply(stdout, RpcResult(id, new JsonObject { ["tools"] = Catalog.Build() }));
                    break;
                case "tools/call":
                    Reply(stdout, RpcResult(id, CallTool(message, session)));
                    break;
                default:
                    Reply(stdout, RpcError(id, -32601, $"method not found: {method}"));
                    break;
            }
        }
    }

    private static JsonObject InitializeResult(JsonObject request)
    {
        var requested = request["params"]?["protocolVersion"]?.GetValue<string>();
        var protocol = requested is not null && SupportedProtocols.Contains(requested) ? requested : LatestProtocol;
        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "adesk-pro",
                ["version"] = Version.Value,
                ["title"] = "adesk-pro — desktop use via accessibility trees",
            },
            ["instructions"] =
                "adesk-pro reads Windows accessibility trees and operates desktop apps. Call the 'skills' tool " +
                "first for the full usage guide. In short: take a 'snapshot' of the app, act on refs from it " +
                "(@sid:eN) with click/type/set-value/press, then re-snapshot to verify. Retry an action only " +
                "when its disposition.retry is safe.",
        };
    }

    private static JsonObject CallTool(JsonObject request, Session session)
    {
        var name = request["params"]?["name"]?.GetValue<string>() ?? "";
        var arguments = request["params"]?["arguments"] as JsonObject ?? new JsonObject();
        return EnvelopeResult(Handlers.Dispatch(name, arguments, session));
    }

    /// <summary>Structured content carries the envelope; text is a one-line summary; inline screenshots become image blocks.</summary>
    private static JsonObject EnvelopeResult(Envelope envelope)
    {
        var structured = JsonSerializer.SerializeToNode(envelope, EnvelopeJson.Options)?.AsObject() ?? new JsonObject();
        var image = TakeInlineImage(structured);
        var text = EnvelopeJson.Summarize(envelope);
        if (image is not null) text += " (screenshot attached)";
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        if (image is not null) content.Add(image);
        return new JsonObject
        {
            ["content"] = content,
            ["structuredContent"] = structured,
            ["isError"] = !envelope.Ok,
        };
    }

    /// <summary>Lifts screenshot bytes out of the structured envelope into a dedicated image block,
    /// and drops them from the JSON so the payload is not carried twice.</summary>
    private static JsonObject? TakeInlineImage(JsonObject structured)
    {
        if (structured["data"] is not JsonObject data) return null;
        var format = data["format"]?.GetValue<string>();
        if (format is not ("png" or "jpg" or "jpeg")) return null;
        if (data["data"] is not JsonValue value || !value.TryGetValue<string>(out var encoded)) return null;
        data.Remove("data");
        return new JsonObject
        {
            ["type"] = "image",
            ["data"] = encoded,
            ["mimeType"] = format == "png" ? "image/png" : "image/jpeg",
        };
    }

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message } },
        ["isError"] = true,
    };

    private static JsonObject RpcResult(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject RpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static void Reply(TextWriter stdout, JsonObject response) =>
        stdout.WriteLine(response.ToJsonString(Options));
}

/// <summary>Derives MCP tool JSON schemas from the command registry — the CLI stays the single source of truth.</summary>
public static class Catalog
{
    public static JsonArray Build()
    {
        var tools = new JsonArray();
        foreach (var spec in Registry.Commands)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var f in spec.Flags)
            {
                var schema = new JsonObject
                {
                    ["description"] = f.Description,
                    ["type"] = f.Kind switch
                    {
                        FlagKind.Flag => "boolean",
                        FlagKind.Integer => "integer",
                        _ => "string",
                    },
                };
                properties[f.Name] = schema;
                if (f.Required) required.Add(JsonValue.Create(f.Name));
            }
            tools.Add(new JsonObject
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                },
            });
        }
        return tools;
    }
}
