using System.Text.Json;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Acp;

/// <summary>
/// Drives one ACP turn against a ConversationLoop. Translates SSE events back to the connected
/// client through SseWriter (acting as IAcpEventSink), and orchestrates pause/resume around
/// PendingToolResultsException so the client can post tool_results back.
/// </summary>
public sealed class AcpTurnRunner : IAcpEventSink
{
    private readonly AcpSession _acpSession;
    private readonly SseWriter _writer;
    private readonly AcpSessionStore _store;
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _toolRegistry;
    private readonly AppConfig _config;
    private readonly AcpServerSettings _settings;

    public AcpTurnRunner(
        AcpSession acpSession,
        SseWriter writer,
        AcpSessionStore store,
        ILlmClient llm,
        ToolRegistry toolRegistry,
        AppConfig config,
        AcpServerSettings settings)
    {
        _acpSession = acpSession;
        _writer = writer;
        _store = store;
        _llm = llm;
        _toolRegistry = toolRegistry;
        _config = config;
        _settings = settings;
    }

    public async Task RunUserMessageAsync(string userText, CancellationToken ct)
    {
        _acpSession.Messages.Add(new Message { Role = MessageRole.User, Content = userText });
        await DriveLoopAsync(initialMessage: userText, isResume: false, ct);
    }

    public async Task ResumeWithToolResultsAsync(JsonElement results, CancellationToken ct)
    {
        var lastAssistant = _acpSession.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        var toolNames = lastAssistant?.ToolCalls?.ToDictionary(c => c.Id, c => c.Name) ?? new Dictionary<string, string>();

        foreach (var (id, result) in AcpToolBridge.ParseToolResults(results))
        {
            if (!_acpSession.TryResolvePendingCall(id, result))
                throw new InvalidOperationException($"tool_result for unknown or already-resolved call id: {id}");

            // Append the Tool message that the resumed loop will see when it calls the LLM again.
            _acpSession.Messages.Add(new Message
            {
                Role = MessageRole.Tool,
                ToolCallId = id,
                ToolName = toolNames.GetValueOrDefault(id, "unknown"),
                Content = result.ModelPreview,
            });
        }

        await DriveLoopAsync(initialMessage: null, isResume: true, ct);
    }

    private async Task DriveLoopAsync(string? initialMessage, bool isResume, CancellationToken ct)
    {
        var sessionState = BuildSessionState();
        var executor = new AcpToolExecutor(_acpSession, _writer, _settings.PendingToolResultsTimeout);
        var schema = AcpToolSchema.ForSession(_toolRegistry, _acpSession.ClientTools);
        var silentOutput = new SilentRenderer();

        using var loop = new ConversationLoop(
            _llm, _toolRegistry,
            new PermissionEngine(_config, silentOutput, silentOutput),
            silentOutput, silentOutput, silentOutput,
            _config, sessionState,
            sink: this, executor: executor, toolSubset: schema);

        try
        {
            if (isResume)
                await loop.ContinueTurnAsync(ct);
            else
                await loop.RunTurnAsync(initialMessage ?? "", ct);

            SyncBackToAcpSession(sessionState, incrementTurn: !isResume);
            await _writer.WriteEventAsync("done", new { });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SyncBackToAcpSession(sessionState, incrementTurn: !isResume);
        }
        catch (PendingToolResultsException)
        {
            SyncBackToAcpSession(sessionState, incrementTurn: !isResume);
            await _writer.WriteEventAsync("awaiting_tool_results", new { });
        }
        catch (Exception e)
        {
            SyncBackToAcpSession(sessionState, incrementTurn: !isResume);
            await _writer.WriteEventAsync("error", new { message = e.Message });
        }
    }

    private SessionState BuildSessionState()
    {
        var ss = new SessionState();
        foreach (var m in _acpSession.Messages) ss.AddMessage(m);
        ss.TurnCount = _acpSession.TurnCount;
        ss.Meta.PlanMode = _acpSession.PlanMode;
        ss.Todos.Clear();
        foreach (var t in _acpSession.Todos) ss.Todos.Add(t);
        ss.Meta.TokenTracker ??= new TokenTracker();
        return ss;
    }

    private void SyncBackToAcpSession(SessionState ss, bool incrementTurn)
    {
        _acpSession.Messages.Clear();
        _acpSession.Messages.AddRange(ss.Messages);
        if (incrementTurn) _acpSession.TurnCount = ss.TurnCount;
        _acpSession.PlanMode = ss.Meta.PlanMode;
        _acpSession.Todos.Clear();
        foreach (var t in ss.Todos) _acpSession.Todos.Add(t);
    }

    // ── IAcpEventSink ───────────────────────────────────────────────────────────

    public Task OnTextDeltaAsync(string content)
        => _writer.WriteEventAsync("text_delta", new { content });

    public Task OnThinkingDeltaAsync(string content)
        => _writer.WriteEventAsync("thinking_delta", new { content });

    public Task OnCompactionAsync(int messagesCompressed, double durationSeconds, int checkpointIndex)
        => _writer.WriteEventAsync("compaction", new
        {
            messages_compressed = messagesCompressed,
            duration_seconds = durationSeconds,
            checkpoint_index = checkpointIndex,
        });

    public Task OnUsageAsync(int inputTokens, int outputTokens, int totalTokens)
        => _writer.WriteEventAsync("usage", new
        {
            input_tokens = inputTokens,
            output_tokens = outputTokens,
            total_tokens = totalTokens,
        });

    public Task OnToolResultPreviewAsync(string callId, string preview, string? artifactId)
        => _writer.WriteEventAsync("tool_result_preview", new
        {
            id = callId,
            preview,
            artifact_id = artifactId,
        });

    // ── Silent renderer/input for ACP mode ──────────────────────────────────────

    /// <summary>
    /// All-no-op implementation of the TUI renderer/input interfaces. ACP turns surface
    /// every visible event through SseWriter via IAcpEventSink, so any TUI-side writes
    /// in ConversationLoop become invisible. Permission prompts auto-deny — ACP-declared
    /// tools should already be filtered through AcpToolSchema, and any remaining prompt
    /// would block forever otherwise.
    /// </summary>
    private sealed class SilentRenderer : IRenderer, IOutputSink, IInputReader, ILiveFeedback
    {
        public bool Verbose { get; set; }

        public void StartAssistantResponse() { }
        public void StreamText(string text) { }
        public void EndAssistantResponse(int tokens = 0) { }
        public void AppendThinking(string text) { }
        public void CollapseThinking(int charCount) { }
        public void ShowWaitingIndicator() { }
        public void ClearWaitingIndicator() { }
        public void WriteWelcome(string model, string endpoint) { }
        public void WriteMarkdown(string markdown) { }
        public void WriteDebug(string message) { }
        public void WriteToolStart(string toolName, string args) { }
        public void WriteToolSuccess(string toolName) { }
        public void WriteToolError(string toolName, string error) { }
        public void WriteToolDenied(string toolName, string reason) { }
        public void WriteToolDiff(string diff) { }
        public void WriteWarning(string message) { }
        public void WriteError(string message) { }
        public void WriteInfo(string message) { }
        public void WriteTodos(IReadOnlyList<TodoItem> todos) { }
        public void ClearConversation() { }

        public void EnableCommandSuggestions(CommandRegistry registry) { }
        public string ReadInput() => string.Empty;
        public string? ShowCommandPicker(CommandRegistry registry) => null;
        public Task<string> AskUserAsync(string question, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
            => Task.FromResult(PermissionResponse.Deny);

        public void BeginTurn() { }
        public void EndTurn() { }
    }
}
