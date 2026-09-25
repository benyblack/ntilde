using System;
using System.IO;
using System.Text;
using Ntilde.CommandAssist.ShellIntegration;
using Xunit;

namespace Ntilde.Tests.CommandAssist.ShellIntegration;

/// <summary>
/// A pane persisted the arguments it was *launched* with, which included the shell-integration
/// bootstrap we injected. On the next launch the provider saw its own `-File` in the incoming
/// arguments, took the "the user supplied a script" bail-out, and passed the stale command line
/// through unchanged — so integration silently stopped and the old bootstrap path was launched
/// forever. Reported after a 0.5.0 → 0.6.0 update: the restored session carried 0.5.0's arguments.
///
/// This strips what we injected so a stored command line cannot be mistaken for the user's own.
/// </summary>
public sealed class ShellIntegrationArgumentsTests
{
    private const string BootstrapDir = @"C:\Users\x\AppData\Local\ntilde\command-assist";

    private static string OurBootstrap => Path.Combine(BootstrapDir, "command-assist-bootstrap.ps1");

    private static string Encode(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    // A macOS session file from the field, verbatim: bash's injection, saved by a pre-rebrand
    // version, under the old NovaTerminal data folder, with a space in the path.
    private const string MacLegacyDir = "/Users/babak/Library/Application Support/NovaTerminal/command-assist";
    private const string MacCurrentDir = "/Users/babak/Library/Application Support/ntilde/command-assist";
    private static readonly string?[] MacDirs = { MacCurrentDir, MacLegacyDir };

    [Fact]
    public void StripInjected_RemovesAStaleBashBootstrapFromThePreRebrandFolder()
    {
        string stale = $"--rcfile \"{MacLegacyDir}/command-assist-bootstrap.bash\" -i";

        // The injected trailing -i goes with it: "" is what the user configured.
        Assert.Equal(string.Empty, ShellIntegrationArguments.StripInjected(stale, MacDirs));
    }

    [Fact]
    public void StripInjected_RemovesABashBootstrapFromTheCurrentFolder()
    {
        string stale = $"--rcfile \"{MacCurrentDir}/command-assist-bootstrap.bash\" -i";

        Assert.Equal(string.Empty, ShellIntegrationArguments.StripInjected(stale, MacDirs));
    }

    [Fact]
    public void StripInjected_UnquotedBashBootstrap_IsRemovedToo()
    {
        const string dir = "/home/me/.local/share/ntilde/command-assist";
        string stale = $"--rcfile {dir}/command-assist-bootstrap.bash -i";

        Assert.Equal(string.Empty, ShellIntegrationArguments.StripInjected(stale, dir));
    }

    [Fact]
    public void StripInjected_KeepsTheUsersOwnArgumentsAfterTheBashBootstrap()
    {
        // The provider puts the user's arguments after its --rcfile and adds -i only when they had
        // no interactive flag - so with --login there is no trailing -i to remove.
        string stale = $"--rcfile \"{MacLegacyDir}/command-assist-bootstrap.bash\" --noprofile --login";

        Assert.Equal("--noprofile --login", ShellIntegrationArguments.StripInjected(stale, MacDirs));
    }

    [Theory]
    // A user's own rcfile - the case the bash provider's bail-out exists for.
    [InlineData("--rcfile \"/Users/babak/my rc/bashrc\" -i")]
    // Our file name, but not in one of our folders.
    [InlineData("--rcfile /tmp/command-assist-bootstrap.bash -i")]
    // bash's option is lowercase; this is not it.
    [InlineData("--RCFILE \"/Users/babak/Library/Application Support/ntilde/command-assist/command-assist-bootstrap.bash\" -i")]
    // Unterminated quote: not a token, so nothing can be proven ours.
    [InlineData("--rcfile \"/Users/babak/Library/Application Support/ntilde/command-assist/command-assist-bootstrap.bash -i")]
    public void StripInjected_KeepsBashArgumentsThatAreNotProvablyOurs(string args)
    {
        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, MacDirs));
    }

    [Fact]
    public void StripInjected_RemovesAStaleBootstrapFileArgument()
    {
        string stale = $"-NoLogo -NoExit -File {OurBootstrap}";

        string cleaned = ShellIntegrationArguments.StripInjected(stale, BootstrapDir);

        Assert.DoesNotContain("command-assist-bootstrap.ps1", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-File", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-NoLogo", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void StripInjected_RemovesOurEncodedBootstrap()
    {
        string encoded = Encode(
            Ntilde.CommandAssist.ShellIntegration.PowerShell.PowerShellBootstrapBuilder.BuildScript());

        string cleaned = ShellIntegrationArguments.StripInjected(
            $"-NoLogo -NoExit -EncodedCommand {encoded}", BootstrapDir);

        Assert.DoesNotContain("-EncodedCommand", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(encoded, cleaned, StringComparison.Ordinal);
    }

    // Greptile P1 on #368. Split-on-space plus a single-space rejoin rewrote every restored
    // command line, not just ones we had injected into — so a quoted value containing repeated
    // spaces came back altered and the shell ran something the user never configured.
    [Theory]
    [InlineData("-Command \"a  b\"")]
    [InlineData("-NoLogo   -NoExit")]
    [InlineData("  -NoLogo ")]
    [InlineData(@"-File ""C:\my  scripts\run.ps1""")]
    public void StripInjected_ReturnsTheCommandLineVerbatimWhenNothingWasInjected(string args)
    {
        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, BootstrapDir));
    }

    // Greptile P1 on #368. A suffix-only check claimed any file with our name, wherever it lived.
    [Fact]
    public void StripInjected_KeepsAUserScriptThatMerelySharesTheBootstrapFileName()
    {
        // Same file name, a directory we do not own. It is the user's script.
        const string userArgs = @"-NoLogo -File C:\mine\command-assist-bootstrap.ps1";

        string cleaned = ShellIntegrationArguments.StripInjected(userArgs, BootstrapDir);

        Assert.Equal(userArgs, cleaned);
    }

    // Greptile P1 round 2 on #368. The seam cleanup was a GLOBAL Replace("  ", " "), so a
    // removal anywhere re-spaced the whole retained line - including the user's quoted values.
    [Fact]
    public void StripInjected_RemovingOurArgumentLeavesTheUsersQuotedSpacingIntact()
    {
        string args = $"-Command \"a  b\" -File {OurBootstrap} -NoLogo";

        string cleaned = ShellIntegrationArguments.StripInjected(args, BootstrapDir);

        Assert.DoesNotContain("command-assist-bootstrap.ps1", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-Command \"a  b\"", cleaned, StringComparison.Ordinal);
        Assert.Contains("-NoLogo", cleaned, StringComparison.Ordinal);
    }

    // Greptile P1 round 2 on #368. The provider converts a bootstrap path containing spaces to
    // its 8.3 short form (PowerShellShellIntegrationProvider.ResolveSpacelessPath), so a legacy
    // session can hold a short path. Path.GetFullPath does not expand 8.3, so a purely lexical
    // comparison rejected our own file and left the stale -File in place forever.
    [Fact]
    public void StripInjected_RecognisesTheShortPathFormOfOurOwnBootstrap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 8.3 short names are a Windows filesystem feature.
        }

        // A directory whose long name contains a space is what triggers the short-path form.
        string dir = Path.Combine(Path.GetTempPath(), "ntilde bootstrap short " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string longPath = Path.Combine(dir, "command-assist-bootstrap.ps1");
            File.WriteAllText(longPath, "# bootstrap");

            string shortPath = ShortPath(longPath);
            Assert.NotEqual(longPath, shortPath); // otherwise the test proves nothing

            string cleaned = ShellIntegrationArguments.StripInjected($"-NoLogo -File {shortPath}", dir);

            Assert.Equal("-NoLogo", cleaned);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string ShortPath(string path)
    {
        var buffer = new System.Text.StringBuilder(512);
        uint n = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        return n == 0 ? path : buffer.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpszLongPath,
        System.Text.StringBuilder lpszShortPath,
        uint cchBuffer);

    [Fact]
    public void StripInjected_KeepsAUsersOwnScript()
    {
        const string userArgs = @"-NoLogo -File C:\work\my-profile.ps1";

        Assert.Equal(userArgs, ShellIntegrationArguments.StripInjected(userArgs, BootstrapDir));
    }

    [Fact]
    public void StripInjected_KeepsAUsersOwnEncodedCommand()
    {
        string userEncoded = Encode("Write-Host 'hello'");
        string args = $"-EncodedCommand {userEncoded}";

        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, BootstrapDir));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("-NoLogo", "-NoLogo")]
    public void StripInjected_LeavesOrdinaryArgumentsAlone(string? input, string expected)
    {
        Assert.Equal(expected, ShellIntegrationArguments.StripInjected(input, BootstrapDir));
    }

    [Fact]
    public void StripInjected_ToleratesAMalformedEncodedCommand()
    {
        const string args = "-EncodedCommand not-base64!!";

        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, BootstrapDir));
    }

    [Fact]
    public void StripInjected_WithoutAKnownBootstrapDirectory_KeepsEverything()
    {
        // No directory to compare against means no way to prove a -File is ours, and
        // guessing would launch a shell without the user's script.
        string args = $"-File {OurBootstrap}";

        Assert.Equal(args, ShellIntegrationArguments.StripInjected(args, bootstrapDirectory: null));
    }
}
