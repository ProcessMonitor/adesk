using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AdeskPro;

/// <summary>The one JSON envelope every command prints: ok with data, or ok=false with error.</summary>
public sealed class Envelope
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("data")] public JsonObject? Data { get; init; }
    [JsonPropertyName("error")] public ErrorBody? Error { get; init; }

    public static Envelope Good(JsonObject data) => new() { Ok = true, Data = data };

    public static Envelope Fail(string code, string message, string? suggestion = null,
        JsonObject? details = null, string delivery = "not_delivered", string retry = "safe") =>
        new() { Ok = false, Error = new ErrorBody
        {
            Code = code,
            Message = message,
            Suggestion = suggestion,
            Details = details,
            Disposition = new Disposition(delivery, retry),
        } };
}

public sealed class ErrorBody
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("suggestion")] public string? Suggestion { get; init; }
    [JsonPropertyName("details")] public JsonObject? Details { get; init; }
    [JsonPropertyName("disposition")] public Disposition Disposition { get; init; } = new("not_delivered", "safe");
}

public sealed class Disposition
{
    [JsonPropertyName("delivery")] public string Delivery { get; init; }
    [JsonPropertyName("retry")] public string Retry { get; init; }

    public Disposition(string delivery, string retry) { Delivery = delivery; Retry = retry; }
}

public static class EnvelopeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(Envelope envelope) => JsonSerializer.Serialize(envelope, Options);

    /// <summary>Short one-line human summary for MCP text content — never the full envelope again.</summary>
    public static string Summarize(Envelope envelope)
    {
        if (envelope.Error is not null)
            return $"error {envelope.Error.Code}: {envelope.Error.Message}";
        var data = envelope.Data;
        if (data is null) return "ok";
        if (data.TryGetPropertyValue("snapshotId", out var sid) && sid is not null
            && data.TryGetPropertyValue("refCount", out var rc) && rc is not null)
            return $"{data["command"]} {sid}: {rc} refs, complete={data["complete"]}";
        if (data.TryGetPropertyValue("command", out var cmd) && cmd is not null)
        {
            var count = data.TryGetPropertyValue("count", out var c) && c is not null ? $" ({c})" : "";
            return $"{cmd}{count}: ok";
        }
        return "ok";
    }
}

public static class ErrorCodes
{
    public const string InvalidArgs = "INVALID_ARGS";
    public const string AppNotFound = "APP_NOT_FOUND";
    public const string AmbiguousWindow = "AMBIGUOUS_WINDOW";
    public const string StaleRef = "STALE_REF";
    public const string AmbiguousTarget = "AMBIGUOUS_TARGET";
    public const string NoPattern = "NO_PATTERN";
    public const string Timeout = "TIMEOUT";
    public const string NotSupported = "NOT_SUPPORTED";
    public const string Internal = "INTERNAL";
}
