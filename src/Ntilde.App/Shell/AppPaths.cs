using System;
using System.IO;

namespace Ntilde.Shell
{
    public static class AppPaths
    {
        private const string AppName = "ntilde";
        /// <summary>Data folder name before the Ntilde rebrand. Read once for migration; never written.</summary>
        private const string LegacyAppName = "NovaTerminal";
        /// <summary>Written into the new root after the one-time copy so it never runs twice.</summary>
        public const string MigrationMarkerFileName = ".migrated-from-novaterminal";
        private const string RootOverrideEnvVar = "NTILDE_APPDATA_ROOT";
        private static readonly object InitLock = new();
        private static bool _initialized;

        static AppPaths()
        {
            EnsureInitialized();
        }

        public static string RootDirectory
        {
            get
            {
                string? overrideRoot = Environment.GetEnvironmentVariable(RootOverrideEnvVar);
                if (!string.IsNullOrWhiteSpace(overrideRoot))
                {
                    return Path.GetFullPath(overrideRoot);
                }

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppName);
            }
        }

        /// <summary>Where a pre-rebrand install kept its data. Only consulted by <see cref="MigrateLegacyRoot"/>.</summary>
        public static string LegacyRootDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyAppName);

        public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");
        public static string ThemesDirectory => Path.Combine(RootDirectory, "themes");
        public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
        public static string DebugLogPath => Path.Combine(LogsDirectory, "debug.log");
        public static string StartupErrorFilePath => Path.Combine(LogsDirectory, "startup_error.txt");
        public static string ResizeDebugLogPath => Path.Combine(LogsDirectory, "resize_debug.log");
        public static string SessionsDirectory => Path.Combine(RootDirectory, "sessions");
        public static string SessionFilePath => Path.Combine(SessionsDirectory, "last_session.json");
        public static string WorkspacesDirectory => Path.Combine(RootDirectory, "workspaces");
        public static string WorkspaceTemplatesDirectory => Path.Combine(RootDirectory, "workspace_templates");
        public static string PolicyDirectory => Path.Combine(RootDirectory, "policy");
        public static string WorkspacePolicyFilePath => Path.Combine(PolicyDirectory, "workspace_policy.json");
        public static string WorkspaceAuditLogPath => Path.Combine(LogsDirectory, "workspace_audit.log");
        public static string RecordingsDirectory => Path.Combine(RootDirectory, "recordings");

        /// <summary>Automatic configuration snapshots written by <c>BackupService</c>.</summary>
        public static string BackupsDirectory => Path.Combine(RootDirectory, "backups");
        public static string CommandPaletteUsageFilePath => Path.Combine(RootDirectory, "command-palette-usage.json");
        public static string CommandAssistDirectory => Path.Combine(RootDirectory, "command-assist");

        /// <summary>Append-only JSON-Lines command history written by <c>JsonlHistoryStore</c>.</summary>
        public static string CommandHistoryFilePath => Path.Combine(CommandAssistDirectory, "history.jsonl");

        /// <summary>
        /// Pre-JSONL whole-file history. Kept only as the one-time migration source; the store
        /// renames it to <c>history.json.bak</c> once it has been converted.
        /// </summary>
        public static string LegacyCommandHistoryFilePath => Path.Combine(CommandAssistDirectory, "history.json");

        public static string CommandSnippetsFilePath => Path.Combine(CommandAssistDirectory, "snippets.json");
        public static string SshDirectory => Path.Combine(RootDirectory, "ssh");
        public static string NativeKnownHostsFilePath => Path.Combine(SshDirectory, "native_known_hosts.json");

        /// <summary>Path of the pre-#100 weakly-encrypted vault file, kept only so it can be deleted.</summary>
        public static string LegacyVaultFilePath => Path.Combine(RootDirectory, "vault.dat");

        public static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (InitLock)
            {
                if (_initialized) return;

                try
                {
                    // Skipped under the env override: tests and portable installs point at a
                    // scratch root, and copying a developer's real NovaTerminal folder into it
                    // would be a surprise. Real installs have no override set.
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootOverrideEnvVar)))
                    {
                        MigrateLegacyRoot(LegacyRootDirectory, RootDirectory);
                    }

                    Directory.CreateDirectory(RootDirectory);
                    Directory.CreateDirectory(ThemesDirectory);
                    Directory.CreateDirectory(LogsDirectory);
                    Directory.CreateDirectory(SessionsDirectory);
                    Directory.CreateDirectory(WorkspacesDirectory);
                    Directory.CreateDirectory(WorkspaceTemplatesDirectory);
                    Directory.CreateDirectory(PolicyDirectory);
                    Directory.CreateDirectory(RecordingsDirectory);
                    Directory.CreateDirectory(CommandAssistDirectory);
                    Directory.CreateDirectory(SshDirectory);
                    Directory.CreateDirectory(BackupsDirectory);

                    string legacyBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string legacyRoamingRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        AppName);

                    MigrateFileIfNeeded(Path.Combine(legacyBaseDir, "settings.json"), SettingsFilePath);
                    MigrateDirectoryIfNeeded(Path.Combine(legacyBaseDir, "themes"), ThemesDirectory);
                    MigrateFileIfNeeded(Path.Combine(legacyRoamingRoot, "debug.log"), DebugLogPath);
                    MigrateFileIfNeeded(Path.Combine(legacyRoamingRoot, "last_session.json"), SessionFilePath);
                    MigrateFileIfNeeded(Path.Combine(legacyBaseDir, "startup_error.txt"), StartupErrorFilePath);
                    MigrateDirectoryIfNeeded(Path.Combine(legacyBaseDir, "recordings"), RecordingsDirectory);
                }
                catch
                {
                    // Keep path init best-effort; callers should remain resilient if storage is unavailable.
                }

                _initialized = true;
            }
        }

        /// <returns>
        /// <c>true</c> when the destination is up to date afterwards (copied, already newer/identical,
        /// or the source does not exist); <c>false</c> when a copy was attempted and threw.
        /// </returns>
        public static bool MigrateFileIfNeeded(string sourcePath, string destinationPath)
        {
            try
            {
                if (!File.Exists(sourcePath)) return true;

                string sourceFullPath = Path.GetFullPath(sourcePath);
                string destinationFullPath = Path.GetFullPath(destinationPath);
                if (PathsEqual(sourceFullPath, destinationFullPath)) return true;

                string? destinationDirectory = Path.GetDirectoryName(destinationFullPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (File.Exists(destinationFullPath))
                {
                    DateTime sourceWrite = File.GetLastWriteTimeUtc(sourceFullPath);
                    DateTime destinationWrite = File.GetLastWriteTimeUtc(destinationFullPath);
                    if (destinationWrite >= sourceWrite) return true;
                }

                File.Copy(sourceFullPath, destinationFullPath, overwrite: true);
                return true;
            }
            catch
            {
                // Best-effort migration only.
                return false;
            }
        }

        /// <returns>
        /// <c>true</c> when every file in the directory ended up up to date; <c>false</c> when any
        /// file copy was attempted and threw, or the directory walk itself threw. Every file is still
        /// attempted, even after one fails.
        /// </returns>
        public static bool MigrateDirectoryIfNeeded(string sourceDirectory, string destinationDirectory)
        {
            try
            {
                if (!Directory.Exists(sourceDirectory)) return true;

                string sourceFullPath = Path.GetFullPath(sourceDirectory);
                string destinationFullPath = Path.GetFullPath(destinationDirectory);
                if (PathsEqual(sourceFullPath, destinationFullPath)) return true;

                Directory.CreateDirectory(destinationFullPath);

                bool allCopied = true;
                foreach (string sourceFile in Directory.GetFiles(sourceFullPath, "*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(sourceFullPath, sourceFile);
                    string destinationFile = Path.Combine(destinationFullPath, relativePath);
                    allCopied &= MigrateFileIfNeeded(sourceFile, destinationFile);
                }

                return allCopied;
            }
            catch
            {
                // Best-effort migration only.
                return false;
            }
        }

        /// <summary>
        /// One-time copy of a pre-rebrand data folder into the new root. Copies every top-level
        /// file and every subdirectory except <c>logs</c>, newer-file-wins per file, never deletes
        /// the source, and writes <see cref="MigrationMarkerFileName"/> only once every copy
        /// succeeded, so it runs once. If any copy fails, no marker is written and the next launch
        /// retries; newer-file-wins makes that retry idempotent.
        /// </summary>
        /// <returns><c>true</c> when the marker was written (everything copied); <c>false</c> when
        /// there was nothing to do, or when any copy failed and the marker was withheld.</returns>
        public static bool MigrateLegacyRoot(string legacyRoot, string newRoot)
        {
            try
            {
                if (!Directory.Exists(legacyRoot)) return false;

                string legacyFull = Path.GetFullPath(legacyRoot);
                string newFull = Path.GetFullPath(newRoot);
                if (PathsEqual(legacyFull, newFull)) return false;

                string marker = Path.Combine(newFull, MigrationMarkerFileName);
                if (File.Exists(marker)) return false;

                Directory.CreateDirectory(newFull);

                bool allCopied = true;

                foreach (string file in Directory.GetFiles(legacyFull))
                {
                    // Skip the agent-host discovery file: it is written by the running app, and a
                    // stale copy would point the MCP server at a dead NovaTerminal process.
                    if (string.Equals(Path.GetFileName(file), Ntilde.AgentHost.Contracts.AgentHostProtocol.DiscoveryFileName, StringComparison.OrdinalIgnoreCase)) continue;

                    allCopied &= MigrateFileIfNeeded(file, Path.Combine(newFull, Path.GetFileName(file)));
                }

                foreach (string directory in Directory.GetDirectories(legacyFull))
                {
                    string name = Path.GetFileName(directory);
                    if (string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase)) continue;
                    allCopied &= MigrateDirectoryIfNeeded(directory, Path.Combine(newFull, name));
                }

                // Only mark migration complete when every copy succeeded; a partial failure must
                // not permanently suppress a retry of the files that failed.
                if (!allCopied) return false;

                File.WriteAllText(
                    marker,
                    $"Settings were copied from {legacyFull} on {DateTime.UtcNow:O}. The old folder was left in place and can be deleted.{Environment.NewLine}");
                return true;
            }
            catch
            {
                // Best-effort migration only; a failed copy must never block startup.
                return false;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return OperatingSystem.IsWindows()
                ? string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                : string.Equals(left, right, StringComparison.Ordinal);
        }
    }
}
