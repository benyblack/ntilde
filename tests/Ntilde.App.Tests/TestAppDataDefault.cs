using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace Ntilde.Tests
{
    /// <summary>
    /// Starts the test process on a scratch app-data root (#317): <c>NTILDE_APPDATA_ROOT</c> points at a fresh directory
    /// before any test runs, so no test reads or writes the machine's real one, where the developer's own Ntilde keeps
    /// its settings, sessions, command-assist bootstraps and mux descriptors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set unconditionally: a developer's own override (a test profile, say) is no place for the suite to write either.
    /// A test that sets the variable for itself restores this value afterwards.
    /// </para>
    /// <para>
    /// Linked into each test project whose code resolves the root: App, McpServer, Platform and Mux. The projects that
    /// reference Ntilde.App also link <c>TestAppDataDefault.AppPaths.cs</c>, which sweeps what <c>AppPaths</c>' legacy
    /// migration copies into the root.
    /// </para>
    /// </remarks>
    internal static partial class TestAppDataDefault
    {
        /// <summary>The files the root held once initialized, before any test ran: none, whatever the machine holds.</summary>
        internal static IReadOnlyList<string> FilesAtStart { get; private set; } = [];

        /// <summary>
        /// True once <c>AppPaths</c>' migration ran against the root and was swept. Without the sweep linked in, the
        /// migration still runs, later, whenever a test first touches <c>AppPaths</c>, and nothing at start shows it.
        /// </summary>
        internal static bool SweptAppPathsMigration { get; private set; }

        [ModuleInitializer]
        internal static void Init()
        {
            string root = Path.Combine(Path.GetTempPath(), $"ntilde_test_process_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);
            SweepAppPathsMigration(root);

            FilesAtStart = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best effort: a child process may hold a file */ }
            };
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="realRoot"/> or inside it: what each project's guard
        /// asks of the roots its code resolves.
        /// </summary>
        internal static bool IsInside(string path, string realRoot)
        {
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(realRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }

        // Implemented where the project references Ntilde.App; elsewhere the call compiles away.
        static partial void SweepAppPathsMigration(string root);
    }
}
