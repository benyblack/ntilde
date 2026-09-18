using System;
using System.Linq;
using System.Net.Http;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;
using Ntilde.Replay;
using Ntilde.Shell;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// Reads the TypeSafe key from the OS secret store, but re-reads it at most every 30 s, and
    /// immediately after a key is saved (<see cref="Invalidate"/>). Screen inference ticks every
    /// second, and every tick calls <see cref="TryGetKey"/>; without the cache that is a
    /// secret-store round trip once a second even when the key never changes.
    /// </summary>
    internal sealed class VaultApiKeySource : IApiKeySource
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

        private readonly VaultService _vault;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _lock = new();
        private string? _cached;
        private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

        public VaultApiKeySource(VaultService vault, Func<DateTimeOffset>? nowProvider = null)
        {
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));
            _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        public string? TryGetKey()
        {
            lock (_lock)
            {
                var now = _now();
                if (now - _cachedAt < Ttl) return _cached;
                _cached = _vault.GetInferenceApiKey();
                _cachedAt = now;
                return _cached;
            }
        }

        /// <summary>Forces the next <see cref="TryGetKey"/> call to re-read the vault.</summary>
        public void Invalidate()
        {
            lock (_lock) { _cachedAt = DateTimeOffset.MinValue; }
        }
    }

    /// <summary>
    /// The process-wide monitor with its real dependencies. Kept apart from
    /// <see cref="ObservedActivityMonitor"/> so the monitor's logic stays constructible with fakes.
    /// </summary>
    public static class ObservedActivityMonitorComposition
    {
        private static readonly Lazy<ObservedActivityMonitor> LazyInstance = new(Create);
        private static VaultApiKeySource? _apiKeySource;

        public static ObservedActivityMonitor Instance => LazyInstance.Value;

        /// <summary>
        /// Forces the next screen-inference request to re-read the API key from the OS secret
        /// store, instead of waiting out the 30 s cache. Called right after Settings saves a key.
        /// </summary>
        internal static void InvalidateApiKeyCache() => _apiKeySource?.Invalidate();

        /// <summary>
        /// The filter every captured screen passes through before it leaves the process: the
        /// output-oriented <see cref="ScreenSecretsFilter"/> layered over the command-history
        /// <see cref="SecretsFilter"/>. Internal so a test can pin the choice.
        /// </summary>
        internal static ISecretsFilter CreateScreenSecretsFilter() => new ScreenSecretsFilter(new SecretsFilter());

        private static ObservedActivityMonitor Create()
        {
            var http = new HttpClient { Timeout = SystemOneClient.DefaultTimeout };
            _apiKeySource = new VaultApiKeySource(new VaultService());
            var client = new SystemOneClient(http, _apiKeySource);
            return new ObservedActivityMonitor(
                AgentSessionRegistry.Instance,
                new ScreenActivityClassifier(client),
                CreateScreenSecretsFilter(),
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
