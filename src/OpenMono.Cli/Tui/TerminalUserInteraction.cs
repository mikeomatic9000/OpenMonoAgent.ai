using OpenMono.Acp;
using OpenMono.Permissions;
using OpenMono.Rendering;

namespace OpenMono.Tui;

/// <summary>
/// TUI-mode implementation of <see cref="IAcpUserInteraction"/>. Wraps the existing
/// <see cref="IInputReader"/> prompts (TerminalRenderer / AnsiInputReader) so the
/// interactive terminal user answers permission and AskUser prompts exactly as before.
///
/// ConversationLoop falls back to this when no ACP <see cref="IAcpUserInteraction"/>
/// is supplied, so the existing TUI flow keeps working unchanged.
/// </summary>
public sealed class TerminalUserInteraction : IAcpUserInteraction
{
    private readonly IInputReader _input;

    public TerminalUserInteraction(IInputReader input)
    {
        _input = input;
    }

    public async Task<bool> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct)
    {
        var response = await _input.AskPermissionAsync(toolName, summary, ct);
        return response is PermissionResponse.Allow or PermissionResponse.AllowAll;
    }

    public async Task<string?> RequestUserInputAsync(string question, CancellationToken ct)
    {
        var answer = await _input.AskUserAsync(question, ct);
        return answer;
    }
}
