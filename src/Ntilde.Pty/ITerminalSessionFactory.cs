using System;
using System.Collections.Generic;

namespace Ntilde.Pty
{
    /// <summary>
    /// Everything a host knows about a session it wants opened, in one value.
    /// </summary>
    /// <remarks>
    /// A record rather than a parameter list because the point of the seam is that the host stops
    /// choosing an implementation: today's caller picks between <c>RustPtySession</c> and the SSH
    /// factory inline, and a multiplexer-backed pane must be able to route the same request to a
    /// daemon instead. A single value also means adding a field later does not re-break every
    /// implementation's signature.
    /// </remarks>
    /// <param name="Command">
    /// The executable to run, already split from any inline arguments and already merged with the
    /// shell-integration launch plan. Ignored when <paramref name="Ssh"/> is set.
    /// </param>
    /// <param name="Arguments">Arguments as a single command-line string; may be empty.</param>
    /// <param name="StartingDirectory">Working directory, or empty for the host's default.</param>
    /// <param name="Cols">Initial column count.</param>
    /// <param name="Rows">Initial row count.</param>
    /// <param name="EnvironmentOverrides">
    /// Extra environment for the child (the shell-integration bootstrap's variables), or null.
    /// </param>
    /// <param name="SkipPowerShellPostLaunchInit">
    /// Suppresses the PowerShell post-launch init injection, because the shell-integration launch
    /// plan already did that work.
    /// </param>
    /// <param name="Ssh">
    /// Set for an SSH pane, null for a local one. When set, <see cref="Command"/>,
    /// <see cref="Arguments"/>, <see cref="StartingDirectory"/>, <see cref="EnvironmentOverrides"/>
    /// and <see cref="SkipPowerShellPostLaunchInit"/> are ignored; <see cref="Cols"/> and
    /// <see cref="Rows"/> still apply.
    /// </param>
    public sealed record TerminalSessionRequest(
        string Command,
        string Arguments,
        string StartingDirectory,
        int Cols,
        int Rows,
        IReadOnlyDictionary<string, string>? EnvironmentOverrides,
        bool SkipPowerShellPostLaunchInit,
        SshSessionDescriptor? Ssh);

    /// <summary>
    /// The SSH half of a <see cref="TerminalSessionRequest"/>, kept deliberately opaque.
    /// </summary>
    /// <remarks>
    /// SSH profiles, diagnostics levels and interaction handlers all live in
    /// <c>Ntilde.Platform</c>, which references this assembly - so naming those types here would
    /// invert the dependency. The descriptor therefore carries a profile id, an integral
    /// diagnostics level and an untyped handler, and the implementation in the layer that owns
    /// those types does the resolution. Phase 0 keeps that resolution exactly where it is today
    /// (in App); only the call site moves.
    /// </remarks>
    /// <param name="ProfileId">Identifies the stored SSH profile to connect with.</param>
    /// <param name="DiagnosticsLevel">
    /// Integral value of <c>Ntilde.Platform.Ssh.Launch.SshDiagnosticsLevel</c>
    /// (0 = None, 1 = Verbose, 2 = VeryVerbose).
    /// </param>
    /// <param name="InteractionHandler">
    /// The host's <c>Ntilde.Platform.Ssh.Interactions.ISshInteractionHandler</c>, or null. Untyped
    /// for the layering reason above; implementations cast.
    /// </param>
    /// <param name="NativeSshEnabled">Whether the native SSH backend is permitted.</param>
    public sealed record SshSessionDescriptor(
        Guid ProfileId,
        int DiagnosticsLevel,
        object? InteractionHandler,
        bool NativeSshEnabled);

    /// <summary>
    /// Opens terminal sessions. One method, because the whole point is that the caller expresses
    /// what it wants and the implementation decides what backs it.
    /// </summary>
    public interface ITerminalSessionFactory
    {
        /// <summary>
        /// Opens a session for <paramref name="request"/>, or throws describing why it could not.
        /// Never returns a fallback session of a different kind than the one asked for - a
        /// request that named an SSH profile and quietly produced a local shell is worse than a
        /// visible failure.
        /// </summary>
        ITerminalSession Create(TerminalSessionRequest request);
    }
}
