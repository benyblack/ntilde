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
            bool[] wrapped;
            int rows, cols;
            buffer.Lock.EnterReadLock();
            try
            {
                lines = BufferSnapshot.Capture(buffer, includeAttributes: false).Lines;
                // IsWrapped on row i means the row ended by auto-wrap, so row i+1 continues it. A
                // genuine wrap fills the row to its last column, so a row whose last cell holds no
                // written character is treated as not wrapped even if the flag says otherwise:
                // that shape is a row erased and repainted with shorter content, and joining it
                // to the row below would glue unrelated text together (and could hide a token
                // from the filter behind a missing word boundary).
                wrapped = buffer.ViewportRows.Select(r => r.IsWrapped && RowEndsWithContent(r)).ToArray();
                rows = buffer.Rows;
                cols = buffer.Cols;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            return new ScreenSample(JoinSoftWrappedRows(lines, wrapped), rows, cols);
        }

        private static bool RowEndsWithContent(Ntilde.VT.TerminalRow row)
        {
            if (row.Cells.Length == 0) return false;
            char last = row.Cells[^1].Character;
            return last != ' ' && last != '\0';
        }

        /// <summary>
        /// Re-joins rows that the terminal soft-wrapped, so a token or path that the viewport
        /// split across two rows reaches the secrets filter (and the model) as one string. A
        /// line-bounded redaction pattern cannot recognise <c>ghp_abc</c> on one row and the rest on
        /// the next; without this, a narrow pane would leak exactly the tokens the filter exists
        /// to catch. Hard line breaks stay as <c>\n</c>. Trailing blank lines are dropped.
        /// </summary>
        internal static string JoinSoftWrappedRows(string[] lines, bool[] wrapped)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append(lines[i].TrimEnd());
                bool continues = i < wrapped.Length && wrapped[i] && i + 1 < lines.Length;
                if (!continues && i + 1 < lines.Length)
                {
                    sb.Append('\n');
                }
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}
