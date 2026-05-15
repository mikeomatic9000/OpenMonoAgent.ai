using OpenMono.Session;

namespace OpenMono.Tools;

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(ToolCall call, ITool? tool, ToolContext ctx, CancellationToken ct);

    /// <summary>
    /// True when ExecuteAsync emits the tool call to a remote client and returns a Task that only
    /// completes once the client posts results back. ConversationLoop uses this to throw
    /// PendingToolResultsException after firing a round of tool calls, so AcpTurnRunner can emit
    /// the awaiting_tool_results sentinel and close the SSE stream.
    /// </summary>
    bool PausesAfterEmit => false;
}
