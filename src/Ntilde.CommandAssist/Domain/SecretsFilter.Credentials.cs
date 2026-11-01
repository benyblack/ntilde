using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ntilde.CommandAssist.Domain;

public sealed partial class SecretsFilter
{
    private const string CredentialFlags = "(?:password|token|api-key|api_key|apikey|access-token|auth-token|client-secret|secret-key)";
    private const string CredentialOption = "(?:--" + CredentialFlags + "|-password)";
    private const string CredentialNames = "(?:GITHUB_TOKEN|GH_TOKEN|GITLAB_TOKEN|GITLAB_PRIVATE_TOKEN|CI_JOB_TOKEN|NPM_TOKEN|" +
        "OPENAI_API_KEY|ANTHROPIC_API_KEY|AZURE_OPENAI_API_KEY|GOOGLE_API_KEY|GEMINI_API_KEY|HF_TOKEN|HUGGING_FACE_HUB_TOKEN|" +
        "AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY|AWS_SESSION_TOKEN|API_KEY|ACCESS_TOKEN|AUTH_TOKEN|DOCKER_PASSWORD|DATABASE_PASSWORD|JWT)";
    private const int MaxCredentialNesting = 32;
    private static readonly Regex CredentialPrefixRegex = CredentialPrefix();

    [GeneratedRegex(@"(?<![\w-])(?:sshpass[ \t]+-p[ \t]+|(?<argQuote>[""'])(?:" + CredentialOption + "|" + CredentialNames + @")=|" +
        @"(?<cmdQuoted>set[ \t]+"")" + CredentialNames + @"[ \t]*=|" +
        @"(?<cmd>set[ \t]+)" + CredentialNames + @"=|" +
        @"\$env:" + CredentialNames + @"[ \t]*=[ \t]*|" + CredentialNames + @"=|" +
        @"(?:""" + CredentialOption + @"""|'" + CredentialOption + @"'|" + CredentialOption + @")(?:=|[ \t]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CredentialPrefix();

    private readonly record struct CredentialRange(int Start, int End, char? ClosingQuote);

    private static string RedactCredentialArguments(string text)
    {
        var ranges = CredentialRanges(text);
        if (ranges.Count == 0) return text;
        var output = new StringBuilder(text.Length);
        int copiedThrough = 0;
        foreach (var range in ranges)
        {
            output.Append(text, copiedThrough, range.Start - copiedThrough);
            output.Append("[REDACTED]");
            if (range.ClosingQuote.HasValue) output.Append(range.ClosingQuote.Value);
            copiedThrough = range.End;
        }
        return output.Append(text, copiedThrough, text.Length - copiedThrough).ToString();
    }

    private static List<CredentialRange> CredentialRanges(string text)
    {
        var ranges = new List<CredentialRange>();
        foreach (Match prefix in CredentialPrefixRegex.Matches(text))
        {
            int start = prefix.Index + prefix.Length;
            var (end, closingQuote) = CredentialValueEnd(text, start, prefix);
            if (end == start) continue;
            // A value may itself look like another credential option. Hide the union of both values.
            if (ranges.Count > 0 && prefix.Index < ranges[^1].End)
            {
                var previous = ranges[^1];
                if (end > previous.End) ranges[^1] = previous with { End = end };
            }
            else ranges.Add(new CredentialRange(start, end, closingQuote));
        }
        return ranges;
    }

    private static (int End, char? ClosingQuote) CredentialValueEnd(string text, int start, Match prefix)
    {
        string argumentQuote = prefix.Groups["argQuote"].Value;
        bool quotedValue = argumentQuote.Length > 0 || prefix.Groups["cmdQuoted"].Success;
        if (!quotedValue) return (CredentialWordEnd(text, start, prefix.Groups["cmd"].Success), null);
        char quote = argumentQuote.Length > 0 ? argumentQuote[0] : '"';
        int quotedEnd = CredentialQuotedEnd(text, start, quote, 0);
        // Whole-argument quotes can be followed by more fragments of the same shell word.
        int end = argumentQuote.Length > 0 ? CredentialWordEnd(text, quotedEnd, cmdAssignment: false) : quotedEnd;
        char? closingQuote = quotedEnd > start && text[quotedEnd - 1] == quote ? quote : null;
        return (end, closingQuote);
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
        // These escape markers have incompatible meanings across supported shells.
        if (c is '\\' or '`' or '^') return text.Length;
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
            if (IsDoubledCredentialQuote(text, index, quote)) { index += 2; continue; }
            if (c == quote) return index + 1;
            // Do not guess which shell owns an escape; none of its remaining text may leak.
            if (quote != '\'' && c is '\\' or '`' or '^') return text.Length;
            if (IsCredentialExpansion(text, index, quote))
            {
                index = CredentialGroupEnd(text, index + 2, CredentialCloser(text[index + 1]), depth + 1);
                continue;
            }
            index++;
        }
        return text.Length;
    }

    private static bool IsDoubledCredentialQuote(string text, int index, char quote) =>
        text[index] == quote && index + 1 < text.Length && text[index + 1] == quote;

    private static bool IsCredentialExpansion(string text, int index, char quote) =>
        quote == '"' && text[index] == '$' && index + 1 < text.Length && text[index + 1] is '(' or '{';
}
