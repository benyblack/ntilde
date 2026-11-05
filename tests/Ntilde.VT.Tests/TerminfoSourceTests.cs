using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// The opt-in <c>xterm-ntilde</c> terminfo entry (#844): the checked-in source is exactly what
/// <see cref="TerminfoSource"/> generates from the capability table, and once compiled with
/// <c>tic -x</c>, <c>infocmp -x</c> reports the table's values and otherwise <c>xterm-256color</c>.
/// </summary>
public sealed class TerminfoSourceTests
{
    /// <summary>Set to 1 to rewrite the checked-in source from the table instead of comparing.</summary>
    private const string UpdateVariable = "NTILDE_UPDATE_TERMINFO";

    [Fact]
    public void Checked_in_entry_is_the_generated_one()
    {
        string path = Path.Combine(TerminalCapabilityTests.RepositoryRoot(), TerminfoSource.RepositoryPath);
        string generated = TerminfoSource.Generate();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
            File.WriteAllText(path, generated, new UTF8Encoding(false));
        Assert.True(File.Exists(path), $"{TerminfoSource.RepositoryPath} is missing; run with {UpdateVariable}=1.");
        Assert.True(generated == File.ReadAllText(path),
            $"{TerminfoSource.RepositoryPath} differs from the capability table; run this test with {UpdateVariable}=1 to regenerate it.");
    }

    [Fact]
    public void Entry_writes_every_capability_that_has_no_omission_reason()
    {
        string[] lines = TerminfoSource.Generate().Split('\n');
        Assert.Equal("xterm-ntilde|Ntilde terminal emulator,", lines.Single(line => line.Length > 0 && line[0] is not '#' and not '\t'));
        Assert.Equal("\tuse=xterm-256color,", lines[^2]); // use= last, so the table overrides the base
        Assert.Equal("", lines[^1]);
        foreach (TerminalCapability capability in TerminalCapabilities.All)
        {
            bool written = lines.Any(line => line.StartsWith('\t' + capability.Name, StringComparison.Ordinal)
                && line.Length > capability.Name.Length + 1 && line[capability.Name.Length + 1] is ',' or '#' or '=');
            Assert.True(written == capability.TerminfoOmission is null, capability.Name);
            if (capability.TerminfoOmission is not null) Assert.False(string.IsNullOrWhiteSpace(capability.TerminfoOmission));
            if (capability.RequiresClipboardWrite) Assert.NotNull(capability.TerminfoOmission); // host-gated: live replies only
        }
        Assert.Equal(new[] { "Co", "RGB", "Ms" }, TerminalCapabilities.All.Where(c => c.TerminfoOmission is not null).Select(c => c.Name));
    }

    [Theory]
    [InlineData("\u001b[4:%p1%dm", "\\E[4\\:%p1%dm")]
    [InlineData("\u001b]2;", "\\E]2;")]
    [InlineData("\u0007", "^G")]
    [InlineData("\u0001\u007f", "^A^?")]
    [InlineData("a,b", "a\\,b")]
    [InlineData("^\\", "\\^\\\\")]
    [InlineData("\u001b[%p1%d q", "\\E[%p1%d q")]
    public void Values_use_terminfo_escapes(string value, string expected)
    {
        Assert.Equal(expected, TerminfoSource.Escape(value));
        Assert.Equal(value, Unescape(expected));
    }

    [Fact]
    public void Non_ascii_values_are_refused()
        => Assert.Throws<ArgumentException>(() => TerminfoSource.Escape("é"));

