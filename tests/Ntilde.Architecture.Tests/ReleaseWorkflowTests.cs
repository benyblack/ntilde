using System.Text.RegularExpressions;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// The order of <c>.github/workflows/release.yml</c>'s jobs (Phase 4 final review I4). The App's remote
/// installer downloads only its own version's <c>ntilde-mux-&lt;rid&gt;</c>, so an App version published
/// without them can never install <c>ntilde-mux</c> on that platform. Every job that uploads the App to the
/// release therefore waits for <c>publish_mux_daemon</c>: a failed leg there keeps the App back too.
/// </summary>
/// <remarks>
/// The workflow is parsed, not searched for spellings: its jobs are the keys indented two spaces under
/// <c>jobs:</c>, a job's <c>needs:</c> is the flow list (<c>[a, b]</c>), the block list or the single name
/// right under it, and a job uploads to the release when one of its steps uses
/// <c>softprops/action-gh-release</c>. Dependencies are followed transitively.
/// </remarks>
public sealed partial class ReleaseWorkflowTests
{
    private const string MuxDaemonJob = "publish_mux_daemon";

    /// <summary>Creates the release that every other upload, ntilde-mux's included, goes into: it cannot wait for them.</summary>
    private const string CreateReleaseJob = "create_release";

    [Fact]
    public void Every_job_that_uploads_the_app_waits_for_ntilde_mux()
    {
        Dictionary<string, Job> jobs = ParseJobs(File.ReadAllLines(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml")));
        Assert.Contains(MuxDaemonJob, jobs.Keys);
        Assert.Contains(CreateReleaseJob, jobs.Keys);

        string[] uploaders = jobs.Values
            .Where(j => j.UploadsToRelease && j.Name is not (MuxDaemonJob or CreateReleaseJob))
            .Select(j => j.Name)
            .ToArray();
        Assert.NotEmpty(uploaders);

        string[] ungated = uploaders.Where(name => !DependsOn(jobs, name, MuxDaemonJob)).ToArray();
        Assert.True(ungated.Length == 0,
            $"These release.yml jobs upload the App to the release without waiting for {MuxDaemonJob}: {string.Join(", ", ungated)}. " +
            "A failed ntilde-mux leg would then ship an App version that can never install ntilde-mux on that platform.");
    }

    private sealed record Job(string Name, IReadOnlyList<string> Needs, bool UploadsToRelease);

    private static bool DependsOn(Dictionary<string, Job> jobs, string job, string dependency)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(jobs[job].Needs);
        while (pending.TryPop(out string? next))
        {
            if (next == dependency) return true;
            if (!seen.Add(next) || !jobs.TryGetValue(next, out Job? found)) continue;
            foreach (string need in found.Needs) pending.Push(need);
        }

        return false;
    }

    private static Dictionary<string, Job> ParseJobs(string[] lines)
    {
        var jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
        int start = Array.FindIndex(lines, l => l.TrimEnd() == "jobs:");
        Assert.True(start >= 0, "release.yml has no top-level jobs: key");

        string? name = null;
        var body = new List<string>();
        for (int i = start + 1; i <= lines.Length; i++)
        {
            string? line = i < lines.Length ? lines[i] : null;
            bool newTopLevel = line is not null && line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#');
            Match key = line is null ? Match.Empty : JobKey().Match(line);
            if (line is null || newTopLevel || key.Success)
            {
                if (name is not null) jobs[name] = new Job(name, ParseNeeds(body), body.Any(l => l.TrimStart().StartsWith("uses: softprops/action-gh-release", StringComparison.Ordinal)));
                if (line is null || newTopLevel) break;
                name = key.Groups[1].Value;
                body.Clear();
                continue;
            }

            body.Add(line);
        }

        return jobs;
    }

    /// <summary>The job's <c>needs:</c> (four spaces in): <c>[a, b]</c>, a block list under it, or one name.</summary>
    private static List<string> ParseNeeds(List<string> body)
    {
        int at = body.FindIndex(l => l.StartsWith("    needs:", StringComparison.Ordinal));
        if (at < 0) return [];

        string value = StripComment(body[at]["    needs:".Length..]).Trim();
        if (value.StartsWith('['))
        {
            Assert.True(value.EndsWith(']'), $"a needs: list that spans lines is not parsed here: {body[at]}");
            return value[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        if (value.Length > 0) return [value];

        var needs = new List<string>();
        for (int i = at + 1; i < body.Count && body[i].StartsWith("      - ", StringComparison.Ordinal); i++)
        {
            needs.Add(StripComment(body[i]["      - ".Length..]).Trim());
        }

        return needs;
    }

    private static string StripComment(string value)
    {
        int hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash < 0 ? value : value[..hash];
    }

    [GeneratedRegex(@"^  ([A-Za-z0-9_-]+):\s*(#.*)?$")]
    private static partial Regex JobKey();

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

        throw new DirectoryNotFoundException("Could not locate the repository root (Ntilde.sln).");
    }
}
