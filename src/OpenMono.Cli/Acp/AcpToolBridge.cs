using System.Text.Json;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Acp;

public static class AcpToolBridge
{
    public static object ToToolCallPayload(ToolCall call)
        => new
        {
            id = call.Id,
            name = call.Name,
            input = ParseArgumentsAsJson(call.Arguments),
        };

    public static ToolResult FromToolResult(string id, string result)
        => ToolResult.Success(result);

    /// <summary>
    /// Parses the `tool_results` array from a /turn POST body, yielding (call-id, ToolResult) pairs
    /// so AcpTurnRunner.ResumeWithToolResultsAsync can resolve pending TCS entries on the session.
    /// </summary>
    public static IEnumerable<(string Id, ToolResult Result)> ParseToolResults(JsonElement results)
    {
        if (results.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("tool_results must be a JSON array");

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("each tool_results entry must be an object");
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException("tool_results entry missing 'id'");
            var resultText = item.TryGetProperty("result", out var rEl) ? rEl.GetString() : null;
            yield return (id, FromToolResult(id, resultText ?? ""));
        }
    }

    private static JsonElement ParseArgumentsAsJson(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return JsonDocument.Parse("{}").RootElement.Clone();
        try
        {
            return JsonDocument.Parse(arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}
