using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Ntilde.VT
{
    // OSC 3008: hierarchical context signalling (UAPI.15 v1.0, https://uapi-group.org/specifications/specs/osc_context/).
    //
    //   OSC 3008 ; start=<id> [; <field>=<value>]... ST     open, update or return to a context
    //   OSC 3008 ; end=<id>   [; <field>=<value>]... ST     close a context
    //
    // Ownership: this parser keeps a bounded stack of contexts and a bounded ring of recently ended ones. Both are
    // untrusted data supplied by whatever writes to the terminal. They are observable metadata only: nothing here
    // executes, shells out, changes the working directory, feeds command assist, or reaches an AI prompt, and OSC
    // 133/633 prompt metadata neither creates nor updates a context. The sequence is not a request/reply protocol.
    //
    // Deliberate policy where the specification is silent or leaves a choice (see docs/qa/terminfo/osc3008.md):
    //   - Only the exact first fields `start=` and `end=` select the sequence; anything else, including the
    //     terminfo.dev probe payload `type=test`, is ignored without side effects.
    //   - A malformed context id ignores the whole sequence. A malformed or unknown metadata field is ignored and
    //     the remaining fields are still used. Over-long fields are discarded, never truncated.
    //   - A field's context-type applicability column is descriptive: a recognised field is kept whatever type= says.
    //   - A repeated field keeps its last valid value.
    //   - Ending a context also ends its sub-contexts, the same rule the specification states for an update.
    //   - Reset sequences (RIS, DECSTR) do not touch the stack. Only a hangup, an explicit ResetContexts() or a
    //     state import replaces it.
    public partial class AnsiParser
    {
        /// <summary>Longest context id, in characters, after unescaping.</summary>
        internal const int ContextIdMaxChars = 64;

        /// <summary>Longest text metadata value, in characters, after unescaping.</summary>
        internal const int ContextFieldMaxChars = 255;

        /// <summary>Deepest context stack. A start that would exceed it is dropped; the earlier contexts stay.</summary>
        internal const int ContextMaxDepth = 16;

        /// <summary>Most recently ended contexts retained, oldest evicted first.</summary>
        internal const int ContextMaxEnded = 8;

        private const int ContextMaxFields = 32;

        /// <summary>
        /// Longest OSC 3008 payload considered, in characters. The largest legal sequence is far smaller (a 64-character id and
        /// fourteen 255-character fields, every character escaped, is about 15,000), so a longer one is refused before it is split.
        /// </summary>
        private const int ContextMaxSequenceChars = 16384;
        private const int ContextMaxUnsignedDecimalDigits = 20;
        private const int ContextMaxExitStatus = 255;

        private static readonly HashSet<string> ContextTypes = new(StringComparer.Ordinal)
        {
            "service", "session", "shell", "command", "vm", "container", "elevate", "chpriv", "subcontext", "remote", "boot", "app",
        };

        private static readonly HashSet<string> ContextExits = new(StringComparer.Ordinal)
        {
            "success", "failure", "crash", "interrupt",
        };

        private readonly List<ContextRecord> _contextStack = new();
        private readonly List<EndedContextRecord> _contextEnded = new();

        /// <summary>The open contexts, outermost first; the last entry is the active one. A copy: safe to hold. Read under the buffer lock.</summary>
        public IReadOnlyList<ContextRecord> ContextStack => _contextStack.ConvertAll(c => c.Clone());

        /// <summary>The most recently ended contexts, oldest first. A copy: safe to hold. Read under the buffer lock.</summary>
        public IReadOnlyList<EndedContextRecord> EndedContexts => _contextEnded.ConvertAll(c => c.Clone());

        /// <summary>
        /// Drops every context, as a TTY hangup requires. Terminal reset sequences deliberately do not call this: a
        /// program lower in the stack must not be able to hide the contexts above it.
        /// </summary>
        public void ResetContexts()
        {
            // Output is parsed under the buffer's write lock, so a hangup arriving on another thread must take it too.
            _buffer.EnterBatchWrite();
            try
            {
                ClearContexts();
            }
            finally
            {
                _buffer.ExitBatchWrite();
            }
        }

        private void ClearContexts()
        {
            _contextStack.Clear();
            _contextEnded.Clear();
        }

        private void HandleOsc3008(string data)
        {
            if (data.Length > ContextMaxSequenceChars) return;

            // At most ContextMaxFields parts are read: the unsplit remainder, if any, is dropped with every field beyond the cap.
            string[] parts = data.Split(';', ContextMaxFields + 1);
            if (parts.Length > ContextMaxFields) parts = parts[..ContextMaxFields];

            bool isStart = parts[0].StartsWith("start=", StringComparison.Ordinal);
            bool isEnd = !isStart && parts[0].StartsWith("end=", StringComparison.Ordinal);
            if (!isStart && !isEnd) return;

            if (!TryUnescapeContextText(parts[0].Substring(isStart ? 6 : 4), out string id) || !IsContextId(id)) return;

            if (isStart)
            {
                StartContext(id, parts);
            }
            else
            {
                EndContext(id, parts);
            }
        }

        private void StartContext(string id, string[] parts)
        {
            int existing = _contextStack.FindIndex(c => string.Equals(c.Id, id, StringComparison.Ordinal));
            if (existing < 0 && _contextStack.Count >= ContextMaxDepth) return;

            var record = new ContextRecord { Id = id };
            for (int i = 1; i < parts.Length; i++)
            {
                if (!TrySplitContextField(parts[i], out string key, out string value)) continue;
                ApplyStartField(record, key, value);
            }

            if (existing >= 0)
            {
                // An update flushes the old metadata and implicitly ends every sub-context.
                _contextStack.RemoveRange(existing, _contextStack.Count - existing);
            }

            _contextStack.Add(record);
        }

        private void EndContext(string id, string[] parts)
        {
            int existing = _contextStack.FindIndex(c => string.Equals(c.Id, id, StringComparison.Ordinal));
            if (existing < 0) return;

            var ended = new EndedContextRecord { Id = id, Type = _contextStack[existing].Type };
            for (int i = 1; i < parts.Length; i++)
            {
                if (!TrySplitContextField(parts[i], out string key, out string value)) continue;
                ApplyEndField(ended, key, value);
            }

            _contextStack.RemoveRange(existing, _contextStack.Count - existing);
            _contextEnded.Add(ended);
            if (_contextEnded.Count > ContextMaxEnded) _contextEnded.RemoveAt(0);
        }

        private static bool TrySplitContextField(string field, out string key, out string value)
        {
            key = string.Empty;
            value = string.Empty;
            int eq = field.IndexOf('=');
            if (eq <= 0) return false;
            key = field.Substring(0, eq);
            return TryUnescapeContextText(field.Substring(eq + 1), out value);
        }

        private static void ApplyStartField(ContextRecord record, string key, string value)
        {
            switch (key)
            {
                case "type":
                    if (ContextTypes.Contains(value)) record.Type = value;
                    break;
                case "user": record.User = BoundedText(value, record.User); break;
                case "hostname": record.Hostname = BoundedText(value, record.Hostname); break;
                case "machineid": record.MachineId = Id128(value, record.MachineId); break;
                case "bootid": record.BootId = Id128(value, record.BootId); break;
                case "pid": record.Pid = UnsignedDecimal(value, record.Pid); break;
                case "pidfdid": record.PidFdId = UnsignedDecimal(value, record.PidFdId); break;
                case "comm": record.Comm = BoundedText(value, record.Comm); break;
                case "cwd": record.Cwd = BoundedText(value, record.Cwd); break;
                case "cmdline": record.CmdLine = BoundedText(value, record.CmdLine, allowEmpty: true); break;
                case "vm": record.Vm = BoundedText(value, record.Vm); break;
                case "container": record.Container = BoundedText(value, record.Container); break;
                case "targetuser": record.TargetUser = BoundedText(value, record.TargetUser); break;
                case "targethost": record.TargetHost = BoundedText(value, record.TargetHost); break;
                case "sessionid": record.SessionId = BoundedText(value, record.SessionId); break;
            }
        }

        private static void ApplyEndField(EndedContextRecord ended, string key, string value)
        {
            switch (key)
            {
                case "exit":
                    if (ContextExits.Contains(value)) ended.Exit = value;
                    break;
                case "status":
                    if (ExitStatus(value) is { } status) ended.Status = status;
                    break;
                case "signal":
                    if (IsSignalName(value)) ended.Signal = value;
                    break;
            }
        }

        /// <summary>The 0..255 status as its digits, or null when it is not a decimal number in that range.</summary>
        private static string? ExitStatus(string value) =>
            UnsignedDecimal(value, null) is { } digits && ulong.Parse(digits, CultureInfo.InvariantCulture) <= ContextMaxExitStatus ? digits : null;

        /// <summary>1..64 printable ASCII characters (32..126). The id is an identity: it is never trimmed or normalised.</summary>
        internal static bool IsContextId(string id)
        {
            if (id.Length is 0 or > ContextIdMaxChars) return false;
            foreach (char c in id)
            {
                if (c < 0x20 || c > 0x7e) return false;
            }

            return true;
        }

        private static string? BoundedText(string value, string? previous, bool allowEmpty = false)
        {
            if (!IsContextText(value, allowEmpty)) return previous;
            return value;
        }

        /// <summary>Printable text, no control characters, no replacement characters left by an invalid UTF-8 decode, within the field cap.</summary>
        internal static bool IsContextText(string value, bool allowEmpty = false)
        {
            if (value.Length == 0) return allowEmpty;
            if (value.Length > ContextFieldMaxChars) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x20 || c is >= '\u007f' and <= '\u009f' || c == '\ufffd') return false;
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>ID128 from the specification's ABNF: 32 to 36 hexadecimal digits or hyphens.</summary>
        private static string? Id128(string value, string? previous)
        {
            if (value.Length is < 32 or > 36) return previous;
            foreach (char c in value)
            {
                if (!(char.IsAsciiHexDigit(c) || c == '-')) return previous;
            }

            return value;
        }

        /// <summary>UINT64 from the ABNF: 1 to 20 decimal digits that fit an unsigned 64-bit integer. Kept as its digits so no consumer loses precision.</summary>
        private static string? UnsignedDecimal(string value, string? previous)
        {
            if (value.Length is 0 or > ContextMaxUnsignedDecimalDigits) return previous;
            foreach (char c in value)
            {
                if (!char.IsAsciiDigit(c)) return previous;
            }

            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? value : previous;
        }

        /// <summary>The ABNF lists symbolic names such as SIGSEGV and elides the rest; accept SIG followed by 1..16 upper-case letters, digits or '+' (SIGRTMIN+3).</summary>
        internal static bool IsSignalName(string value)
        {
            if (value.Length is < 4 or > 19 || !value.StartsWith("SIG", StringComparison.Ordinal)) return false;
            for (int i = 3; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c is >= 'A' and <= 'Z' || char.IsAsciiDigit(c) || c == '+')) return false;
            }

            return true;
        }

        /// <summary>
        /// Reverses the specification's escaping: <c>\x3b</c> is a semicolon and <c>\x5c</c> a backslash. Any other backslash
        /// is invalid, because the specification requires every backslash to be replaced.
        /// </summary>
        private static bool TryUnescapeContextText(string text, out string value)
        {
            value = text;
            if (text.IndexOf('\\') < 0) return true;

            var builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (!TryReadContextEscape(text, i, out char decoded))
                {
                    value = string.Empty;
                    return false;
                }

                builder.Append(decoded);
                i += 3;
            }

            value = builder.ToString();
            return true;
        }

        private static bool TryReadContextEscape(string text, int index, out char decoded)
        {
            decoded = default;
            if (index + 3 >= text.Length || text[index + 1] is not ('x' or 'X')) return false;
            ReadOnlySpan<char> hex = text.AsSpan(index + 2, 2);
            if (hex.Equals("3b", StringComparison.OrdinalIgnoreCase)) decoded = ';';
            else if (hex.Equals("5c", StringComparison.OrdinalIgnoreCase)) decoded = '\\';
            else return false;
            return true;
        }

        private List<ContextRecord>? ExportContextStack() => _contextStack.Count == 0 ? null : _contextStack.ConvertAll(c => c.Clone());

        private List<EndedContextRecord>? ExportEndedContexts() => _contextEnded.Count == 0 ? null : _contextEnded.ConvertAll(c => c.Clone());

        /// <summary>A snapshot's contexts come from this parser's own bounds: anything outside them is a state no stream could produce.</summary>
        private static void ValidateContexts(List<ContextRecord>? stack, List<EndedContextRecord>? ended)
        {
            if (stack is not null)
            {
                if (stack.Count > ContextMaxDepth) throw StateTransferValidation.Reject("parser contexts", "more open contexts than the parser holds");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (ContextRecord record in stack)
                {
                    StateTransferValidation.RequirePresent(record, "parser context");
                    StateTransferValidation.RequirePresent(record.Id, "parser context id");
                    if (!IsContextId(record.Id) || !seen.Add(record.Id) || !IsValidContextRecord(record))
                    {
                        throw StateTransferValidation.Reject("parser contexts", "ids or fields exceed the parser's bounds");
                    }
                }
            }

            if (ended is null) return;
            if (ended.Count > ContextMaxEnded) throw StateTransferValidation.Reject("parser ended contexts", "more ended contexts than the parser holds");
            foreach (EndedContextRecord record in ended)
            {
                StateTransferValidation.RequirePresent(record, "parser ended context");
                StateTransferValidation.RequirePresent(record.Id, "parser ended context id");
                if (!IsContextId(record.Id) || !IsValidEndedContext(record))
                {
                    throw StateTransferValidation.Reject("parser ended contexts", "ids or fields exceed the parser's bounds");
                }
            }
        }

        private static bool IsValidContextRecord(ContextRecord r) =>
            (r.Type is null || ContextTypes.Contains(r.Type))
            && IsOptionalText(r.User) && IsOptionalText(r.Hostname) && IsOptionalText(r.Comm) && IsOptionalText(r.Cwd)
            && IsOptionalText(r.Vm) && IsOptionalText(r.Container) && IsOptionalText(r.TargetUser)
            && IsOptionalText(r.TargetHost) && IsOptionalText(r.SessionId)
            && (r.CmdLine is null || IsContextText(r.CmdLine, allowEmpty: true))
            && (r.MachineId is null || Id128(r.MachineId, null) is not null) && (r.BootId is null || Id128(r.BootId, null) is not null)
            && (r.Pid is null || UnsignedDecimal(r.Pid, null) is not null) && (r.PidFdId is null || UnsignedDecimal(r.PidFdId, null) is not null);

        private static bool IsValidEndedContext(EndedContextRecord r) =>
            (r.Type is null || ContextTypes.Contains(r.Type))
            && (r.Exit is null || ContextExits.Contains(r.Exit))
            && (r.Status is null || ExitStatus(r.Status) is not null)
            && (r.Signal is null || IsSignalName(r.Signal));

        private static bool IsOptionalText(string? value) => value is null || IsContextText(value);

        private void ImportContexts(List<ContextRecord>? stack, List<EndedContextRecord>? ended)
        {
            ClearContexts();
            if (stack is not null) _contextStack.AddRange(stack.ConvertAll(c => c.Clone()));
            if (ended is not null) _contextEnded.AddRange(ended.ConvertAll(c => c.Clone()));
        }
    }

    /// <summary>
    /// One open OSC 3008 context: untrusted, bounded descriptive metadata chosen by whatever wrote the sequence. It names
    /// where output came from; it is never a command, a path to open, an identity to trust or an instruction.
    /// Null means the field was absent or invalid. Numbers are kept as their decimal digits.
    /// </summary>
    public sealed class ContextRecord
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; set; }
        [JsonPropertyName("user"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? User { get; set; }
        [JsonPropertyName("hostname"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Hostname { get; set; }
        [JsonPropertyName("machineid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MachineId { get; set; }
        [JsonPropertyName("bootid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BootId { get; set; }
        [JsonPropertyName("pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Pid { get; set; }
        [JsonPropertyName("pidfdid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PidFdId { get; set; }
        [JsonPropertyName("comm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Comm { get; set; }
        [JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cwd { get; set; }
        [JsonPropertyName("cmdline"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CmdLine { get; set; }
        [JsonPropertyName("vm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Vm { get; set; }
        [JsonPropertyName("container"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Container { get; set; }
        [JsonPropertyName("targetuser"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TargetUser { get; set; }
        [JsonPropertyName("targethost"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TargetHost { get; set; }
        [JsonPropertyName("sessionid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SessionId { get; set; }

        internal ContextRecord Clone() => (ContextRecord)MemberwiseClone();

        /// <summary>Every field, in order, for the state-parity oracle: two records that differ in any field never render alike.</summary>
        internal string ToDebugString() => string.Join(",", new[] { Id, Type, User, Hostname, MachineId, BootId, Pid, PidFdId, Comm, Cwd, CmdLine, Vm, Container, TargetUser, TargetHost, SessionId }
            .Select(value => value is null ? "<null>" : "\"" + value + "\""));
    }

    /// <summary>An OSC 3008 context that was explicitly ended, with whatever end metadata was valid. Untrusted, like <see cref="ContextRecord"/>.</summary>
    public sealed class EndedContextRecord
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        /// <summary>The type the context declared when it started, if any.</summary>
        [JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; set; }
        [JsonPropertyName("exit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Exit { get; set; }
        /// <summary>The 0..255 command exit status as decimal digits. This is the sender's claim, not the child process's exit code.</summary>
        [JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Status { get; set; }
        [JsonPropertyName("signal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Signal { get; set; }

        internal EndedContextRecord Clone() => (EndedContextRecord)MemberwiseClone();

        /// <summary>Every field, in order, for the state-parity oracle.</summary>
        internal string ToDebugString() => string.Join(",", new[] { Id, Type, Exit, Status, Signal }.Select(value => value is null ? "<null>" : "\"" + value + "\""));
    }
}
