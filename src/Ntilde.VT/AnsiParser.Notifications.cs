using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ntilde.VT
{
    // OSC 777 (urxvt/VTE/Konsole `notify`) and OSC 99 (kitty desktop-notification protocol).
    // Both end in OnTitledDesktopNotification; the parser only decodes, sanitizes and caps. Whether a
    // toast appears (opt-in setting, focus policy) stays the App layer's call, the same split as OSC 9.
    // Scope is deliberately small and stated here so the capability reply never over-claims:
    //   OSC 777 ; notify ; <title> [; <body>]
    //   OSC 99 ; <metadata> ; <payload>, with the metadata keys i (id), d (done), p (payload type:
    //     title, body, or ? for the capability query) and e (base64 payload). Every other key is
    //     ignored, as the protocol requires of unknown keys; payload types close, alive, icon and
    //     buttons are ignored, so no actions, close reports, urgency, sounds or expiry are claimed.
    public partial class AnsiParser
    {
        private const int NotificationTitleMaxChars = 80;
        private const int NotificationBodyMaxChars = Osc9MaxNotificationChars;
        private const int Osc99MaxChunkChars = 4096;
        private const int Osc99MaxPendingNotifications = 8;
        private const int Osc99MaxPendingChars = 8192;
        private const int Osc99MaxEchoedIdChars = 64;
        private const string Osc99IdAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-+.";

        // Capability reply payload for `OSC 99 ; i=<id>:p=? ;`: o=always is mandatory when no other occasion
        // is honored, p lists exactly the payload types handled here, and a, c, u, w, s are absent
        // because actions, close reports, urgency, expiry and sounds are not implemented.
        private const string Osc99CapabilityPayload = "o=always:p=title,body";

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly Dictionary<string, PendingOsc99Notification> _osc99Pending = new(StringComparer.Ordinal);
        private readonly List<string> _osc99PendingOrder = new();

        private sealed class PendingOsc99Notification
        {
            public readonly StringBuilder Title = new();
            public readonly StringBuilder Body = new();
            public bool Overflowed;
        }

        /// <summary>
        /// Raised for OSC 777 <c>notify</c> and OSC 99 notifications once a complete notification is
        /// known. Both strings are already flattened to one line and capped; either may be empty, never
        /// both. OSC 9 keeps its own <see cref="OnDesktopNotification"/> shape.
        /// </summary>
        public Action<string, string>? OnTitledDesktopNotification { get; set; }

        private static string SanitizeNotificationText(string text, int maxChars)
        {
            var builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    continue;
                }

                builder.Append(char.IsControl(ch) ? ' ' : ch);
            }

            string result = builder.ToString().Trim();
            return result.Length > maxChars ? result[..(maxChars - 1)] + "…" : result;
        }

        private void RaiseTitledNotification(string rawTitle, string rawBody)
        {
            string title = SanitizeNotificationText(rawTitle, NotificationTitleMaxChars);
            string body = SanitizeNotificationText(rawBody, NotificationBodyMaxChars);
            if (title.Length == 0 && body.Length == 0) return;
            OnTitledDesktopNotification?.Invoke(title, body);
        }

        // OSC 777 ; notify ; title [; body]. Other 777 subcommands (precmd, preexec, ...) are not
        // notifications and are ignored.
        private void HandleOsc777(string data)
        {
            const string prefix = "notify;";
            if (!data.StartsWith(prefix, StringComparison.Ordinal)) return;

            string rest = data.Substring(prefix.Length);
            int split = rest.IndexOf(';');
            string title = split < 0 ? rest : rest.Substring(0, split);
            string body = split < 0 ? string.Empty : rest.Substring(split + 1);
            RaiseTitledNotification(title, body);
        }

        private static Dictionary<char, string> ParseOsc99Metadata(string metadata)
        {
            var values = new Dictionary<char, string>();
            foreach (string field in metadata.Split(':'))
            {
                int eq = field.IndexOf('=');
                if (eq == 1 && char.IsAsciiLetter(field[0]))
                {
                    values[field[0]] = field.Substring(2);
                }
            }

            return values;
        }

        private static bool IsOsc99Id(string id) => id.All(c => Osc99IdAlphabet.Contains(c));

        private static string SanitizeOsc99Id(string? id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            return SanitizeEchoParameter(id, Osc99IdAlphabet, Osc99MaxEchoedIdChars, string.Empty);
        }

        private void HandleOsc99(string data)
        {
            // The grammar always carries both semicolons, even with no metadata.
            int split = data.IndexOf(';');
            if (split < 0) return;

            var metadata = ParseOsc99Metadata(data.Substring(0, split));
            string payload = data.Substring(split + 1);
            metadata.TryGetValue('p', out string? type);
            type ??= "title";

            if (type == "?")
            {
                metadata.TryGetValue('i', out string? queryId);
                string id = SanitizeOsc99Id(queryId);
                string idField = id.Length == 0 ? string.Empty : $"i={id}:";
                OnResponse?.Invoke($"\x1b]99;{idField}p=?;{Osc99CapabilityPayload}\x1b\\");
                return;
            }

            if (type != "title" && type != "body") return;

            // A chunk's id is its identity: an id outside the protocol's alphabet or longer than the cap is refused, never trimmed into another notification's key.
            metadata.TryGetValue('i', out string? rawId);
            rawId ??= string.Empty;
            if (rawId.Length > Osc99MaxEchoedIdChars || !IsOsc99Id(rawId)) return;
            metadata.TryGetValue('e', out string? encoded);
            metadata.TryGetValue('d', out string? done);
            AccumulateOsc99(rawId, type == "body", payload, encoded == "1", done != "0");
        }

        private void AccumulateOsc99(string id, bool isBody, string payload, bool base64, bool done)
        {
            _osc99Pending.TryGetValue(id, out var pending);
            bool isNew = pending is null;
            pending ??= new PendingOsc99Notification();

            if (!TryDecodeOsc99Payload(payload, base64, out string text))
            {
                pending.Overflowed = true;
            }
            else if (!pending.Overflowed)
            {
                (isBody ? pending.Body : pending.Title).Append(text);
                if (pending.Title.Length + pending.Body.Length > Osc99MaxPendingChars)
                {
                    pending.Overflowed = true;
                }
            }

            if (done)
            {
                RemovePendingOsc99(id);
                if (!pending.Overflowed) RaiseTitledNotification(pending.Title.ToString(), pending.Body.ToString());
                return;
            }

            if (isNew) StorePendingOsc99(id, pending);
        }

        private static bool TryDecodeOsc99Payload(string payload, bool base64, out string text)
        {
            text = string.Empty;
            if (payload.Length > Osc99MaxChunkChars) return false;
            if (!base64)
            {
                text = payload;
                return true;
            }

            try
            {
                text = StrictUtf8.GetString(Convert.FromBase64String(payload));
                return true;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return false;
            }
        }

        private void StorePendingOsc99(string id, PendingOsc99Notification pending)
        {
            if (_osc99PendingOrder.Count >= Osc99MaxPendingNotifications)
            {
                _osc99Pending.Remove(_osc99PendingOrder[0]);
                _osc99PendingOrder.RemoveAt(0);
            }

            _osc99Pending[id] = pending;
            _osc99PendingOrder.Add(id);
        }

        /// <summary>RIS discards any unfinished OSC 99 notification: a later one reusing its id must not inherit its text.</summary>
        private void ClearPendingOsc99()
        {
            _osc99Pending.Clear();
            _osc99PendingOrder.Clear();
        }

        private void RemovePendingOsc99(string id)
        {
            if (_osc99Pending.Remove(id)) _osc99PendingOrder.Remove(id);
        }
    }
}
