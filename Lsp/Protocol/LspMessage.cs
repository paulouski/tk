using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk.Lsp.Protocol;

/// <summary>
/// Incoming message from the LSP server.
/// </summary>
public record LspIncoming(
    string? jsonrpc,
    int? id,
    long? longId,
    string? method,
    JsonElement? result,
    JsonElement? error,
    [property: JsonPropertyName("params")] JsonElement? @params);

/// <summary>
/// Outgoing request to the LSP server.
/// </summary>
public record LspRequest(string jsonrpc, int id, string method, object? @params);

/// <summary>
/// Outgoing notification to the LSP server (no id, no response expected).
/// </summary>
public record LspNotification(string jsonrpc, string method, object? @params);

/// <summary>
/// Outgoing response to a server request. A JSON-RPC response MUST carry either a
/// 'result' or an 'error'; a null result must still be emitted as `"result":null`,
/// otherwise (with WhenWritingNull) the message has neither field and the peer rejects
/// it as "Expected a request, result, or error message".
/// </summary>
public record LspResponse(
    string jsonrpc,
    int id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? result,
    object? error = null);

/// <summary>
/// Helpers for parsing LSP messages.
/// </summary>
public static class LspMessage
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses a JSON string into an <see cref="LspIncoming"/>.
    /// </summary>
    public static LspIncoming Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? jsonrpc = root.TryGetProperty("jsonrpc", out var jv) ? jv.GetString() : null;
        string? method = root.TryGetProperty("method", out var mv) ? mv.GetString() : null;

        int? id = null;
        long? longId = null;
        if (root.TryGetProperty("id", out var idv))
        {
            if (idv.ValueKind == JsonValueKind.Number)
            {
                if (idv.TryGetInt32(out var i32))
                    id = i32;
                else if (idv.TryGetInt64(out var i64))
                    longId = i64;
            }
        }

        JsonElement? result = root.TryGetProperty("result", out var rv) ? rv.Clone() : null;
        JsonElement? error = root.TryGetProperty("error", out var ev) ? ev.Clone() : null;
        JsonElement? @params = root.TryGetProperty("params", out var pv) ? pv.Clone() : null;

        return new LspIncoming(jsonrpc, id, longId, method, result, error, @params);
    }

    /// <summary>
    /// Serializes an object to JSON using the standard LSP camelCase options.
    /// </summary>
    public static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, Options);
}
