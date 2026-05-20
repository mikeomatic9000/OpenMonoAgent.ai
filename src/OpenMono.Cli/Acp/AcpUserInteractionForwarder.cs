namespace OpenMono.Acp;

/// <summary>
/// ACP-mode implementation of <see cref="IAcpUserInteraction"/>. Each request:
/// 1. Generates a pause id (perm_… / ask_…).
/// 2. Registers a pause on the session so the next <c>/turn</c> POST can resolve it.
/// 3. Emits the matching SSE event to the connected client.
/// 4. Throws <see cref="PendingUserResponseException"/> so <c>AcpTurnRunner</c> unwinds
///    the loop and closes the SSE stream. The C# call stack cannot suspend across an
///    HTTP request boundary, so the loop is re-entered fresh on the next /turn POST —
///    AcpTurnRunner appends the resolved decision to the conversation history before
///    re-prompting, and a fresh invocation of this forwarder inside the resumed loop
///    is what actually returns the user's answer to the LLM.
/// </summary>
public sealed class AcpUserInteractionForwarder : IAcpUserInteraction
{
    private readonly AcpSession _session;
    private readonly SseWriter _writer;
    private readonly TimeSpan _timeout;

    public AcpUserInteractionForwarder(AcpSession session, SseWriter writer, TimeSpan timeout)
    {
        _session = session;
        _writer = writer;
        _timeout = timeout;
    }

    public async Task<bool> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct)
    {
        var id = "perm_" + Guid.NewGuid().ToString("N")[..12];
        _session.RegisterPause(id, PendingResponseKind.Permission);

        await _writer.WriteEventAsync("permission_request", new
        {
            id,
            tool = toolName,
            summary,
            dangerous,
        });

        throw new PendingUserResponseException(id, PendingResponseKind.Permission);
    }

    public async Task<string?> RequestUserInputAsync(string question, CancellationToken ct)
    {
        var id = "ask_" + Guid.NewGuid().ToString("N")[..12];
        _session.RegisterPause(id, PendingResponseKind.UserInput);

        await _writer.WriteEventAsync("user_input_request", new
        {
            id,
            question,
        });

        throw new PendingUserResponseException(id, PendingResponseKind.UserInput);
    }
}
