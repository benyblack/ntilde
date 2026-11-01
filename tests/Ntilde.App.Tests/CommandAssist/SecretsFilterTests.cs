using Ntilde.CommandAssist.Domain;

namespace Ntilde.Tests.CommandAssist;

public sealed class SecretsFilterTests
{
    [Theory]
    [InlineData("gh auth login --password hunter2", "gh auth login --password [REDACTED]")]
    [InlineData("curl https://example.test?token=abc123", "curl https://example.test?token=[REDACTED]")]
    [InlineData("sshpass -p hunter2 ssh user@example.com", "sshpass -p [REDACTED] ssh user@example.com")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer [REDACTED]")]
    [InlineData("export JWT=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.abc.def", "export JWT=[REDACTED]")]
    [InlineData("Server=db;User Id=app;Password=supersecret;", "Server=db;User Id=app;Password=[REDACTED];")]
    public void Redact_WhenCommandContainsKnownSecretPatterns_RedactsSensitiveSegments(string input, string expected)
    {
        var filter = new SecretsFilter();

        RedactionResult result = filter.Redact(input);

        Assert.True(result.WasRedacted);
        Assert.Equal(expected, result.RedactedText);
    }

    [Theory]
    [InlineData("tool --token synth677 --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --token=synth677 --verbose", "tool --token=[REDACTED] --verbose")]
    [InlineData("tool --api-key 'synth 677' --verbose", "tool --api-key [REDACTED] --verbose")]
    [InlineData("tool --api_key=\"synth 677\" --verbose", "tool --api_key=[REDACTED] --verbose")]
    [InlineData("tool --apikey=synth677", "tool --apikey=[REDACTED]")]
    [InlineData("tool --access-token synth677", "tool --access-token [REDACTED]")]
    [InlineData("tool --auth-token synth677", "tool --auth-token [REDACTED]")]
    [InlineData("tool --client-secret synth677", "tool --client-secret [REDACTED]")]
    [InlineData("tool --secret-key synth677", "tool --secret-key [REDACTED]")]
    [InlineData("tool '--token' 'synth677' --help", "tool '--token' [REDACTED] --help")]
    [InlineData("GITHUB_TOKEN=synth677 gh repo list", "GITHUB_TOKEN=[REDACTED] gh repo list")]
    [InlineData("export OPENAI_API_KEY='synth 677'", "export OPENAI_API_KEY=[REDACTED]")]
    [InlineData("ANTHROPIC_API_KEY=\"synth677\" tool", "ANTHROPIC_API_KEY=[REDACTED] tool")]
    [InlineData("AWS_SECRET_ACCESS_KEY=synth677 aws s3 ls", "AWS_SECRET_ACCESS_KEY=[REDACTED] aws s3 ls")]
    [InlineData("AWS_SESSION_TOKEN=synth677 aws s3 ls", "AWS_SESSION_TOKEN=[REDACTED] aws s3 ls")]
    [InlineData("$env:GITHUB_TOKEN = 'synth 677'; gh repo list", "$env:GITHUB_TOKEN = [REDACTED]; gh repo list")]
    [InlineData("set GITHUB_TOKEN=synth 677&&gh repo list", "set GITHUB_TOKEN=[REDACTED]&&gh repo list")]
    [InlineData("set \"GITHUB_TOKEN=synth 677\" && gh repo list", "set \"GITHUB_TOKEN=[REDACTED]\" && gh repo list")]
    [InlineData("GH_TOKEN=synth677 NPM_TOKEN=synth677 tool --token=synth677", "GH_TOKEN=[REDACTED] NPM_TOKEN=[REDACTED] tool --token=[REDACTED]")]
    [InlineData("tool --token 'synth'\"677\" --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --token synth\\ 677 --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --token synth` 677 --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --token \"synth\\\"677\" --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --token 'synth''677' --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --token $(printf 'synth 677') --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --token 'unterminated synth677", "tool --token [REDACTED]")]
    [InlineData("tool --token synth\u00a0677 --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool --password=synth677 --verbose", "tool --password=[REDACTED] --verbose")]
    [InlineData("tool --password synth\u00a0677 --verbose", "tool --password [REDACTED] --verbose")]
    [InlineData("sshpass -p synth\u00a0677 ssh host", "sshpass -p [REDACTED] ssh host")]
    [InlineData("Authorization: Bearer abc+def/ghi~jkl==", "Authorization: Bearer [REDACTED]")]
    [InlineData("tool --token \"`printf '%s' \"synth 677\"`\" --verbose", "tool --token [REDACTED] --verbose")]
    [InlineData("tool \"--token=synth 677\" --verbose", "tool \"--token=[REDACTED]\" --verbose")]
    [InlineData("tool '--token=synth 677' --verbose", "tool '--token=[REDACTED]' --verbose")]
    [InlineData("env \"GITHUB_TOKEN=synth 677\" tool", "env \"GITHUB_TOKEN=[REDACTED]\" tool")]
    [InlineData("tool \"--token=synth \"677 --verbose", "tool \"--token=[REDACTED]\" --verbose")]
    public void Redact_SupportedCredentials_RemovesEntireValueAndPreservesArguments(string command, string expected)
    {
        RedactionResult result = new SecretsFilter().Redact(command);
        Assert.True(result.WasRedacted);
        Assert.Equal(expected, result.RedactedText);
        Assert.Equal(expected, new SecretsFilter().Redact(expected).RedactedText);
    }

    [Theory]
    [InlineData("tool --token-count 10 --api-keyring file")]
    [InlineData("GITHUB_TOKEN_FILE=/tmp/token tool")]
    [InlineData("UNKNOWN_VENDOR_TOKEN=not-supported tool")]
    [InlineData("GITHUB_TOKEN= gh repo list")]
    [InlineData("tool --token")]
    public void Redact_UnrelatedOrEmptyValues_AreUnchanged(string command)
    {
        RedactionResult result = new SecretsFilter().Redact(command);
        Assert.False(result.WasRedacted);
        Assert.Equal(command, result.RedactedText);
    }

    public static TheoryData<string> CredentialEnvironmentNames => new(
        "GITHUB_TOKEN", "GH_TOKEN", "GITLAB_TOKEN", "GITLAB_PRIVATE_TOKEN", "CI_JOB_TOKEN", "NPM_TOKEN",
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "AZURE_OPENAI_API_KEY", "GOOGLE_API_KEY", "GEMINI_API_KEY",
        "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN",
        "API_KEY", "ACCESS_TOKEN", "AUTH_TOKEN", "DOCKER_PASSWORD", "DATABASE_PASSWORD", "JWT");

    [Theory]
    [MemberData(nameof(CredentialEnvironmentNames))]
    public void Redact_EachDocumentedEnvironmentName_WorksInSupportedShellAssignments(string name)
    {
        var filter = new SecretsFilter();
        foreach (var (input, expected) in new[]
        {
            ($"{name}=synth677 tool", $"{name}=[REDACTED] tool"),
            ($"$env:{name} = 'synth 677'; tool", $"$env:{name} = [REDACTED]; tool"),
            ($"set \"{name}=synth 677\" && tool", $"set \"{name}=[REDACTED]\" && tool")
        })
        {
            RedactionResult result = filter.Redact(input);
            Assert.True(result.WasRedacted);
            Assert.Equal(expected, result.RedactedText);
        }
    }

    [Fact]
    public void Redact_WhenCommandDoesNotContainSecrets_ReturnsOriginalText()
    {
        var filter = new SecretsFilter();

        RedactionResult result = filter.Redact("git status");

        Assert.False(result.WasRedacted);
        Assert.Equal("git status", result.RedactedText);
    }
}
