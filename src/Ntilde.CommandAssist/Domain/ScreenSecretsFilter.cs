using System;
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
/// Deliberately absent: a generic high-entropy detector. It would redact git SHAs, package
/// hashes and base64 in ordinary build output, which degrades what the classifier sees far more
/// than it protects. Documented in <c>docs/agent-host/known-limitations.md</c>.
/// </para>
/// <para>
/// Every pattern keeps the name and redacts the value, so the model still sees that an
/// assignment happened. Single-line patterns never add or remove lines; the private-key block
/// collapses its body to one <c>[REDACTED]</c> line.
/// </para>
/// </remarks>
public sealed partial class ScreenSecretsFilter : ISecretsFilter
{
    private const string Redacted = "[REDACTED]";

    private static readonly Regex PrivateKeyBlockRegex = PrivateKeyBlock();
    private static readonly Regex BasicAuthRegex = BasicAuth();
    private static readonly Regex UrlUserInfoRegex = UrlUserInfo();
    private static readonly Regex ProviderTokenRegex = ProviderToken();
    private static readonly Regex CredentialAssignmentRegex = CredentialAssignment();

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

        string redacted = _inner.Redact(commandText).RedactedText;

        redacted = PrivateKeyBlockRegex.Replace(redacted, static m => m.Groups[2].Success
            ? $"{m.Groups[1].Value}\n{Redacted}\n{m.Groups[2].Value}"
            : $"{m.Groups[1].Value}\n{Redacted}");
        redacted = BasicAuthRegex.Replace(redacted, "$1" + Redacted);
        redacted = UrlUserInfoRegex.Replace(redacted, "$1" + Redacted + "@");
        redacted = ProviderTokenRegex.Replace(redacted, Redacted);
        redacted = CredentialAssignmentRegex.Replace(redacted, "$1" + Redacted);

        return new RedactionResult(redacted, !string.Equals(commandText, redacted, StringComparison.Ordinal));
    }

    // "-----BEGIN ... PRIVATE KEY-----" through the matching END line, or to the end of the text
    // when the screen cut the block off. The body is matched lazily so two blocks on one screen
    // are redacted separately.
    [GeneratedRegex(@"(-----BEGIN [A-Z ]*PRIVATE KEY-----)\r?\n[\s\S]*?(?:\r?\n(-----END [A-Z ]*PRIVATE KEY-----)|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    // Sibling of the inner filter's "Authorization: Bearer" pattern.
    [GeneratedRegex(@"(Authorization:\s+Basic\s+)(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BasicAuth();

    // user:pass@host and TOKEN@host after a scheme: git remotes, connection URLs.
    [GeneratedRegex(@"(://)[^/\s@:]+(?::[^/\s@]*)?@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    // Provider-specific token shapes, case-sensitive by design: GitHub, GitLab, Slack, AWS access
    // key ids, OpenAI/Anthropic-style "sk-", TypeSafe "apikey_", bare JWTs.
    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,}|apikey_[A-Za-z0-9_]{20,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProviderToken();

    // NAME=value, NAME: value, "name": "value", name = value — where the name carries a
    // credential keyword. Bare "auth" is excluded on purpose ("Author:" in git log, and the
    // "Authorization:" header, which its own patterns handle); so is "pwd" (printenv's PWD=).
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.\-])(?!authorization\s*:)(""?'?[A-Za-z0-9_.\-]*(?:secret|token|passw(?:or)?d|api[_\-]?key|access[_\-]?key|private[_\-]?key|client[_\-]?secret|auth[_\-]?token)[A-Za-z0-9_.\-]*""?'?\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignment();
}
