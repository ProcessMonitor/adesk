using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Adesk.Ui;

namespace Adesk.Mcp;

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

    public static void Run(string sessionName)
    {
        var session = new Session
        {
            Name = sessionName,
            InMemory = true,
            Refs = new RefStore(null),
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
                ["name"] = "adesk",
                ["version"] = Version.Value,
                ["title"] = "adesk — desktop use via accessibility trees",
            },
            ["instructions"] =
                "adesk reads Windows accessibility trees and operates desktop apps. Read the 'snapshot' " +
                "tool first: take a snapshot of the app, act on refs from it (@sid:eN), then re-snapshot to verify. " +
                "Retry an action only when its disposition.retry is safe.",
        };
    }

    private static JsonObject CallTool(JsonObject request, Session session)
    {
        var name = request["params"]?["name"]?.GetValue<string>() ?? "";
        var spec = Registry.Find(name);
        if (spec is null)
            return ToolError($"unknown tool '{name}'");
        var arguments = request["params"]?["arguments"] as JsonObject ?? new JsonObject();
        var argv = new List<string>();
        foreach (var (key, value) in arguments)
        {
            var flag = spec.Flags.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
            if (flag is null) return ToolError($"unknown argument '{key}' for '{name}'");
            if (flag.Kind == FlagKind.Flag)
            {
                if (value is JsonValue v && v.TryGetValue<bool>(out var b) && b) argv.Add($"--{key}");
                continue;
            }
            var text = value is JsonValue jv && jv.TryGetValue<string>(out var s)
                ? s
                : value?.ToJsonString(Options) ?? "";
            argv.Add($"--{key}");
            argv.Add(text);
        }

        var (args, failure) = Registry.Parse(spec, argv.ToArray());
        if (failure is not null) return EnvelopeResult(failure);
        var envelope = spec!.Run(args!, session);
        return EnvelopeResult(envelope);
    }

    /// <summary>Structured content carries the envelope; text content is a one-line summary, never the envelope again.</summary>
    private static JsonObject EnvelopeResult(Envelope envelope)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = EnvelopeJson.Summarize(envelope) },
            },
            ["structuredContent"] = JsonSerializer.SerializeToNode(envelope, EnvelopeJson.Options),
            ["isError"] = !envelope.Ok,
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
                properties[$"--{f.Name}".TrimStart('-')] = schema;
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
