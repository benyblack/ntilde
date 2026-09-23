using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Ntilde.VT.Links;

namespace Ntilde.Shell
{
    /// <summary>
    /// The side effects of activating a terminal link: the platform, this machine's names, two
    /// filesystem probes and a process launch. Behind an interface so
    /// <see cref="TerminalLinkOpener"/>'s routing can be tested without touching the disk or
    /// launching anything.
    /// </summary>
    internal interface ILinkLaunchEnvironment
    {
        bool IsWindows { get; }

        bool IsMacOS { get; }

        /// <summary>This machine's own names, for <c>file://$HOSTNAME/path</c> links.</summary>
        IReadOnlyCollection<string> LocalHostNames { get; }

        bool FileExists(string path);

        bool DirectoryExists(string path);

        void Start(ProcessStartInfo startInfo);
    }

    /// <summary>The real <see cref="ILinkLaunchEnvironment"/>.</summary>
    internal sealed class SystemLinkLaunchEnvironment : ILinkLaunchEnvironment
    {
        public static SystemLinkLaunchEnvironment Instance { get; } = new();

        public bool IsWindows => OperatingSystem.IsWindows();

        public bool IsMacOS => OperatingSystem.IsMacOS();

        public IReadOnlyCollection<string> LocalHostNames => s_localHostNames.Value;

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void Start(ProcessStartInfo startInfo) => Process.Start(startInfo)?.Dispose();

        // Read once: hover asks on every pointer move over a link. Both names are local queries
        // (GetComputerName and gethostname underneath), not DNS lookups, and nothing here ever
        // resolves the host a link names. Environment.MachineName alone is not enough on Windows,
        // where it is the NetBIOS name, cut to 15 characters; the DNS host name is the full one.
        private static readonly Lazy<string[]> s_localHostNames = new(ReadLocalHostNames);

        private static string[] ReadLocalHostNames()
        {
            var names = new List<string>(2);
            TryAdd(names, static () => Environment.MachineName);
            TryAdd(names, static () => System.Net.Dns.GetHostName());
            return names.ToArray();

            static void TryAdd(List<string> names, Func<string> read)
            {
                try
                {
                    string name = read();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                catch (Exception)
                {
                    // No name means file://$HOSTNAME links stay inert, which is the safe side.
                }
            }
        }
    }

    /// <summary>
    /// Opens a terminal link on Ctrl+click (Cmd+click on macOS) and tells the hover code whether a link
    /// is clickable at all, so the two can never disagree.
    /// </summary>
    /// <remarks>
    /// <see cref="LinkSchemes.Classify"/> decides what a link may do, without touching the filesystem.
    /// This class carries that out: web and mail links go to the OS URL handler exactly as they always
    /// have, and a local <c>file:</c> path is shown in the file manager with an explicit executable
    /// and arguments, never shell-executed, so a link to a program reveals it instead of running it.
    /// </remarks>
    internal sealed class TerminalLinkOpener
    {
        public static TerminalLinkOpener Default { get; } = new(SystemLinkLaunchEnvironment.Instance);

        private readonly ILinkLaunchEnvironment _environment;

        public TerminalLinkOpener(ILinkLaunchEnvironment environment) => _environment = environment;

        /// <summary>Whether a link underlines and shows the hand cursor. Syntactic only.</summary>
        public bool IsActivatable(string? link) => Classify(link).Action != LinkAction.Reject;

        /// <summary>Activates <paramref name="link"/>; false when nothing was launched.</summary>
        public bool TryOpen(string? link)
        {
            LinkTarget target = Classify(link);
            ProcessStartInfo? startInfo = target.Action switch
            {
                LinkAction.OpenExternal => new ProcessStartInfo
                {
                    FileName = target.ExternalUri!.ToString(),
                    UseShellExecute = true
                },
                LinkAction.RevealLocalPath => BuildRevealStartInfo(target.LocalPath!),
                _ => null
            };

            if (startInfo is null) return false;

            try
            {
                _environment.Start(startInfo);
                return true;
            }
            catch
            {
                // Ignore failed launch attempts.
                return false;
            }
        }

        private LinkTarget Classify(string? link) =>
            LinkSchemes.Classify(link, _environment.IsWindows, _environment.LocalHostNames);

        /// <summary>
        /// The file-manager launch that shows <paramref name="path"/>, or null when nothing exists there.
        /// </summary>
        /// <remarks>
        /// The path has already been through <see cref="LinkSchemes.Classify"/>, so on Windows it is a
        /// drive path: the existence checks below cannot be sent to a host of the link's choosing (a
        /// mapped network drive reaches only the server the user mapped). The target path is taken
        /// apart by hand rather than through <see cref="Path"/>, whose behaviour depends on the OS
        /// running it, so each platform's route is testable from any other.
        /// </remarks>
        private ProcessStartInfo? BuildRevealStartInfo(string path)
        {
            bool isFile = _environment.FileExists(path);
            if (!isFile && !_environment.DirectoryExists(path)) return null;

            if (_environment.IsWindows)
            {
                // explorer.exe does not parse its command line the C runtime way, so ArgumentList,
                // which quotes only arguments that contain whitespace, is the wrong tool: a comma is
                // explorer's switch separator, and an unquoted "C:\a,b\x.txt" would be split there.
                // The argument is written out by hand instead, always quoted. That is only safe
                // because '"' cannot occur in a Windows path, which Classify enforces, so nothing in
                // the path can end the quoted section early. The full path to explorer.exe keeps
                // process creation from searching the current directory for it.
                return new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    Arguments = isFile ? "/select,\"" + path + "\"" : QuoteDirectoryForExplorer(path),
                    UseShellExecute = false
                };
            }

            if (_environment.IsMacOS)
            {
                // -R reveals in Finder for directories too, not only files: an application bundle is a
                // directory, and a plain `open` on one launches the application.
                return WithArguments(new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }, "-R", path);
            }

            // Linux and the BSDs: xdg-open has no "reveal", so a file opens its containing folder. A
            // directory is only ever handed to the directory handler, which is the file manager.
            // xdg-open is looked up on PATH; where it is installed varies by distribution.
            return WithArguments(
                new ProcessStartInfo("xdg-open") { UseShellExecute = false },
                isFile ? PosixParentDirectory(path) : path);
        }

        private static ProcessStartInfo WithArguments(ProcessStartInfo startInfo, params string[] arguments)
        {
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        /// <summary>
        /// A directory for explorer.exe's command line. The trailing separator is dropped so the closing
        /// quote never follows a backslash, which the C runtime convention reads as an escaped quote
        /// (explorer's own parser is undocumented, so it is not trusted to do better). A drive root
        /// keeps its separator and goes unquoted: <c>X:\</c> has nothing in it to protect.
        /// </summary>
        private static string QuoteDirectoryForExplorer(string path)
        {
            string trimmed = path.TrimEnd('\\');
            return trimmed.Length <= 2 ? trimmed + "\\" : "\"" + trimmed + "\"";
        }

        private static string PosixParentDirectory(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path.Substring(0, slash);
        }
    }
}
