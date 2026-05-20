using OpenMono.Commands;
using OpenMono.Permissions;
using OpenMono.Rendering;

namespace OpenMono.Acp;

/// <summary>
/// Adapts <see cref="IAcpUserInteraction"/> back into the <see cref="IInputReader"/>
/// surface that <c>PermissionEngine</c> and the AskUser tool flow consume.
///
/// This is the one-way bridge that lets the existing TUI-shaped code paths
/// (PermissionEngine.CheckAsync internally calls _input.AskPermissionAsync;
///  ConversationLoop's AskUser delegate calls _input.AskUserAsync) keep working
/// unchanged when ACP is active — we just substitute the IInputReader instance.
///
/// Non-interactive members (ReadInput, ShowCommandPicker, EnableCommandSuggestions)
/// are no-ops because an ACP-connected client has no terminal to read from. Anything
/// the existing TUI flow tries to read interactively from outside the two Async
/// methods would already be a bug in ACP mode; failing closed (empty / null) keeps
/// the loop moving instead of blocking on stdin that will never arrive.
/// </summary>
public sealed class AcpInputReaderAdapter : IInputReader
{
    private readonly IAcpUserInteraction _interaction;

    public AcpInputReaderAdapter(IAcpUserInteraction interaction)
    {
        _interaction = interaction;
    }

    public async Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct)
    {
        var dangerous = LooksDestructive(toolName, summary);
        var allow = await _interaction.RequestPermissionAsync(toolName, summary, dangerous, ct);
        return allow ? PermissionResponse.Allow : PermissionResponse.Deny;
    }

    public async Task<string> AskUserAsync(string question, CancellationToken ct)
    {
        var value = await _interaction.RequestUserInputAsync(question, ct);
        return value ?? string.Empty;
    }

    // ── Non-interactive members: no terminal in ACP mode ───────────────────────

    public void EnableCommandSuggestions(CommandRegistry registry) { }
    public string ReadInput() => string.Empty;
    public string? ShowCommandPicker(CommandRegistry registry) => null;

    /// <summary>
    /// Heuristic for marking a permission prompt "dangerous" in the SSE event so the
    /// VS Code modal can surface the warning copy. Conservative: matches anything
    /// the existing PermissionEngine rules already flag as needing approval, plus
    /// a handful of well-known destructive shell idioms.
    /// </summary>
    private static bool LooksDestructive(string toolName, string summary)
    {
        if (string.Equals(toolName, "Bash", StringComparison.Ordinal))
        {
            var lower = summary.ToLowerInvariant();
            if (lower.Contains("rm -rf") || lower.Contains("rm -fr")) return true;
            if (lower.Contains("git reset --hard")) return true;
            if (lower.Contains("git push --force") || lower.Contains("git push -f")) return true;
            if (lower.Contains("docker volume rm") || lower.Contains("docker system prune")) return true;
            if (lower.Contains("mkfs") || lower.Contains("dd if=")) return true;
        }
        if (string.Equals(toolName, "FileWrite", StringComparison.Ordinal)) return true;
        if (string.Equals(toolName, "ApplyPatch", StringComparison.Ordinal)) return true;
        return false;
    }
}
