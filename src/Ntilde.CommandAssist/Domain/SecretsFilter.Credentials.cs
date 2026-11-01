using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Ntilde.CommandAssist.Domain;

public sealed partial class SecretsFilter
{
    private const string CredentialFlags = "(?:password|token|api-key|api_key|apikey|access-token|auth-token|client-secret|secret-key)";
    private const string CredentialNames = "(?:GITHUB_TOKEN|GH_TOKEN|GITLAB_TOKEN|GITLAB_PRIVATE_TOKEN|CI_JOB_TOKEN|NPM_TOKEN|" +
        "OPENAI_API_KEY|ANTHROPIC_API_KEY|AZURE_OPENAI_API_KEY|GOOGLE_API_KEY|GEMINI_API_KEY|HF_TOKEN|HUGGING_FACE_HUB_TOKEN|" +
        "AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY|AWS_SESSION_TOKEN|API_KEY|ACCESS_TOKEN|AUTH_TOKEN|DOCKER_PASSWORD|DATABASE_PASSWORD|JWT)";
    private const int MaxCredentialNesting = 32;
    private static readonly Regex CredentialPrefixRegex = CredentialPrefix();

    [GeneratedRegex(@"(?<![\w-])(?:sshpass[ \t]+-p[ \t]+|(?<cmdQuoted>set[ \t]+"")" + CredentialNames + @"[ \t]*=|" +
        @"(?<cmd>set[ \t]+)" + CredentialNames + @"=|" +
        @"\$env:" + CredentialNames + @"[ \t]*=[ \t]*|" + CredentialNames + @"=|" +
        @"(?:""--" + CredentialFlags + @"""|'--" + CredentialFlags + @"'|--" + CredentialFlags + @")(?:=|[ \t]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CredentialPrefix();

    private static string RedactCredentialArguments(string text)
    {
        StringBuilder? output = null;
        int copiedThrough = 0;
        foreach (Match prefix in CredentialPrefixRegex.Matches(text))
        {
            if (prefix.Index < copiedThrough) continue;
            int start = prefix.Index + prefix.Length;
            bool quotedAssignment = prefix.Groups["cmdQuoted"].Success;
            int end = quotedAssignment ? CredentialQuotedEnd(text, start, '"', 0)
                : CredentialWordEnd(text, start, prefix.Groups["cmd"].Success);
            if (end == start) continue;
            output ??= new StringBuilder(text.Length);
            output.Append(text, copiedThrough, start - copiedThrough);
            output.Append("[REDACTED]");
            if (quotedAssignment && end > start && text[end - 1] == '"') output.Append('"');
            copiedThrough = end;
        }
        return output is null ? text : output.Append(text, copiedThrough, text.Length - copiedThrough).ToString();
    }

    private static bool CredentialBoundary(char c, bool cmdAssignment) =>
        c is '\r' or '\n' or '&' or '|' or '<' or '>' ||
        (!cmdAssignment && c is ' ' or '\t' or ';');

    private static int CredentialWordEnd(string text, int start, bool cmdAssignment)
    {
        int index = start;
        while (index < text.Length && !CredentialBoundary(text[index], cmdAssignment))
            index = CredentialSyntaxEnd(text, index, 0);
        return index;
    }

    private static int CredentialSyntaxEnd(string text, int index, int depth)
    {
        if (depth >= MaxCredentialNesting) return text.Length;
        char c = text[index];
        if (c is '\'' or '"') return CredentialQuotedEnd(text, index + 1, c, depth + 1);
        if (c is '(' or '{' or '[') return CredentialGroupEnd(text, index + 1, CredentialCloser(c), depth + 1);
        if (c == '`' && text.IndexOf('`', index + 1) >= 0) return CredentialQuotedEnd(text, index + 1, '`', depth + 1);
        if (c is '\\' or '`' or '^')
        {
            // Ambiguous continued commands are hidden through the end, rather than leaking a following word.
            if (index + 1 < text.Length && text[index + 1] is '\r' or '\n') return text.Length;
            return Math.Min(text.Length, index + 2);
        }
        return index + 1;
    }

    private static char CredentialCloser(char opener) => opener switch { '(' => ')', '{' => '}', _ => ']' };

    private static int CredentialGroupEnd(string text, int start, char closer, int depth)
    {
        int index = start;
        while (index < text.Length)
        {
            if (text[index] == closer) return index + 1;
            index = CredentialSyntaxEnd(text, index, depth);
        }
        // An unclosed group consumes the remaining command conservatively.
        return text.Length;
    }

    private static int CredentialQuotedEnd(string text, int start, char quote, int depth)
    {
        if (depth >= MaxCredentialNesting) return text.Length;
        int index = start;
        while (index < text.Length)
        {
            char c = text[index];
            if (c == quote)
            {
                if (quote != '`' && index + 1 < text.Length && text[index + 1] == quote) { index += 2; continue; }
                return index + 1;
            }
            if (quote != '\'' && c is '\\' or '`' or '^') { index = Math.Min(text.Length, index + 2); continue; }
            if (IsCredentialExpansion(text, index, quote))
            {
                index = CredentialGroupEnd(text, index + 2, CredentialCloser(text[index + 1]), depth + 1);
                continue;
            }
            index++;
        }
        return text.Length;
    }

    private static bool IsCredentialExpansion(string text, int index, char quote) =>
        quote == '"' && text[index] == '$' && index + 1 < text.Length && text[index + 1] is '(' or '{';
}
