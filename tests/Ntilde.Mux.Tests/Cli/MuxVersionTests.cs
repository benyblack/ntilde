using System.Runtime.InteropServices;
using System.Text.Json;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Cli;

/// <summary>
/// <c>ntilde-mux --version [--json]</c> (Phase 4 spec §12.1): what the GUI's installer reads to decide
/// whether a remote host's binary can serve it.
/// </summary>
public sealed class MuxVersionTests
{
    private const string Version = "1.2.3-test";

    private static MuxCliHost Host(MuxCliVerbs verbs = MuxCliVerbs.Serve | MuxCliVerbs.Ls | MuxCliVerbs.Proxy | MuxCliVerbs.Version) => new()
    {
        // Never touched: --version reads no daemon state.
        Paths = new MuxPaths(Path.Combine(Path.GetTempPath(), "nmxv-unused")),
        UsagePrefix = "ntilde-mux",
        ServeArguments = ["serve"],
        SessionFactory = () => new ScriptedSessionFactory(),
        Verbs = verbs,
        Version = Version,
    };

    private static (int Code, string Out, string Err) Run(MuxCliHost host, params string[] verbArgs)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = MuxCli.Execute(verbArgs, o, e, host);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Version_json_has_the_protocol_range()
    {
        var (code, output, err) = Run(Host(), "--version", "--json");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, err);
        Assert.EndsWith(Environment.NewLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', output.TrimEnd());   // one line
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(["version", "protocolMin", "protocolMax", "rid", "path"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Version, root.GetProperty("version").GetString());
        Assert.Equal(MuxProtocol.MinSupportedVersion, root.GetProperty("protocolMin").GetInt32());
        Assert.Equal(MuxProtocol.MaxSupportedVersion, root.GetProperty("protocolMax").GetInt32());
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, root.GetProperty("rid").GetString());
        Assert.Equal(Environment.ProcessPath ?? string.Empty, root.GetProperty("path").GetString());

        // How the App's installer reads it back.
        Assert.Equal(
            new MuxVersionInfo(Version, MuxProtocol.MinSupportedVersion, MuxProtocol.MaxSupportedVersion, RuntimeInformation.RuntimeIdentifier, Environment.ProcessPath ?? string.Empty),
            JsonSerializer.Deserialize(output, MuxCliJsonContext.Default.MuxVersionInfo));
    }

    [Fact]
    public void Plain_version_is_one_line()
    {
        var (code, output, err) = Run(Host(), "--version");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, err);
        Assert.Equal(
            $"ntilde-mux {Version} (protocol {MuxProtocol.MinSupportedVersion}-{MuxProtocol.MaxSupportedVersion}, {RuntimeInformation.RuntimeIdentifier}){Environment.NewLine}",
            output);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--VERSION")]   // verbs match in any case
    public void Every_spelling_of_the_verb_answers_the_same(string verb) =>
        Assert.Equal(Run(Host(), "--version").Out, Run(Host(), verb).Out);

    [Fact]
    public void An_unknown_option_is_a_usage_error()
    {
        var (code, output, err) = Run(Host(), "--version", "--yaml");

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("ntilde-mux --version [--json]", err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_that_does_not_offer_it_does_not_answer_it()
    {
        var (code, output, _) = Run(Host(MuxCliVerbs.Ls), "--version");

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
    }
}
