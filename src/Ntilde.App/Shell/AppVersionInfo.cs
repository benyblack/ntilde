using System.Reflection;
using Ntilde.Shell.Mux;

namespace Ntilde.Shell;

/// <summary>
/// The running app's version, read once from the entry assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> - the version the build pins in
/// <c>Directory.Build.props</c> and release builds override.
/// </summary>
internal static class AppVersionInfo
{
    /// <summary>
    /// The informational version as built, which may carry SemVer build metadata (<c>0.11.0+3f2c1ab</c>);
    /// the assembly version when there is no such attribute; <see langword="null"/> when neither is known.
    /// The About window shows this.
    /// </summary>
    public static string? InformationalVersion { get; } = Of(Assembly.GetEntryAssembly());

    /// <summary>
    /// <see cref="InformationalVersion"/> without build metadata (<c>0.11.0</c>): the version releases are
    /// tagged with (<c>v0.11.0</c>), so the one the remote installer downloads <c>ntilde-mux</c> for
    /// (Phase 4 spec §9 step 2). Empty when unknown.
    /// </summary>
    public static string Version { get; } = WithoutBuildMetadata(InformationalVersion);

    /// <summary><paramref name="assembly"/>'s informational version, else its assembly version.</summary>
    internal static string? Of(Assembly? assembly) =>
        assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly?.GetName().Version?.ToString();

    /// <summary>Drops a <c>+…</c> build-metadata suffix, which never names a release.</summary>
    internal static string WithoutBuildMetadata(string? version) => RemoteMuxStatusText.StripBuildMetadata(version);
}
