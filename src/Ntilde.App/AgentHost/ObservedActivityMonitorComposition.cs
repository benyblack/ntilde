using System;
using System.Linq;
using System.Net.Http;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;
using Ntilde.Replay;
using Ntilde.Shell;

namespace Ntilde.AgentHost
{
    /// <summary>Reads the TypeSafe key from the OS secret store on every call, so a key saved in Settings takes effect without a restart.</summary>
    internal sealed class VaultApiKeySource : IApiKeySource
    {
        private readonly VaultService _vault;
        public VaultApiKeySource(VaultService vault) => _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        public string? TryGetKey() => _vault.GetInferenceApiKey();
    }

    /// <summary>
    /// The process-wide monitor with its real dependencies. Kept apart from
    /// <see cref="ObservedActivityMonitor"/> so the monitor's logic stays constructible with fakes.
    /// </summary>
    public static class ObservedActivityMonitorComposition
    {
        private static readonly Lazy<ObservedActivityMonitor> LazyInstance = new(Create);

        public static ObservedActivityMonitor Instance => LazyInstance.Value;

        private static ObservedActivityMonitor Create()
        {
            var http = new HttpClient { Timeout = SystemOneClient.DefaultTimeout };
            var client = new SystemOneClient(http, new VaultApiKeySource(new VaultService()));
            return new ObservedActivityMonitor(
                AgentSessionRegistry.Instance,
                new ScreenActivityClassifier(client),
                new SecretsFilter(),
                CaptureVisibleText,
                () => DateTimeOffset.UtcNow,
                AppLogger.Log);
        }

        /// <summary>
        /// The visible viewport as text, captured under the buffer read lock exactly like the
        /// agent-host <c>readScreen</c> handler. Trailing whitespace per line is trimmed and
        /// trailing blank lines dropped so a mostly-empty screen costs few tokens. Any thread.
        /// </summary>
        public static ScreenSample? CaptureVisibleText(AgentSessionRegistration registration)
        {
            var buffer = registration.Buffer;
            string[] lines;
            int rows, cols;
            buffer.Lock.EnterReadLock();
            try
            {
                lines = BufferSnapshot.Capture(buffer, includeAttributes: false).Lines;
                rows = buffer.Rows;
                cols = buffer.Cols;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            var text = string.Join('\n', lines.Select(l => l.TrimEnd())).TrimEnd('\n');
            return new ScreenSample(text, rows, cols);
        }
    }
}