    [Fact]
    public void Tic_compiles_the_entry_and_infocmp_reports_the_table()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "tic and infocmp are ncurses tools; Windows has no terminfo.");
        string tic = FindTool("tic");
        string infocmp = FindTool("infocmp");
        string source = Path.Combine(TerminalCapabilityTests.RepositoryRoot(), TerminfoSource.RepositoryPath);
        string database = Directory.CreateTempSubdirectory("ntilde-terminfo-").FullName;
        try
        {
            (int ticExit, _, string ticErrors) = Run(tic, "-x", "-o", database, source);
            Assert.True(ticExit == 0, $"tic -x failed: {ticErrors}");
            Assert.True(ticErrors.Trim().Length == 0, $"tic -x warned: {ticErrors}");

            Dictionary<string, string> entry = Capabilities(Infocmp(infocmp, "-A", database, TerminfoSource.EntryName));
            Dictionary<string, string> baseEntry = Capabilities(Infocmp(infocmp, TerminfoSource.BaseEntry));
            Assert.NotEmpty(baseEntry);

            var written = TerminfoSource.Capabilities.ToDictionary(c => c.Name, Expected, StringComparer.Ordinal);
            foreach ((string name, string expected) in written)
                Assert.True(entry.TryGetValue(name, out string? actual) && actual == expected, $"{name}: infocmp reports {actual ?? "nothing"}, the table {expected}");
            foreach (string name in entry.Keys.Union(baseEntry.Keys).Where(name => !written.ContainsKey(name)))
            {
                entry.TryGetValue(name, out string? actual);
                baseEntry.TryGetValue(name, out string? inherited);
                Assert.True(actual == inherited, $"{name}: {TerminfoSource.EntryName} has {actual ?? "nothing"}, {TerminfoSource.BaseEntry} {inherited ?? "nothing"}");
            }
            Assert.DoesNotContain("RGB", entry.Keys.Except(baseEntry.Keys)); // never added: ncurses would read setaf as direct color
        }
        finally
        {
            Directory.Delete(database, recursive: true);
        }
    }

    private static string Expected(TerminalCapability capability) => capability.Kind switch
    {
        TerminalCapabilityKind.Flag => "!",
        TerminalCapabilityKind.Number => "#" + capability.Value,
        _ => "=" + capability.Value,
    };

    private static string Infocmp(string infocmp, params string[] arguments)
    {
        (int exit, string output, string errors) = Run(infocmp, ["-x", "-1", .. arguments]);
        Assert.True(exit == 0, $"infocmp {string.Join(' ', arguments)} failed: {errors}");
        return output;
    }

    /// <summary>
    /// Reads <c>infocmp -1</c> output into name → <c>!</c> (flag), <c>#n</c> (decimal number) or
    /// <c>=value</c> (decoded string).
    /// </summary>
    private static Dictionary<string, string> Capabilities(string infocmpOutput)
    {
        var capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in infocmpOutput.Split('\n'))
        {
            if (raw.Length == 0 || raw[0] != '\t') continue; // comments and the name line
            string field = raw.Trim();
            Assert.EndsWith(",", field);
            field = field[..^1];
            int equals = field.IndexOf('=');
            int hash = field.IndexOf('#');
            if (equals > 0 && (hash < 0 || equals < hash))
                capabilities.Add(field[..equals], "=" + Unescape(field[(equals + 1)..]));
            else if (hash > 0)
                capabilities.Add(field[..hash], "#" + ParseNumber(field[(hash + 1)..]).ToString(CultureInfo.InvariantCulture));
            else
                capabilities.Add(field, "!");
        }
        return capabilities;
    }

    private static int ParseNumber(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : text.Length > 1 && text[0] == '0' ? Convert.ToInt32(text, 8) : int.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>Decodes terminfo source escapes (terminfo(5), "String capabilities").</summary>
    private static string Unescape(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (ch == '^' && i + 1 < value.Length)
            {
                char next = value[++i];
                decoded.Append(next == '?' ? '\u007f' : (char)(next & 0x1f));
            }
            else if (ch == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                if (next is >= '0' and <= '7')
                {
                    int end = i;
                    while (end < value.Length && end < i + 3 && value[end] is >= '0' and <= '7') end++;
                    int code = Convert.ToInt32(value[i..end], 8);
                    decoded.Append(code == 0 ? '\u0080' : (char)code);
                    i = end - 1;
                    continue;
                }
                decoded.Append(next switch
                {
                    'E' or 'e' => '\u001b',
                    'n' or 'l' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    's' => ' ',
                    'a' => '\u0007',
                    _ => next, // \^ \\ \, \: and anything else stand for themselves
                });
            }
            else
            {
                decoded.Append(ch);
            }
        }
        return decoded.ToString();
    }

    private static string FindTool(string name)
    {
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(["/usr/bin", "/bin"]).ToArray();
        string? found = directories.Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
        Assert.True(found is not null, $"{name} (ncurses) is required on this platform to validate the terminfo entry.");
        return found;
    }

    private static (int Exit, string Output, string Errors) Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), $"{fileName} timed out.");
        return (process.ExitCode, output.Result, errors.Result);
    }
}
