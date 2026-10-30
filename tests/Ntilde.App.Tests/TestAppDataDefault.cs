using System;
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
    /// The root starts empty, as a CI runner's real one does, so a local run sees what CI sees rather than the
    /// developer's settings and saved session.
    /// </para>
    /// </remarks>
    internal static class TestAppDataDefault
    {
        [ModuleInitializer]
        internal static void Init()
        {
            string root = Path.Combine(Path.GetTempPath(), $"ntilde_test_process_{Environment.ProcessId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("NTILDE_APPDATA_ROOT", root);

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best effort: a child process may hold a file */ }
            };
        }
    }
}
