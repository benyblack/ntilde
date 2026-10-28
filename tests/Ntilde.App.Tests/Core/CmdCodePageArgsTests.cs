using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests.Core
{
    /// <summary>
    /// #348: the cmd.exe launch used to prepend "/k chcp 65001 " to whatever the profile passed,
    /// so a profile's own /k or /c command became an argument to chcp and never ran. These pin
    /// the merge; <see cref="Ntilde.Tests.PtyCmdArgsTests"/> runs the same shapes through a real
    /// cmd.exe.
    /// </summary>
    public class CmdCodePageArgsTests
    {
        [Theory]
        [InlineData(null, "/k chcp 65001")]
        [InlineData("", "/k chcp 65001")]
        [InlineData("   ", "/k chcp 65001")]
        // Switches stay switches: cmd reads them before its command switch, and chcp would reject them.
        [InlineData("/q", "/q /k chcp 65001")]
        [InlineData("/d /u", "/d /u /k chcp 65001")]
        public void Without_a_command_the_codepage_switch_is_the_sessions_command(string? args, string expected)
        {
            Assert.Equal(expected, CmdCodePageArgs.Merge(args));
        }

        [Theory]
        [InlineData("/k echo RAW-ARGS-KEPT", "/k chcp 65001 & echo RAW-ARGS-KEPT")]
        [InlineData("/K echo hi", "/K chcp 65001 & echo hi")]
        [InlineData("/c echo hi", "/c chcp 65001 & echo hi")]
        [InlineData("/C exit 7", "/C chcp 65001 & exit 7")]
        [InlineData("/r echo hi", "/r chcp 65001 & echo hi")]
        [InlineData("/q /k echo hi", "/q /k chcp 65001 & echo hi")]
        // A quote later in the command is fine in front: neither of cmd's quote rules changes a
        // command string that doesn't open with a quote.
        [InlineData("/k echo \"a b\"", "/k chcp 65001 & echo \"a b\"")]
        [InlineData("/k a || b", "/k chcp 65001 & a || b")]
        public void A_users_command_runs_after_the_codepage_switch(string args, string expected)
        {
            Assert.Equal(expected, CmdCodePageArgs.Merge(args));
        }

        [Theory]
        // cmd finds its switch glued to other switches or to the command, and after stray text.
        [InlineData("/d/c echo hi", "/d/c chcp 65001 & echo hi")]
        [InlineData("/cecho hi", "/c chcp 65001 & echo hi")]
        [InlineData("foo /k echo hi", "foo /k chcp 65001 & echo hi")]
        public void The_command_switch_is_found_where_cmd_finds_it(string args, string expected)
        {
            Assert.Equal(expected, CmdCodePageArgs.Merge(args));
        }

        [Theory]
        [InlineData("/k", "/k chcp 65001")]
        [InlineData("/c   ", "/c chcp 65001")]
        public void An_empty_command_becomes_the_codepage_switch(string args, string expected)
        {
            Assert.Equal(expected, CmdCodePageArgs.Merge(args));
        }

        [Theory]
        // A quote-opened command may have its outer quotes stripped by cmd, so nothing may go in
        // front of it. For /k the switch goes behind instead: a quote-free suffix leaves cmd's
        // stripping decision as it was.
        [InlineData("/k \"echo hello\"", "/k \"echo hello\" & chcp 65001")]
        [InlineData(
            "/k \"C:\\Program Files\\VS\\VsDevCmd.bat\" -arch=x64",
            "/k \"C:\\Program Files\\VS\\VsDevCmd.bat\" -arch=x64 & chcp 65001")]
        [InlineData(
            "/k \"\"C:\\Program Files (x86)\\VS\\VsDevCmd.bat\" -arch=x64\"",
            "/k \"\"C:\\Program Files (x86)\\VS\\VsDevCmd.bat\" -arch=x64\" & chcp 65001")]
        public void A_quote_opened_k_command_gets_the_codepage_switch_behind_it(string args, string expected)
        {
            Assert.Equal(expected, CmdCodePageArgs.Merge(args));
        }

        [Theory]
        // A suffix would replace a /c command's exit code with chcp's.
        [InlineData("/c \"exit 7\"")]
        [InlineData("/R \"C:\\tools\\run.cmd\" arg")]
        // An unbalanced quote would swallow a suffix into the quoted text.
        [InlineData("/k \"echo unbalanced")]
        public void A_command_the_switch_cannot_go_around_is_left_as_written(string args)
        {
            Assert.Equal(args, CmdCodePageArgs.Merge(args));
        }

        [Fact]
        public void Surrounding_whitespace_is_dropped()
        {
            Assert.Equal("/k chcp 65001 & echo hi", CmdCodePageArgs.Merge("  /k   echo hi  "));
        }
    }
}
