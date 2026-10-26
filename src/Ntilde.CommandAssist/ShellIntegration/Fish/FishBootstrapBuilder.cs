using System.IO;
using System.Text;

namespace Ntilde.CommandAssist.ShellIntegration.Fish;

public static class FishBootstrapBuilder
{
    // The script is written as fish rather than as escaped C#, one raw string per part, joined by a
    // blank line. A raw string takes this file's line endings (CRLF, per .gitattributes), so they
    // are normalized: fish needs LF.
    public static string BuildScript() =>
        string.Join(
            "\n\n",
            Preamble,
            FishMarks,
            Clock,
            PromptReady,
            CommandHooks,
            PromptHook,
            PromptWrap,
            "__ntilde_emit_prompt_ready").ReplaceLineEndings("\n") + "\n";

    private const string Preamble = """
        # Ntilde command-assist bootstrap for fish.
        # Installed as $XDG_CONFIG_HOME/fish/config.fish. We are the
        # user's config.fish for this session, so we explicitly source
        # the real ~/.config/fish/config.fish if it exists, then layer
        # our hooks on top so user prompt/fish_prompt stays user-owned.

        set -l __ntilde_user_config "$HOME/.config/fish/config.fish"
        if test -f "$__ntilde_user_config"
            source "$__ntilde_user_config"
        end

        set -g __ntilde_command_start_ms ""
        """;

    // fish 4.0 and later write OSC 133 A, C and D themselves (C carries the command as
    // cmdline_url from 4.0.1), and 4.3 added B. Writing ours as well marked every command
    // twice, and everything downstream of D counted it twice, so when fish writes its own we
    // add only what it does not. From 4.0.6 the mark-prompt feature says whether fish's marks
    // are on (no-mark-prompt turns them off). `status test-feature` answers 2 for a feature it
    // does not know, and then the version decides: 4.0.x always marked and 3.x never did.
    // fish runs `; or` / `; and` left to right, so the condition reads "on, or unknown and
    // at least 4.0".
    private const string FishMarks = """
        if status test-feature mark-prompt 2>/dev/null; or test $status -eq 2; and string match -qr '^([4-9]|[1-9][0-9]+)\.' -- $version
            set -g __ntilde_fish_writes_marks 1
            string match -qr '^(4\.([3-9]|[1-9][0-9])|([5-9]|[1-9][0-9]+)\.)' -- $version; and set -g __ntilde_fish_writes_b 1
        end
        """;

    // Portable millisecond clock. `date +%s%N` is GNU-only; macOS/BSD
    // `date` leaves a literal "%N", which would break the `math` call.
    // Detect at runtime: if the output is digits-only, treat as
    // nanoseconds; otherwise fall back to second precision.
    //
    // -s0 on every `math` call. fish's default scale is 6, so the division
    // prints "1780000000123.456787" and the OSC 133;D duration stops being
    // an integer -- AnsiParser parses that field with long.TryParse, so a
    // fractional value is not a rounded duration, it is NO duration.
    private const string Clock = """
        function __ntilde_now_ms
            set -l raw (date +%s%N 2>/dev/null)
            if string match -qr '^[0-9]+$' -- $raw
                math -s0 "$raw / 1000000"
            else
                math -s0 (date +%s) "* 1000"
            end
        end
        """;

    private const string PromptReady = """
        function __ntilde_emit_prompt_ready
            printf '\033]7;file://%s%s\a' (hostname) (string escape --style=url -- $PWD)
            set -q __ntilde_fish_writes_marks; or printf '\033]133;A\a'
        end
        """;

    // fish has native fish_preexec / fish_postexec events. We use
    // event handlers (function ... --on-event ...) so our hooks layer
    // cleanly without overwriting fish_prompt.
    //
    // Only preexec returns early when fish writes its own marks: postexec writes D only once
    // preexec has started the clock, and a guard at its top would overwrite the $status it reads.
    private const string CommandHooks = """
        function __ntilde_preexec --on-event fish_preexec
            set -q __ntilde_fish_writes_marks; and return
            set -l cmd "$argv"
            set -l b64 (printf '%s' "$cmd" | base64 | tr -d '\n')
            printf '\033]133;C;%s\a' "$b64"
            set -g __ntilde_command_start_ms (__ntilde_now_ms)
        end

        function __ntilde_postexec --on-event fish_postexec
            set -l exit $status
            if test -n "$__ntilde_command_start_ms"
                set -l now_ms (__ntilde_now_ms)
                set -l duration_ms (math -s0 $now_ms - $__ntilde_command_start_ms)
                printf '\033]133;D;%s;%s\a' $exit $duration_ms
                set -g __ntilde_command_start_ms ""
            end
        end
        """;

    // fish_prompt fires every prompt cycle. Hook (don't override) it
    // via a function with the same name preserved -- but since fish
    // only allows one function per name, we use a separate event hook
    // that fires after the prompt redraws.
    private const string PromptHook = """
        function __ntilde_promptmark --on-event fish_prompt
            __ntilde_emit_prompt_ready
        end
        """;

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
    private const string PromptWrap = """
        if functions -q fish_prompt; and not functions -q __ntilde_user_fish_prompt; and not set -q __ntilde_fish_writes_b
            functions --copy fish_prompt __ntilde_user_fish_prompt
            function fish_prompt
                __ntilde_user_fish_prompt
                printf '\033]133;B\a'
            end
        end
        """;

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
