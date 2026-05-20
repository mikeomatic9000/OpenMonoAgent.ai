namespace OpenMono.Acp;

/// <summary>
/// Routes the two interactive prompts the agent's tool dispatcher and AskUser tool need
/// (permission approval, free-form text input) through either the connected ACP client
/// or the local terminal user. The ACP implementation lives in
/// <see cref="AcpUserInteractionForwarder"/>; the TUI implementation lives in
/// <c>OpenMono.Tui.TerminalUserInteraction</c>.
/// </summary>
public interface IAcpUserInteraction
{
    /// <summary>
    /// Ask the connected client (or terminal user) to approve a tool invocation.
    /// Returns <c>true</c> on Allow, <c>false</c> on Deny / cancel / disconnect.
    /// </summary>
    Task<bool> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct);

    /// <summary>
    /// Ask the connected client (or terminal user) for free-form text input
    /// (the AskUser tool surface). Returns <c>null</c> if the user cancels.
    /// </summary>
    Task<string?> RequestUserInputAsync(string question, CancellationToken ct);
}
