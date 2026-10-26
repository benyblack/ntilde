using System.IO;
using System.Text;

namespace Ntilde.CommandAssist.ShellIntegration.Fish;

public static class FishBootstrapBuilder
{
    public static string BuildScript()
    {
        const string nl = "\n";
        var b = new StringBuilder();
        b.Append("# Ntilde command-assist bootstrap for fish.").Append(nl);
        b.Append("# Installed as $XDG_CONFIG_HOME/fish/config.fish. We are the").Append(nl);
        b.Append("# user's config.fish for this session, so we explicitly source").Append(nl);
        b.Append("# the real ~/.config/fish/config.fish if it exists, then layer").Append(nl);
        b.Append("# our hooks on top so user prompt/fish_prompt stays user-owned.").Append(nl);
        b.Append(nl);
        b.Append("set -l __ntilde_user_config \"$HOME/.config/fish/config.fish\"").Append(nl);
        b.Append("if test -f \"$__ntilde_user_config\"").Append(nl);
        b.Append("    source \"$__ntilde_user_config\"").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("set -g __ntilde_command_start_ms \"\"").Append(nl);
        b.Append(nl);
        // fish 4.0 and later write OSC 133 A, C and D themselves (C carries the command as
        // cmdline_url from 4.0.1), and 4.3 added B. Writing ours as well marked every command
        // twice, and everything downstream of D counted it twice, so when fish writes its own we
        // add only what it does not. From 4.0.6 the mark-prompt feature says whether fish's marks
        // are on (no-mark-prompt turns them off). `status test-feature` answers 2 for a feature it
        // does not know, and then the version decides: 4.0.x always marked and 3.x never did.
        // fish runs `; or` / `; and` left to right, so the condition reads "on, or unknown and
        // at least 4.0".
        b.Append("if status test-feature mark-prompt 2>/dev/null; or test $status -eq 2; and string match -qr '^([4-9]|[1-9][0-9]+)\\.' -- $version").Append(nl);
        b.Append("    set -g __ntilde_fish_writes_marks 1").Append(nl);
        b.Append("    string match -qr '^(4\\.([3-9]|[1-9][0-9])|([5-9]|[1-9][0-9]+)\\.)' -- $version; and set -g __ntilde_fish_writes_b 1").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        // Portable millisecond clock. `date +%s%N` is GNU-only; macOS/BSD
        // `date` leaves a literal "%N", which would break the `math` call.
        // Detect at runtime: if the output is digits-only, treat as
        // nanoseconds; otherwise fall back to second precision.
        //
        // -s0 on every `math` call. fish's default scale is 6, so the division
        // prints "1780000000123.456787" and the OSC 133;D duration stops being
        // an integer -- AnsiParser parses that field with long.TryParse, so a
        // fractional value is not a rounded duration, it is NO duration.
        b.Append("function __ntilde_now_ms").Append(nl);
        b.Append("    set -l raw (date +%s%N 2>/dev/null)").Append(nl);
        b.Append("    if string match -qr '^[0-9]+$' -- $raw").Append(nl);
        b.Append("        math -s0 \"$raw / 1000000\"").Append(nl);
        b.Append("    else").Append(nl);
        b.Append("        math -s0 (date +%s) \"* 1000\"").Append(nl);
        b.Append("    end").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("function __ntilde_emit_prompt_ready").Append(nl);
        b.Append("    printf '\\033]7;file://%s%s\\a' (hostname) (string escape --style=url -- $PWD)").Append(nl);
        b.Append("    set -q __ntilde_fish_writes_marks; or printf '\\033]133;A\\a'").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        // fish has native fish_preexec / fish_postexec events. We use
        // event handlers (function ... --on-event ...) so our hooks layer
        // cleanly without overwriting fish_prompt.
        //
        // Only preexec returns early when fish writes its own marks: postexec writes D only once
        // preexec has started the clock, and a guard at its top would overwrite the $status it reads.
        b.Append("function __ntilde_preexec --on-event fish_preexec").Append(nl);
        b.Append("    set -q __ntilde_fish_writes_marks; and return").Append(nl);
        b.Append("    set -l cmd \"$argv\"").Append(nl);
        b.Append("    set -l b64 (printf '%s' \"$cmd\" | base64 | tr -d '\\n')").Append(nl);
        b.Append("    printf '\\033]133;C;%s\\a' \"$b64\"").Append(nl);
        b.Append("    set -g __ntilde_command_start_ms (__ntilde_now_ms)").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("function __ntilde_postexec --on-event fish_postexec").Append(nl);
        b.Append("    set -l exit $status").Append(nl);
        b.Append("    if test -n \"$__ntilde_command_start_ms\"").Append(nl);
        b.Append("        set -l now_ms (__ntilde_now_ms)").Append(nl);
        b.Append("        set -l duration_ms (math -s0 $now_ms - $__ntilde_command_start_ms)").Append(nl);
        b.Append("        printf '\\033]133;D;%s;%s\\a' $exit $duration_ms").Append(nl);
        b.Append("        set -g __ntilde_command_start_ms \"\"").Append(nl);
        b.Append("    end").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        // fish_prompt fires every prompt cycle. Hook (don't override) it
        // via a function with the same name preserved -- but since fish
        // only allows one function per name, we use a separate event hook
        // that fires after the prompt redraws.
        b.Append("function __ntilde_promptmark --on-event fish_prompt").Append(nl);
        b.Append("    __ntilde_emit_prompt_ready").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        // The fish_prompt EVENT fires before the prompt function runs, so it
        // can only carry A. OSC 133;B has to land after the last prompt cell,
        // and fish has no post-prompt event -- so we copy the user's
        // fish_prompt aside and re-define fish_prompt as "original, then B".
        // The copy keeps the user's prompt output byte-for-byte; we only
        // append to it.
        //
        // Bail out entirely if fish_prompt cannot be resolved (functions -q
        // triggers autoloading, so this only fails in a genuinely broken
        // config). Degrading to A-only beats replacing the user's prompt
        // with a synthesized one.
        //
        // The `not functions -q __ntilde_user_fish_prompt` half is what makes a
        // re-source safe. This file IS $__fish_config_dir/config.fish for the
        // session, so anything that re-runs it (fish's own
        // `source $__fish_config_dir/config.fish`, `exec fish`, a user alias)
        // would otherwise copy the CURRENT fish_prompt -- already our wrapper --
        // over __ntilde_user_fish_prompt, and the redefinition below would then
        // call itself forever. With the guard, the second pass finds
        // __ntilde_user_fish_prompt already defined and leaves both functions
        // alone, so the original user prompt stays wrapped exactly once.
        //
        // fish 4.3 and later write B themselves, at the same cell, so then the prompt is not
        // wrapped at all.
        b.Append("if functions -q fish_prompt; and not functions -q __ntilde_user_fish_prompt; and not set -q __ntilde_fish_writes_b").Append(nl);
        b.Append("    functions --copy fish_prompt __ntilde_user_fish_prompt").Append(nl);
        b.Append("    function fish_prompt").Append(nl);
        b.Append("        __ntilde_user_fish_prompt").Append(nl);
        b.Append("        printf '\\033]133;B\\a'").Append(nl);
        b.Append("    end").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("__ntilde_emit_prompt_ready").Append(nl);
        return b.ToString();
    }

    public static string WriteScript(string targetDirectory)
    {
        // Fish reads $XDG_CONFIG_HOME/fish/config.fish on interactive startup.
        // Place our bootstrap in a fish/ subdirectory so XDG_CONFIG_HOME can
        // point at <root> while only fish-related files live in <root>/fish.
        string fishDir = Path.Combine(targetDirectory, "fish");
        Directory.CreateDirectory(fishDir);
        string path = Path.Combine(fishDir, "config.fish");
        File.WriteAllText(path, BuildScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
