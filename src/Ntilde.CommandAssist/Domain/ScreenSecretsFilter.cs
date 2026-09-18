using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Ntilde.CommandAssist.Domain;

/// <summary>
/// Redaction for text that came off a terminal <em>screen</em> rather than a typed command line.
/// Runs the inner filter (<see cref="SecretsFilter"/>, the command-history patterns) first, then
/// the classes of secret that only appear in output: private-key blocks, credential-named
/// assignments in any config syntax, URL userinfo, well-known provider token shapes, and Basic
/// auth headers. Only screen inference uses this; command history keeps the inner filter, so what
/// Ctrl+R shows is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything except the private-key passes runs one screen line at a time.</strong> The
/// inner filter was written for a single command line, and at least one of its patterns
/// (<c>Password=[^;]+</c>) would otherwise run across line breaks and swallow the rest of the
/// screen after a <c>PASSWORD=</c> row in a <c>printenv</c> dump. Per-line application bounds every
/// pattern, keeps the line count stable, and is what a screen actually is: rows.
/// </para>
/// <para>
/// A private key taller than the viewport can lose its BEGIN line to scrollback while its body
/// and END marker stay visible, so an END marker with base64-looking rows above it is redacted
/// on its own; a BEGIN with no END (the block runs off the bottom) is redacted to the end of text.
/// </para>
/// <para>
/// Deliberately absent: a generic high-entropy detector. It would redact git SHAs, package
/// hashes and base64 in ordinary build output, which degrades what the classifier sees far more
/// than it protects. Documented in <c>docs/agent-host/known-limitations.md</c>. Soft-wrapped
/// tokens are the caller's job: the screen capture joins wrapped rows before this filter sees
/// them, because no line-bounded pattern can recognise a token split across two rows.
/// </para>
/// </remarks>
public sealed partial class ScreenSecretsFilter : ISecretsFilter
{
    private const string Redacted = "[REDACTED]";

    private static readonly Regex PrivateKeyBlockRegex = PrivateKeyBlock();
    private static readonly Regex OrphanPrivateKeyTailRegex = OrphanPrivateKeyTail();
    private static readonly Regex BasicAuthRegex = BasicAuth();
    private static readonly Regex UrlUserInfoRegex = UrlUserInfo();
    private static readonly Regex ProviderTokenRegex = ProviderToken();
    private static readonly Regex CredentialAssignmentEqualsRegex = CredentialAssignmentEquals();
    private static readonly Regex CredentialAssignmentColonRegex = CredentialAssignmentColon();

    private readonly ISecretsFilter _inner;

    public ScreenSecretsFilter(ISecretsFilter inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public RedactionResult Redact(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return new RedactionResult(commandText, false);
        }

        // Multi-line passes first, over the whole text.
        string redacted = PrivateKeyBlockRegex.Replace(commandText, static m => m.Groups[2].Success
            ? $"{m.Groups[1].Value}\n{Redacted}\n{m.Groups[2].Value}"
            : $"{m.Groups[1].Value}\n{Redacted}");
        redacted = OrphanPrivateKeyTailRegex.Replace(redacted, static m => $"{Redacted}\n{m.Groups[1].Value}");

        // Everything else line by line, so no pattern (ours or the inner filter's) can cross a row.
        string[] lines = redacted.Split('\n');
        var sb = new StringBuilder(redacted.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(RedactLine(lines[i]));
        }
        redacted = sb.ToString();

        return new RedactionResult(redacted, !string.Equals(commandText, redacted, StringComparison.Ordinal));
    }

    private string RedactLine(string line)
    {
        if (line.Length == 0) return line;

        string redacted = _inner.Redact(line).RedactedText;
        redacted = BasicAuthRegex.Replace(redacted, "$1" + Redacted);
        redacted = UrlUserInfoRegex.Replace(redacted, "$1" + Redacted + "@");
        redacted = ProviderTokenRegex.Replace(redacted, Redacted);
        redacted = CredentialAssignmentEqualsRegex.Replace(redacted, "$1" + Redacted);
        redacted = CredentialAssignmentColonRegex.Replace(redacted, "$1" + Redacted);
        return redacted;
    }

    // "-----BEGIN ... PRIVATE KEY-----" through the matching END line, or to the end of the text
    // when the screen cut the block off at the bottom. The body is matched lazily so two blocks on
    // one screen are redacted separately.
    [GeneratedRegex(@"(-----BEGIN [A-Z ]*PRIVATE KEY-----)\r?\n[\s\S]*?(?:\r?\n(-----END [A-Z ]*PRIVATE KEY-----)|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    // The block's BEGIN line scrolled off the top: base64-looking rows immediately above an END
    // marker. Key bodies are 64-70 chars of base64 per row and the final row may be short (even
    // "AQ=="), so the tail is either full rows with an optional short last row, or a lone short
    // row. A prompt or prose row is not base64, so the redaction stops at the body's top edge.
    [GeneratedRegex(@"(?:(?:^[A-Za-z0-9+/=]{16,}\r?\n)+(?:^[A-Za-z0-9+/=]{1,15}\r?\n)?|^[A-Za-z0-9+/=]{1,15}\r?\n)(-----END [A-Z ]*PRIVATE KEY-----)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex OrphanPrivateKeyTail();

    // Sibling of the inner filter's "Authorization: Bearer" pattern.
    [GeneratedRegex(@"(Authorization:\s+Basic\s+)(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BasicAuth();

    // user:pass@host and TOKEN@host after a scheme: git remotes, connection URLs.
    [GeneratedRegex(@"(://)[^/\s@:]+(?::[^/\s@]*)?@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    // Provider-specific token shapes, case-sensitive by design: GitHub, GitLab, Slack, AWS access
    // key ids, OpenAI/Anthropic-style "sk-", TypeSafe "apikey_", bare JWTs. No leading word
    // boundary on purpose: a token glued to a preceding word (a stale soft-wrap join, a repainted
    // row) must still be caught, and redacting the tail of an identifier that happens to contain
    // one of these prefixes costs nothing.
    [GeneratedRegex(
        @"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,}|apikey_[A-Za-z0-9_]{20,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProviderToken();

    // Credential-named assignments, where the name carries a credential keyword. Bare "auth" is
    // excluded on purpose ("Author:" in git log, and the "Authorization:" header, which its own
    // patterns handle); so is "pwd" (printenv's PWD=). Two shapes with different value rules:
    //
    //  NAME=value  (shell, .env, INI): the value is one shell word, or a quoted string.
    //  name: value (YAML, JSON, key/value prints): the value runs to the end of the line, stopping
    //              before a trailing ",", or before a "# comment" that is separated from the value
    //              by whitespace (a "#" glued to the value is part of it, as in YAML), or is a
    //              quoted string.
    //
    // Quoted strings consume backslash escapes so an embedded \" cannot end the value early.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.\-])(""?'?[A-Za-z0-9_.\-]*(?:secret|token|passw(?:or)?d|api[_\-]?key|access[_\-]?key|private[_\-]?key|client[_\-]?secret|auth[_\-]?token)[A-Za-z0-9_.\-]*""?'?\s*=\s*)(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentEquals();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.\-])(?!authorization\s*:)(""?'?[A-Za-z0-9_.\-]*(?:secret|token|passw(?:or)?d|api[_\-]?key|access[_\-]?key|private[_\-]?key|client[_\-]?secret|auth[_\-]?token)[A-Za-z0-9_.\-]*""?'?\s*:\s*)(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|[^\s#,;][^\r\n]*?)(?=\s*,?(?:\s+#.*)?\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentColon();
}
