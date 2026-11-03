using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// A test that waits a few seconds for something that must happen is a guess about the runner's scheduler, not a
/// claim about the code. CI runs these suites on 4-core runners with every test class in parallel, and a thread-pool
/// item or a dedicated thread can go seconds without CPU time there. That is how
/// NativeSshSessionTests.RemoteForward_IsRequestedWhenTheConnectedEventArrives (#702) and then
/// NativeSshTerminalParityTests.ChunkedAlternateScreenSequences_DoNotPolluteScrollback (#728) failed: each gave a
/// working session 2 s and lost the race. Fixing them one failure at a time left the rest of the pattern in place.
/// <para>
/// So a wait under five seconds needs a reason. A wait for something that must happen gets a long ceiling instead: it
/// returns the moment the condition holds, so only a broken run pays for it. A wait that is short by design, such as
/// a negative check that gives a wrong early return time to happen, or a deliberate hold, says so with a
/// "Short on purpose:" comment on its line or the line above.
/// </para>
/// </summary>
public class ShortTestWaitTests
{
    private const string Marker = "Short on purpose:";

    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(5);

    // Wait, WaitAsync, WaitOne or Join, given a TimeSpan.FromSeconds/FromMilliseconds literal or a bare millisecond
    // count. A timeout of 0 is a non-blocking poll, not a wait, and is left alone.
    private static readonly Regex TimedWait = new(
        @"\.(?:Wait|WaitAsync|WaitOne|Join)\(\s*(?:TimeSpan\.From(?<unit>Seconds|Milliseconds)\(\s*(?<value>\d+(?:\.\d+)?)\s*\)|(?<ms>\d+)\s*[,)])",
        RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    internal static TimeSpan? ShortWaitIn(string line)
    {
        foreach (Match match in TimedWait.Matches(line))
        {
            TimeSpan timeout = match.Groups["ms"].Success
                ? TimeSpan.FromMilliseconds(double.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture))
                : match.Groups["unit"].Value == "Seconds"
                    ? TimeSpan.FromSeconds(double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture))
                    : TimeSpan.FromMilliseconds(double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture));
            if (timeout > TimeSpan.Zero && timeout < Floor)
            {
                return timeout;
            }
        }

        return null;
    }

    [Theory]
    [InlineData("await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);", true)]
    [InlineData("Assert.True(started.Wait(TimeSpan.FromMilliseconds(500)));", true)]
    [InlineData("Assert.False(dispose.Wait(300, ct));", true)]
    [InlineData("Assert.False(reader.Join(200));", true)]
    [InlineData("handle.WaitOne(TimeSpan.FromSeconds(1.5));", true)]
    [InlineData("await exit.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);", false)]
    [InlineData("Assert.True(started.Wait(TimeSpan.FromSeconds(5)));", false)]
    [InlineData("while (started.Wait(0, ct)) { }", false)]
    [InlineData("await exit.Task.WaitAsync(Patient, ct);", false)]
    [InlineData("await Task.Delay(TimeSpan.FromMilliseconds(20));", false)]
    public void The_scan_recognises_short_waits(string line, bool isShort)
    {
        Assert.Equal(isShort, ShortWaitIn(line) is not null);
    }

    [Fact]
    public void Tests_do_not_wait_a_few_seconds_for_something_that_must_happen()
    {
        string tests = Path.Combine(RepoRoot(), "tests");
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string self = $"{nameof(ShortTestWaitTests)}.cs";

        List<string> offenders = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(bin, StringComparison.Ordinal) && !file.Contains(obj, StringComparison.Ordinal))
            .Where(file => !string.Equals(Path.GetFileName(file), self, StringComparison.Ordinal))
            .SelectMany(file =>
            {
                string[] lines = File.ReadAllLines(file);
                return lines
                    .Select((line, index) => (line, index))
                    .Where(entry => ShortWaitIn(entry.line) is not null)
                    .Where(entry => !entry.line.Contains(Marker, StringComparison.Ordinal)
                        && (entry.index == 0 || !lines[entry.index - 1].Contains(Marker, StringComparison.Ordinal)))
                    .Select(entry => $"{Path.GetRelativePath(tests, file)}:{entry.index + 1}");
            })
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These waits allow under 5 s. If the test waits for something that must happen, give it a long ceiling "
            + "(it returns as soon as the condition holds); if it is short by design, say why in a \"" + Marker
            + "\" comment on the line or the line above: " + string.Join(", ", offenders));
    }
}
