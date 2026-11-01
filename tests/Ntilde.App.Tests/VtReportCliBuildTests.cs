using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ntilde.Tests;

/// <summary>
/// What a real <c>dotnet build</c> of the App and the CLI produces: the binaries answer <c>--vt-report</c>, and the
/// App's output carries the CLI shim as a named sidecar. Moved out of <see cref="VtReportCliTests"/> because these
/// drive the SDK on the source tree rather than run code in-process.
/// </summary>
/// <remarks>
/// <para>
/// <c>Category=SdkBuild</c>, which CI runs in the build job, not in the Unit Tests shards. A shard has only the test
/// assemblies the build job uploaded - no obj/ trees and no package cache - so each of these used to restore the NuGet
/// cache and compile the App and every project it references from scratch: about 76 s of one shard's test step on
/// windows, plus the ~35 s of restore setup only that shard needed. The build job has just built the whole solution
/// with the same <c>dotnet build -c Release --no-restore</c>, so it sets <see cref="UseExistingBuildVariable"/> and
/// these check the binaries that build produced instead of building them a second time.
/// </para>
/// <para>
/// Without the variable - locally, and in any other lane that runs them - they build first, as they always did.
/// </para>
/// </remarks>
[Trait("Category", "SdkBuild")]
public sealed class VtReportCliBuildTests
{
    /// <summary>
    /// Set to <c>1</c> by CI's build job, straight after its own <c>dotnet build</c> of the solution: the outputs under
    /// src/*/bin are that build's, so building again would only re-evaluate every project on the critical path.
    /// </summary>
    public const string UseExistingBuildVariable = "NTILDE_SDK_BUILD_TESTS_USE_EXISTING_BUILD";

    [Fact]
    public void CliShim_PrintsHumanReadableSummary()
    {
        string repoRoot = FindRepositoryRoot();
        string cliProjectPath = Path.Combine(repoRoot, "src", "Ntilde.Cli", "Ntilde.Cli.csproj");
        string cliExecutablePath = GetExecutablePath(Path.Combine(repoRoot, "src", "Ntilde.Cli", "bin", "Release", "net10.0"), "Ntilde.Cli");
        // Builds the references too: with -p:BuildProjectReferences=false the compiler wants
        // src/*/obj/Release/net10.0/ref/*.dll, which on CI only exist if AppBinary_* ran first.
        // xunit orders cases by a name hash, so that dependency flipped when the namespace changed.
        Build(repoRoot, cliProjectPath);
        (int exitCode, string stdout, string stderr) = RunBuiltExecutable(repoRoot, cliExecutablePath, "--vt-report");

        Assert.True(exitCode == 0, $"process exited {exitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Ntilde VT Report", stdout);
        Assert.Contains("Matrix:", stdout);
        Assert.Contains("Validation:", stdout);
    }

    [Fact]
    public void AppBinary_PrintsHumanReadableSummary()
    {
        string repoRoot = FindRepositoryRoot();
        string appProjectPath = Path.Combine(repoRoot, "src", "Ntilde.App", "Ntilde.App.csproj");
        string appExecutablePath = GetExecutablePath(Path.Combine(repoRoot, "src", "Ntilde.App", "bin", "Release", "net10.0"), "Ntilde");
        Build(repoRoot, appProjectPath);
        (int exitCode, string stdout, string stderr) = RunBuiltExecutable(repoRoot, appExecutablePath, "--vt-report");

        Assert.True(exitCode == 0, $"process exited {exitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Ntilde VT Report", stdout);
        Assert.Contains("Matrix:", stdout);
        Assert.Contains("Validation:", stdout);
    }

    [Fact]
    public void AppBuild_CopiesCliShim_AsNamedSidecar()
    {
        string repoRoot = FindRepositoryRoot();
        string appProjectPath = Path.Combine(repoRoot, "src", "Ntilde.App", "Ntilde.App.csproj");
        string appOutputDirectory = Path.Combine(repoRoot, "src", "Ntilde.App", "bin", "Release", "net10.0");
        Build(repoRoot, appProjectPath);

        Assert.True(File.Exists(GetExecutablePath(appOutputDirectory, "Ntilde")));
        Assert.True(File.Exists(GetExecutablePath(appOutputDirectory, "Ntilde.Cli")));
        Assert.False(File.Exists(GetExecutablePath(appOutputDirectory, "Ntilde.Gui")));
    }

    private static bool UseExistingBuild =>
        Environment.GetEnvironmentVariable(UseExistingBuildVariable) == "1";

    private static void Build(string repoRoot, string projectPath)
    {
        if (UseExistingBuild)
        {
            return;
        }

        (int buildExitCode, string buildStdOut, string buildStdErr) = RunProcessFromRepository(
            repoRoot,
            "dotnet",
            $"build \"{projectPath}\" -c Release --no-restore -nodeReuse:false");

        Assert.True(buildExitCode == 0, $"dotnet build exited {buildExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{buildStdOut}{Environment.NewLine}stderr:{Environment.NewLine}{buildStdErr}");
        Assert.Equal(string.Empty, buildStdErr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunBuiltExecutable(
        string repoRoot,
        string executablePath,
        string arguments)
    {
        // With UseExistingBuild a missing binary means the build that ran before this did not produce it; say so,
        // rather than fail on Process.Start with a message that names neither the build nor the path.
        Assert.True(File.Exists(executablePath), $"No built executable at {executablePath}.");
        return RunProcessFromRepository(repoRoot, executablePath, arguments);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ntilde.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcessFromRepository(
        string repoRoot,
        string fileName,
        string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        // Read stdout and stderr concurrently to avoid the deadlock that occurs
        // on Windows when the subprocess fills the 4KB stderr pipe buffer while
        // we're blocked on ReadToEnd() for stdout.
        //
        // Tests that invoke `dotnet build` here must pass `-nodeReuse:false`,
        // otherwise the MSBuild worker pool outlives the build process and holds
        // the write end of the redirected pipes — WaitForExit() returns but
        // ReadToEnd() never sees EOF, causing the test (and Linux CI) to hang.
        var stdoutTask = Task.Run(() => process!.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => process!.StandardError.ReadToEnd());
        process!.WaitForExit();

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string GetExecutablePath(string directory, string baseName)
    {
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{baseName}.exe"
            : baseName;
        return Path.Combine(directory, fileName);
    }
}
