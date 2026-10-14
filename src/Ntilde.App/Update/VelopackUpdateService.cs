using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Ntilde.Update
{
    /// <summary>
    /// <see cref="IUpdateService"/> over Velopack, reading releases straight off this repo's
    /// GitHub releases - or, for a verification run, off the local feed <see cref="UpdateSourceOverride"/> names.
    /// </summary>
    /// <remarks>
    /// This is the only file in the app that uses Velopack's updater types. Besides it, <c>Program.Main</c> runs the
    /// <c>VelopackApp.Build().Run()</c> hook and reads the locator's app id for its startup log, and
    /// <c>VelopackHookLog</c> writes to the locator's log.
    /// </remarks>
    public sealed class VelopackUpdateService : IUpdateService
    {
        // S1075 flags hardcoded URIs because they usually belong in configuration. This one is
        // the opposite: it is the identity of the repository whose releases this build trusts for
        // updates, and making it configurable would turn "where does my terminal download and
        // execute new code from" into a user- or file-controlled value. Compiling it in is the
        // security property, not an oversight. Splitting it into concatenated parts would satisfy
        // the analyzer while making the code worse, so suppress it here with the reason attached.
        // NTILDE_UPDATE_SOURCE_DIR (UpdateSourceOverride) leaves that property intact for the released app: it only
        // works for an install packed under the verification id (UpdateSourceOverride.VerificationAppId), and the
        // released app, packed as NtildeApp, ignores it.
        [SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded",
            Justification = "The update feed's origin is a trust anchor and must not be configurable.")]
        public const string DefaultRepoUrl = "https://github.com/benyblack/ntilde";

        private readonly Action<string> _log;
        private readonly UpdateManager _manager;
        private UpdateInfo? _downloaded;

        /// <summary>
        /// The Velopack channel this process should read updates from, or null to use whatever
        /// channel the running release was packed with.
        /// </summary>
        /// <remarks>
        /// Linux is the only platform that needs this. Windows publishes one architecture and
        /// macOS one, so each resolves its platform-default channel (win, osx) unambiguously.
        /// Linux publishes x64 and arm64 into the SAME GitHub release, and a Velopack feed is
        /// per-channel, not per-architecture - so a single `linux` channel would put both
        /// architectures' packages in one releases.linux.json and could hand an arm64 client an
        /// x64 update.
        ///
        /// `vpk pack --channel linux-x64` (or linux-arm64) already bakes that channel into the
        /// package's .nuspec and into the release manifest filenames, so a client packed
        /// correctly resolves its own channel unaided - this method is a no-op in that case, not
        /// the mechanism preventing the collision. What this method actually guards against is a
        /// future release.yml change that packs without --channel: Velopack would then fall back
        /// to its platform-default `linux` channel, silently recreating the collision above. This
        /// resolves linux-x64 / linux-arm64 explicitly so that regression cannot happen, matching
        /// the --channel values release.yml passes to `vpk pack`.
        ///
        /// Taking isLinux and architecture as parameters rather than reading RuntimeInformation
        /// inline is what makes this assertable on any CI leg (see VelopackUpdateServiceTests).
        ///
        /// Returning null off Linux is load-bearing, not tidiness: Windows and macOS have
        /// installed clients in the field on their default channels, and naming a channel here
        /// would repoint them at a feed that does not exist.
        /// </remarks>
        internal static string? ResolveExplicitChannel(bool isLinux, Architecture architecture)
        {
            if (!isLinux)
            {
                return null;
            }

            return architecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                // Any architecture we publish no feed for: fall back to the packed default,
                // which finds nothing rather than offering a package for the wrong CPU.
                _ => null,
            };
        }

        private static UpdateOptions? BuildUpdateOptions()
        {
            var channel = ResolveExplicitChannel(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                RuntimeInformation.ProcessArchitecture);

            return channel is null ? null : new UpdateOptions { ExplicitChannel = channel };
        }

        public VelopackUpdateService(string repoUrl, Action<string> log)
        {
            _log = log;

            // prerelease: false - a prerelease tag must never pull stable users onto an
            // unfinished build. accessToken: null - the repo is public, and an unauthenticated
            // check is subject to GitHub's anonymous rate limit, which a once-per-launch check
            // is nowhere near.
            //
            // The locator is passed explicitly rather than left to UpdateManager's default.
            // Its parameterless resolution reads VelopackLocator.Current, which only gets
            // populated by VelopackApp.Build().Run() in Program.Main - so it throws
            // InvalidOperationException in every host that never executes that hook: the
            // portable zip, the winget portable package, a plain dev run, and this class's
            // own unit tests (confirmed empirically - constructing UpdateManager with the
            // implicit locator crashes the constructor itself in a test host, before
            // IsSupported ever gets a chance to say no). Falling back to
            // VelopackLocator.CreateDefaultForPlatform keeps construction inert in exactly
            // those hosts, and IsInstalled still resolves to false there as required.
            var locator = VelopackLocator.IsCurrentSet
                ? VelopackLocator.Current
                : VelopackLocator.CreateDefaultForPlatform(null, null);
            _manager = new UpdateManager(
                CreateSource(repoUrl, Environment.GetEnvironmentVariable(UpdateSourceOverride.Variable), locator.AppId, log),
                BuildUpdateOptions(),
                locator);
        }

        /// <summary>
        /// Where updates come from: the local feed directory <paramref name="sourceDirectoryValue"/> names (the value of
        /// <see cref="UpdateSourceOverride.Variable"/>, a verification hook) when <paramref name="appId"/>, this install's
        /// Velopack app id, is <see cref="UpdateSourceOverride.VerificationAppId"/>; else <paramref name="repoUrl"/>'s
        /// GitHub releases. What was chosen goes to <paramref name="log"/> whenever the variable is set, and nothing when
        /// it is not.
        /// </summary>
        internal static IUpdateSource CreateSource(string repoUrl, string? sourceDirectoryValue, string? appId, Action<string> log)
        {
            ArgumentNullException.ThrowIfNull(log);
            string? localFeed = UpdateSourceOverride.Resolve(sourceDirectoryValue, appId, out string? note);
            if (note is not null)
            {
                log(note);
            }

            return localFeed is null
                ? new GithubSource(repoUrl, null, false)
                : new SimpleFileSource(new System.IO.DirectoryInfo(localFeed));
        }

        /// <summary>
        /// True only when Velopack installed this process. False for the portable zip, the winget
        /// portable package, a Linux system package (.deb), and every dev run. A .deb install is
        /// updated through the user's package manager, so the in-app updater staying silent there
        /// is correct, not a gap - see <see cref="IUpdateService.IsSupported"/>.
        /// </summary>
        public bool IsSupported => _manager.IsInstalled;

        public async Task<UpdateAvailability> CheckAndDownloadAsync(CancellationToken ct)
        {
            if (!IsSupported)
            {
                return new UpdateAvailability(false, null);
            }

            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update == null)
            {
                return new UpdateAvailability(false, null);
            }

            var version = update.TargetFullRelease.Version.ToString();
            _log($"Update available: {version}; downloading.");

            await _manager.DownloadUpdatesAsync(update, null, ct).ConfigureAwait(false);
            _downloaded = update;

            _log($"Update {version} downloaded; waiting for a restart.");
            return new UpdateAvailability(true, version);
        }

        /// <summary>
        /// The downloaded release's notes as packed: <c>UpdateInfo.TargetFullRelease.NotesMarkdown</c>, which the feed
        /// (<c>releases.&lt;channel&gt;.json</c>) carries verbatim from <c>vpk pack --releaseNotes</c>, HTML comments included.
        /// </summary>
        public string? StagedReleaseNotes => _downloaded?.TargetFullRelease?.NotesMarkdown;

        public void ApplyAndRestart()
        {
            if (_downloaded == null)
            {
                return;
            }

            _log("Applying update and restarting.");
            _manager.ApplyUpdatesAndRestart(_downloaded);
        }
    }
}
