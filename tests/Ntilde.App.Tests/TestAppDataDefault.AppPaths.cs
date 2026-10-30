namespace Ntilde.Tests
{
    internal static partial class TestAppDataDefault
    {
        /// <summary>
        /// Pointing the variable at an empty directory does not keep the root empty:
        /// <see cref="Ntilde.Shell.AppPaths.EnsureInitialized"/> copies legacy data (a roaming <c>last_session.json</c>,
        /// the themes beside the test binaries) into whichever root is current the first time anything touches
        /// <see cref="Ntilde.Shell.AppPaths"/>. This runs it now and sweeps the root as a
        /// <see cref="Core.TestAppDataRoot"/> scope does, so a run starts from defaults rather than the developer's
        /// settings and saved session.
        /// </summary>
        static partial void SweepAppPathsMigration(string root)
        {
            Core.TestAppDataRoot.Isolate(root);
            SweptAppPathsMigration = true;
        }
    }
}
