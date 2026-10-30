using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace Ntilde.Tests
{
    /// <summary>
    /// Starts the test process on a scratch app-data root (#317): <c>NTILDE_APPDATA_ROOT</c> points at a fresh directory
    /// before any test runs, so no test reads or writes the machine's real one, where the developer's own Ntilde keeps
    /// its settings, sessions and command-assist bootstraps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set unconditionally: a developer's own override (a test profile, say) is no place for the suite to write either.
    /// A <see cref="Core.TestAppDataRoot"/> scope still gives a class an empty root of its own, and restores this one.
    /// </para>
    /// <para>
    /// The root starts empty, so a run sees defaults rather than the developer's settings and saved session. Pointing
    /// the variable at an empty directory is not enough for that: <see cref="Ntilde.Shell.AppPaths.EnsureInitialized"/> copies
    /// legacy data (a roaming <c>last_session.json</c>, the themes beside the test binaries) into whichever root is
    /// current the first time anything touches <see cref="Ntilde.Shell.AppPaths"/>, so this runs it here and sweeps the root
    /// as a <see cref="Core.TestAppDataRoot"/> scope does.
    /// </para>
    /// </remarks>
    internal static class TestAppDataDefault
    {
        /// <summary>The files the root held once initialized, before any test ran: none, whatever the machine holds.</summary>
        internal static IReadOnlyList<string> FilesAtStart { get; private set; } = [];

        [ModuleInitializer]
        internal static void Init()
        {
            string root = Path.Combine(Path.GetTempPath(), $"ntilde_test_process_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);
            Core.TestAppDataRoot.Isolate(root);

            FilesAtStart = Directory.GetFiles(root, "*", SearchOption.AllDirectories);

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best effort: a child process may hold a file */ }
            };
        }
    }
}
