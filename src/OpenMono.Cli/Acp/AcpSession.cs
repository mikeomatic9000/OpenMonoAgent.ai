using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using OpenMono.Session;

namespace OpenMono.Acp;

/// <summary>
/// Per-client ACP session. Persisted fields capture only the state the agent needs to
/// resume a conversation after a container restart; the pause-resume primitives
/// (TurnLock, _pending) are runtime-only and rebuilt on every load.
/// </summary>
public sealed class AcpSession
{
    public required string Id { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime LastActivityAt { get; set; }
    public required string Model { get; init; }
    public int TurnCount { get; set; }
    public bool PlanMode { get; set; }
    public List<TodoItem> Todos { get; init; } = new();
    public List<Message> Messages { get; init; } = new();

    /// <summary>Serializes the single turn-in-flight invariant. One /turn per session at a time.</summary>
    [JsonIgnore]
    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    /// <summary>
    /// Pause-resume registry. Keyed by pause id (e.g. <c>perm_abc</c> / <c>ask_xyz</c>).
    /// Runtime-only: a paused conversation cannot survive a container restart because the
    /// awaiting TaskCompletionSource lives only in this process.
    /// </summary>
    [JsonIgnore]
    private readonly ConcurrentDictionary<string, PendingPause> _pending = new();

    public TaskCompletionSource<AcpPauseResponse> RegisterPause(string id, PendingResponseKind kind)
    {
        var tcs = new TaskCompletionSource<AcpPauseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, new PendingPause(kind, tcs)))
            throw new InvalidOperationException($"Duplicate pause id: {id}");
        return tcs;
    }

    public bool TryResolvePause(string id, AcpPauseResponse response)
        => _pending.TryRemove(id, out var pp) && pp.Tcs.TrySetResult(response);

    [JsonIgnore]
    public IReadOnlyCollection<string> PendingIds => _pending.Keys.ToArray();

    public void CancelAllPending()
    {
        foreach (var kv in _pending) kv.Value.Tcs.TrySetCanceled();
        _pending.Clear();
    }

    private sealed record PendingPause(PendingResponseKind Kind, TaskCompletionSource<AcpPauseResponse> Tcs);
}

/// <summary>
/// Client response to a paused turn. The base type lives in OpenMono.Acp to avoid colliding
/// with <see cref="OpenMono.Permissions.PermissionResponse"/> (an enum) — every concrete
/// subtype here is prefixed with <c>Acp</c> for the same reason.
/// </summary>
public abstract record AcpPauseResponse;

public sealed record AcpPermissionResponse(bool Allow) : AcpPauseResponse;

public sealed record AcpUserInputResponse(string Value) : AcpPauseResponse;

public sealed record AcpCancelledResponse() : AcpPauseResponse;
