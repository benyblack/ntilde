using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// #348, against a real cmd.exe: a profile's own /k or /c command must run, and /c must still
    /// run and exit. Before the fix the session spawned <c>cmd.exe /k chcp 65001 /k echo ...</c>,
    /// chcp rejected the extra switch ("Parameter format not correct"), and the user's command
    /// never ran. <see cref="Core.CmdCodePageArgsTests"/> pins the merge itself.
    /// </summary>
    /// <remarks>
    /// Output is matched with OSC sequences removed: cmd puts its command line in the window
    /// title, so a marker in the command shows up in the stream whether or not it ran. That made
    /// a first version of these tests pass against the bug.
    /// </remarks>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyCmdArgsTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_profiles_own_k_command_runs_and_the_session_stays_open()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

            using var session = new RustPtySession("cmd.exe", 80, 24, args: "/k echo RAW-ARGS-KEPT");
            var output = Capture(session);

            await WaitForAsync(() => output.Contains("RAW-ARGS-KEPT") && output.Contains("65001"), session, output);
            Assert.True(session.IsProcessRunning, "/k keeps the shell open after its command");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_profiles_own_c_command_runs_and_exits()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

            using var session = new RustPtySession("cmd.exe", 80, 24, args: "/c echo RAN-AND-EXITED");
            var output = Capture(session);

            int? code = await WaitForExitAsync(session);

            Assert.True(code == 0, $"expected /c to run its command and exit, got {code?.ToString() ?? "no exit"}.\nOutput:\n{output.Text}");
            Assert.Contains("RAN-AND-EXITED", output.Text, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_c_command_keeps_its_exit_code()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

            using var session = new RustPtySession("cmd.exe", 80, 24, args: "/c exit 7");

            Assert.Equal(7, await WaitForExitAsync(session));
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_quote_opened_c_command_runs_as_written()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

            // cmd strips these outer quotes; the codepage switch stays out of the way.
            using var session = new RustPtySession("cmd.exe", 80, 24, args: "/c \"exit 7\"");

            Assert.Equal(7, await WaitForExitAsync(session));
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_quote_opened_k_command_runs_and_still_gets_the_codepage()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

            using var session = new RustPtySession("cmd.exe", 80, 24, args: "/k \"echo QUOTED-RAN\"");
            var output = Capture(session);

            await WaitForAsync(() => output.Contains("QUOTED-RAN") && output.Contains("65001"), session, output);
            Assert.True(session.IsProcessRunning, "/k keeps the shell open after its command");
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_profile_without_arguments_still_gets_the_codepage()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe is Windows-only");

            using var session = new RustPtySession("cmd.exe", 80, 24);
            var output = Capture(session);

            // "Active code page: 65001" is localized; the number is not.
            await WaitForAsync(() => output.Contains("65001"), session, output);
            Assert.True(session.IsProcessRunning);
        }

        private static OutputLog Capture(RustPtySession session)
        {
            var log = new OutputLog();
            session.OnOutputReceived += log.Append;
            return log;
        }

        private static async Task WaitForAsync(Func<bool> condition, RustPtySession session, OutputLog output)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail(
                        $"cmd.exe never produced the expected output (args '{session.ShellArguments}', " +
                        $"exit {session.ExitCode?.ToString() ?? "none"}).\nOutput:\n{output.Text}");
                }

                await Task.Delay(50);
            }
        }

        /// Returns the reported exit code, or null if none arrived within the timeout.
        private static async Task<int?> WaitForExitAsync(RustPtySession session)
        {
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnExit(int code) => exited.TrySetResult(code);

            session.OnExit += OnExit;
            try
            {
                if (session.ExitCode.HasValue)
                {
                    return session.ExitCode;
                }

                await Task.WhenAny(exited.Task, Task.Delay(Timeout));
                return exited.Task.IsCompletedSuccessfully ? exited.Task.Result : session.ExitCode;
            }
            finally
            {
                session.OnExit -= OnExit;
            }
        }

        /// Everything the session printed, minus OSC sequences (the window title among them).
        private sealed class OutputLog
        {
            private static readonly Regex Osc = new("\u001b\\][^\u0007\u001b]*(\u0007|\u001b\\\\)", RegexOptions.Compiled);
            private readonly StringBuilder _raw = new();

            public string Text
            {
                get
                {
                    string raw;
                    lock (_raw) raw = _raw.ToString();
                    return Osc.Replace(raw, "");
                }
            }

            public void Append(string chunk)
            {
                lock (_raw) _raw.Append(chunk);
            }

            public bool Contains(string value) => Text.Contains(value, StringComparison.Ordinal);
        }
    }
}
