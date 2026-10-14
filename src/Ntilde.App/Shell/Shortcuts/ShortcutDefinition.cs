using System;

namespace Ntilde.Shell.Shortcuts;

public sealed record ShortcutDefinition
{
    public ShortcutDefinition(string commandId, ShortcutScope scope, string defaultBinding)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        ArgumentNullException.ThrowIfNull(defaultBinding);
        CommandId = commandId;
        Scope = scope;
        DefaultBinding = defaultBinding.Trim();
    }

    public string CommandId { get; }

    public ShortcutScope Scope { get; }

    public string DefaultBinding { get; }

    /// <summary>No default chord: reachable from the palette until the user binds one (Phase 3 spec §7.6).</summary>
    public bool IsUnbound => DefaultBinding.Length == 0;
}
