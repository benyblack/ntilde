using Ntilde.CommandAssist.Domain;

namespace Ntilde.Tests.CommandAssist;

/// <summary>
/// The screen-oriented layer over <see cref="SecretsFilter"/>: patterns that appear in command
/// OUTPUT (env dumps, config prints, remotes, key material, provider token shapes) rather than in a
/// typed command line. Only screen inference uses it; command history keeps the inner filter.
/// </summary>
public sealed class ScreenSecretsFilterTests
{
    private static ScreenSecretsFilter Make() => new(new SecretsFilter());

    [Theory]
    // credential-named assignments, any syntax; the name survives, the value does not
    [InlineData("export AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "export AWS_SECRET_ACCESS_KEY=[REDACTED]")]
    [InlineData("DATABASE_PASSWORD=hunter2", "DATABASE_PASSWORD=[REDACTED]")]
    [InlineData("OPENAI_API_KEY=\"sk-live-abc\"", "OPENAI_API_KEY=[REDACTED]")]
    [InlineData("  \"client_secret\": \"9f8e7d6c\",", "  \"client_secret\": [REDACTED],")]
    // escaped quotes inside a JSON string must not end the value early
    [InlineData("  \"client_secret\": \"abc\\\"sensitive-tail\",", "  \"client_secret\": [REDACTED],")]
    [InlineData("password: 'it\\'s-secret' # comment", "password: [REDACTED] # comment")]
    // YAML plain scalars run to the end of the line; a trailing comment or comma survives
    [InlineData("password: correct horse battery staple", "password: [REDACTED]")]
    [InlineData("  db_password: correct horse   # rotated weekly", "  db_password: [REDACTED]   # rotated weekly")]
    [InlineData("  api_key: abc def,", "  api_key: [REDACTED],")]
    // a '#' glued to the value is part of it (YAML); only a whitespace-separated '#' is a comment
    [InlineData("password: abc#sensitive", "password: [REDACTED]")]
    // printenv / dotenv dumps print whole values, spaces included
    [InlineData("DB_PASSWORD=correct horse battery staple", "DB_PASSWORD=[REDACTED]")]
    [InlineData("API_KEY=abc def # note", "API_KEY=[REDACTED] # note")]
    // an environment value may begin with punctuation a YAML scalar could not
    [InlineData("DB_PASSWORD=#hunter2", "DB_PASSWORD=[REDACTED]")]
    [InlineData("TOKEN=;semi,colon", "TOKEN=[REDACTED]")]
    [InlineData("token: a#b # note", "token: [REDACTED] # note")]
    // a provider token glued to a preceding word is still caught (no leading word boundary)
    [InlineData("prefixghp_abcdefghijklmnopqrstuvwxyz0123456789", "prefix[REDACTED]")]
    [InlineData("password: s3cret", "password: [REDACTED]")]
    [InlineData("GITHUB_TOKEN: 'abc'", "GITHUB_TOKEN: [REDACTED]")]
    [InlineData("AUTH_TOKEN=Bearer-ish", "AUTH_TOKEN=[REDACTED]")]
    [InlineData("aws_secret_access_key = wJalrXUtnFEMI", "aws_secret_access_key = [REDACTED]")]
    // URL userinfo
    [InlineData("origin  https://benyblack:ghp_abcdefghijklmnopqrstuvwxyz0123@github.com/x/y.git (fetch)", "origin  https://[REDACTED]@github.com/x/y.git (fetch)")]
    [InlineData("origin  https://x-access-token@github.com/x/y.git (push)", "origin  https://[REDACTED]@github.com/x/y.git (push)")]
    [InlineData("postgres://app:pa55word@db.internal:5432/prod", "postgres://[REDACTED]@db.internal:5432/prod")]
    // provider token shapes anywhere on the line
    [InlineData("token is ghp_abcdefghijklmnopqrstuvwxyz0123456789", "token is [REDACTED]")]
    [InlineData("github_pat_11ABCDEFG0123456789abcdefghijklmnopqrstuvwxyz", "[REDACTED]")]
    [InlineData("glpat-abcdefghijklmnopqrstuv", "[REDACTED]")]
    [InlineData("slack xoxb-1234567890-abcdefghij", "slack [REDACTED]")]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE", "aws_access_key_id = [REDACTED]")]
    [InlineData("using sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "using [REDACTED]")]
    [InlineData("key apikey_21831533b0403b9f4f8eada3319c5dca2dcb_49bba698", "key [REDACTED]")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c", "jwt [REDACTED]")]
    // basic auth header, sibling of the inner filter's bearer pattern
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: Basic [REDACTED]")]
    public void Redact_ScreenPatterns_RedactsValueAndKeepsName(string input, string expected)
    {
        RedactionResult result = Make().Redact(input);

        Assert.True(result.WasRedacted);
        Assert.Equal(expected, result.RedactedText);
    }

    [Fact]
    public void Redact_PrivateKeyBlock_RedactsTheWholeBlock()
    {
        const string input =
            "$ cat id_ed25519\n" +
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" +
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\n" +
            "QyNTUxOQAAACBn3tqhRrJ6w4tS1gA0Y0GJZ0lXt5h2Y5Y5c9k0m2yc4wAAAJgq2Ckg\n" +
            "-----END OPENSSH PRIVATE KEY-----\n" +
            "$ ";

        RedactionResult result = Make().Redact(input);

        Assert.True(result.WasRedacted);
        Assert.Equal("$ cat id_ed25519\n-----BEGIN OPENSSH PRIVATE KEY-----\n[REDACTED]\n-----END OPENSSH PRIVATE KEY-----\n$ ", result.RedactedText);
    }

    [Fact]
    public void Redact_PgpPrivateKeyBlock_IsRedacted()
    {
        const string input =
            "-----BEGIN PGP PRIVATE KEY BLOCK-----\n" +
            "\n" +
            "lQdGBGXo1a0BEADFj3Q1ZkMSk2e5o1Bq0m3uTz6Wm5bFf2cFcfZfOTn4l9M0u6xI\n" +
            "-----END PGP PRIVATE KEY BLOCK-----\n" +
            "$ ";

        RedactionResult result = Make().Redact(input);

        Assert.Equal("-----BEGIN PGP PRIVATE KEY BLOCK-----\n[REDACTED]\n-----END PGP PRIVATE KEY BLOCK-----\n$ ", result.RedactedText);
    }

    [Fact]
    public void Redact_IndentedPrivateKeyBlockAndOrphanTail_AreRedacted()
    {
        // A key printed from a config file or a log keeps its indentation on every line.
        RedactionResult block = Make().Redact(
            "  key: |\n" +
            "    -----BEGIN RSA PRIVATE KEY-----\n" +
            "    MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfnQm2F5sT1Ggvz\n" +
            "    -----END RSA PRIVATE KEY-----\n" +
            "$ ");
        Assert.Equal("  key: |\n    -----BEGIN RSA PRIVATE KEY-----\n[REDACTED]\n    -----END RSA PRIVATE KEY-----\n$ ", block.RedactedText);

        RedactionResult tail = Make().Redact(
            "    MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfnQm2F5sT1Ggvz\n" +
            "    AQ==\n" +
            "    -----END RSA PRIVATE KEY-----\n" +
            "$ ");
        Assert.Equal("[REDACTED]\n    -----END RSA PRIVATE KEY-----\n$ ", tail.RedactedText);
    }

    [Fact]
    public void Redact_OrphanPgpTail_IsRedacted()
    {
        RedactionResult result = Make().Redact("lQdGBGXo1a0BEADFj3Q1ZkMSk2e5o1Bq0m3uTz6Wm5bFf2cFcfZfOTn4l9M0u6xI\n=abcd\n-----END PGP PRIVATE KEY BLOCK-----");

        Assert.Equal("[REDACTED]\n-----END PGP PRIVATE KEY BLOCK-----", result.RedactedText);
    }

    [Fact]
    public void Redact_PrivateKeyBlockCutOffByScreenEdge_RedactsToEndOfText()
    {
        const string input = "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn\nQm2F5sT1Ggvz";

        RedactionResult result = Make().Redact(input);

        Assert.True(result.WasRedacted);
        Assert.Equal("-----BEGIN RSA PRIVATE KEY-----\n[REDACTED]", result.RedactedText);
    }

    [Fact]
    public void Redact_PrivateKeyTailWhoseBeginScrolledAway_RedactsTheVisibleBody()
    {
        const string input =
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\n" +
            "QyNTUxOQAAACBn3tqhRrJ6w4tS1gA0Y0GJZ0lXt5h2Y5Y5c9k0m2yc4wAAAJgq2Ckg\n" +
            "AAAAECg==\n" +
            "-----END OPENSSH PRIVATE KEY-----\n" +
            "$ ";

        RedactionResult result = Make().Redact(input);

        Assert.True(result.WasRedacted);
        Assert.Equal("[REDACTED]\n-----END OPENSSH PRIVATE KEY-----\n$ ", result.RedactedText);
    }

    [Fact]
    public void Redact_LoneShortPrivateKeyTail_IsRedacted()
    {
        // PEM's last base64 row can be as short as "AQ=="; on a small viewport it may be the only
        // body row left above the END marker.
        RedactionResult result = Make().Redact("AQ==\n-----END RSA PRIVATE KEY-----\n$ ");

        Assert.True(result.WasRedacted);
        Assert.Equal("[REDACTED]\n-----END RSA PRIVATE KEY-----\n$ ", result.RedactedText);
    }

    [Fact]
    public void Redact_EndMarkerWithNoBodyAbove_IsLeftAlone()
    {
        RedactionResult result = Make().Redact("$ cat key.pem | tail -1\n-----END RSA PRIVATE KEY-----\n$ ");

        Assert.False(result.WasRedacted);
    }

    [Fact]
    public void Redact_OrphanEndMarker_DoesNotEatTheProseAboveTheKeyBody()
    {
        const string input = "$ cat key.pem\nMIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn\n-----END RSA PRIVATE KEY-----";

        RedactionResult result = Make().Redact(input);

        Assert.Equal("$ cat key.pem\n[REDACTED]\n-----END RSA PRIVATE KEY-----", result.RedactedText);
    }

    [Fact]
    public void Redact_InnerPasswordPattern_IsBoundedToItsOwnScreenLine()
    {
        // The history filter's connection-string pattern is Password=[^;]+ which, over a whole
        // screen, would swallow every row after a printenv PASSWORD= line up to the next ';'.
        const string input = "PASSWORD=hunter2\nuser@host:~$ ls\nnotes.txt; todo.md\nuser@host:~$ ";

        RedactionResult result = Make().Redact(input);

        Assert.Equal("PASSWORD=[REDACTED]\nuser@host:~$ ls\nnotes.txt; todo.md\nuser@host:~$ ", result.RedactedText);
    }

    [Theory]
    [InlineData("commit 3b80c45f0e9a1d2c4b6e8f7a9c1d3e5f7a9b1c3d\nAuthor: someone")]
    [InlineData("-rw-r--r-- 1 u u   12 Sep 17 10:00 notes.txt")]
    [InlineData("export PATH=/usr/local/bin:$PATH")]
    [InlineData("HOME=/home/user")]
    [InlineData("user@host:~/pipeline$ ")]
    [InlineData("npm error code ERESOLVE")]
    [InlineData("sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("https://github.com/benyblack/ntilde.git")]
    public void Redact_OrdinaryOutput_IsLeftAlone(string input)
    {
        RedactionResult result = Make().Redact(input);

        Assert.False(result.WasRedacted);
        Assert.Equal(input, result.RedactedText);
    }

    [Fact]
    public void Redact_RunsTheInnerFilterFirst()
    {
        RedactionResult result = Make().Redact("gh auth login --password hunter2 && export X_TOKEN=abc");

        Assert.True(result.WasRedacted);
        Assert.Equal("gh auth login --password [REDACTED] && export X_TOKEN=[REDACTED]", result.RedactedText);
    }

    [Fact]
    public void Redact_EmptyOrWhitespace_ReturnsInputUnchanged()
    {
        var filter = Make();

        Assert.False(filter.Redact("").WasRedacted);
        Assert.False(filter.Redact("   \n  ").WasRedacted);
        Assert.Equal("   \n  ", filter.Redact("   \n  ").RedactedText);
    }

    [Fact]
    public void Redact_PreservesLineCountForSingleLinePatterns()
    {
        const string input = "line one\nSECRET_KEY=abc\nline three";

        RedactionResult result = Make().Redact(input);

        Assert.Equal(3, result.RedactedText.Split('\n').Length);
        Assert.Equal("line one\nSECRET_KEY=[REDACTED]\nline three", result.RedactedText);
    }
}
