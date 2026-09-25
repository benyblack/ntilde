using System.IO;
using System.Text;

namespace Ntilde.CommandAssist.ShellIntegration.Zsh;

/// <summary>
/// Writes the zsh startup files that inject command-assist integration.
/// </summary>
/// <remarks>
/// Injection works by pointing <c>ZDOTDIR</c> at a directory of ours, and zsh reads EVERY
/// per-user startup file from <c>$ZDOTDIR</c>: <c>.zshenv</c>, <c>.zprofile</c> (login),
/// <c>.zshrc</c> (interactive), <c>.zlogin</c> (login). Shipping only a <c>.zshrc</c> meant the
/// user's <c>~/.zshenv</c>, <c>~/.zprofile</c> and <c>~/.zlogin</c> were never read at all - on
/// macOS that is where Homebrew's installer puts <c>brew shellenv</c>, so a pane came up with no
/// <c>/opt/homebrew/bin</c> on PATH and every <c>brew</c>/<c>nvm</c> line in <c>~/.zshrc</c> failed.
///
/// So each of the four is a shim: it puts the user's <c>ZDOTDIR</c> back (or unsets it, when they
/// had none), sources the user's file of the same name at top level - not from a function, where
/// the user's <c>typeset</c>s would turn local - records whatever <c>ZDOTDIR</c> the user's file
/// left behind (an XDG setup sets it in <c>~/.zshenv</c>), then points zsh back at us for the next
/// file. The last file zsh reads (<c>.zshrc</c>, or <c>.zlogin</c> for a login shell) leaves the
/// user's value in place, so nothing after startup - a nested zsh, the user's own tooling - ever
/// sees our directory. The integration itself still runs right after the user's <c>.zshrc</c>, so
/// its hooks land after the user's exactly as before.
///
/// The user's original <c>ZDOTDIR</c>, when they had one, arrives in
/// <see cref="UserZdotdirVariable"/>; its absence means "unset", i.e. <c>$HOME</c>.
/// </remarks>
public static class ZshBootstrapBuilder
{
    /// <summary>Environment variable carrying the user's own <c>ZDOTDIR</c> into the shim.</summary>
    public const string UserZdotdirVariable = "NTILDE_ZSH_USER_ZDOTDIR";

    private const string nl = "\n";

    // The user's ZDOTDIR goes back before each of their files is sourced - value AND export
    // attribute. zsh reads the shell parameter, so `ZDOTDIR=~/.config/zsh` in ~/.zshenv without
    // `export` is a valid setup, and restoring it exported would leak it to every child process.
    // The unset first clears whatever attribute the shim's own value carried.
    private const string RestoreUserZdotdir =
        "builtin unset ZDOTDIR" + nl +
        "if (( __ntilde_user_zdotdir_set )); then" + nl +
        "    ZDOTDIR=\"$__ntilde_user_zdotdir\"" + nl +
        "    if (( __ntilde_user_zdotdir_exported )); then" + nl +
        "        builtin export ZDOTDIR" + nl +
        "    fi" + nl +
        "fi" + nl;

    // ...and whatever the user's file left it as is what their next file is read from.
    private const string CaptureUserZdotdir =
        "if [[ -n \"${ZDOTDIR+x}\" ]]; then" + nl +
        "    __ntilde_user_zdotdir_set=1" + nl +
        "    __ntilde_user_zdotdir=\"$ZDOTDIR\"" + nl +
        "    if [[ \"${(t)ZDOTDIR}\" == *export* ]]; then" + nl +
        "        __ntilde_user_zdotdir_exported=1" + nl +
        "    else" + nl +
        "        __ntilde_user_zdotdir_exported=0" + nl +
        "    fi" + nl +
        "else" + nl +
        "    __ntilde_user_zdotdir_set=0" + nl +
        "fi" + nl;

    // Hands the next startup file back to us.
    private const string PointZdotdirAtShim = "ZDOTDIR=\"$__ntilde_zdotdir\"" + nl;

    private const string ForgetShimState =
        "builtin unset __ntilde_zdotdir __ntilde_user_zdotdir __ntilde_user_zdotdir_set __ntilde_user_zdotdir_exported" + nl;

    // `-`, not `:-`: zsh falls back to $HOME only when ZDOTDIR is UNSET. Set-but-empty makes it
    // read /.zshenv and friends, and the shim must pick exactly the files zsh would.
    private static string SourceUserFile(string name) =>
        $"if [[ -r \"${{ZDOTDIR-$HOME}}/{name}\" ]]; then" + nl +
        $"    builtin source \"${{ZDOTDIR-$HOME}}/{name}\"" + nl +
        "fi" + nl;

    private static string Header(string name) =>
        $"# Ntilde command-assist shim: $ZDOTDIR/{name}. Sources the user's own {name}" + nl +
        "# from their ZDOTDIR (or $HOME); see ZshBootstrapBuilder for why." + nl;

    public static string BuildZshenv() =>
        Header(".zshenv") +
        "__ntilde_zdotdir=\"$ZDOTDIR\"" + nl +
        $"if [[ -n \"${{{UserZdotdirVariable}+x}}\" ]]; then" + nl +
        "    __ntilde_user_zdotdir_set=1" + nl +
        $"    __ntilde_user_zdotdir=\"${UserZdotdirVariable}\"" + nl +
        // It reached Ntilde through the environment, so it was exported.
        "    __ntilde_user_zdotdir_exported=1" + nl +
        "else" + nl +
        "    __ntilde_user_zdotdir_set=0" + nl +
        "    __ntilde_user_zdotdir=\"\"" + nl +
        "    __ntilde_user_zdotdir_exported=0" + nl +
        "fi" + nl +
        $"builtin unset {UserZdotdirVariable}" + nl +
        RestoreUserZdotdir +
        SourceUserFile(".zshenv") +
        // A shell that reads nothing after .zshenv (a script, `zsh -c` never gets here) must
        // not be left pointing at us.
        "if [[ -o interactive || -o login ]]; then" + nl +
        "    " + CaptureUserZdotdir.Replace(nl, nl + "    ").TrimEnd(' ') +
        "    " + PointZdotdirAtShim +
        "else" + nl +
        "    " + ForgetShimState +
        "fi" + nl;

    public static string BuildZprofile() =>
        Header(".zprofile") +
        RestoreUserZdotdir +
        SourceUserFile(".zprofile") +
        CaptureUserZdotdir +
        PointZdotdirAtShim;

    public static string BuildZlogin() =>
        Header(".zlogin") +
        RestoreUserZdotdir +
        SourceUserFile(".zlogin") +
        ForgetShimState;

    public static string BuildScript()
    {
        var b = new StringBuilder();
        b.Append("#!/usr/bin/env zsh").Append(nl);
        b.Append(Header(".zshrc"));
        b.Append("# The integration below runs after the user's .zshrc so").Append(nl);
        b.Append("# customizations and PROMPT/PS1 stay owned by the user.").Append(nl);
        b.Append(RestoreUserZdotdir);
        // The global zshrc ran while ZDOTDIR still pointed at us, and macOS's /etc/zshrc
        // derives HISTFILE from it - which would keep the user's history in our directory.
        b.Append("if [[ \"${HISTFILE-}\" == \"$__ntilde_zdotdir/.zsh_history\" ]]; then").Append(nl);
        b.Append("    HISTFILE=\"${ZDOTDIR:-$HOME}/.zsh_history\"").Append(nl);
        b.Append("fi").Append(nl);
        b.Append(SourceUserFile(".zshrc"));
        b.Append(nl);
        b.Append("typeset -g __ntilde_command_start_ms=\"\"").Append(nl);
        // Load zsh's native datetime module so $EPOCHREALTIME is available
        // for portable millisecond timing. `date +%s%N` is GNU-only and
        // leaves a literal "%N" on macOS/BSD, breaking arithmetic.
        //
        // +p: because EPOCHREALTIME is a PARAMETER. `zmodload -F` with an
        // unknown feature name fails, and with the error swallowed the module
        // never loads at all: $EPOCHREALTIME stays unset, __ntilde_now_ms falls
        // back to `date +%s`, and every duration is reported as a whole number
        // of seconds. (+b: is the builtin namespace, which has no
        // EPOCHREALTIME in it.)
        b.Append("zmodload -F zsh/datetime +p:EPOCHREALTIME 2>/dev/null || true").Append(nl);
        b.Append(nl);
        b.Append("__ntilde_now_ms() {").Append(nl);
        b.Append("    if (( ${+EPOCHREALTIME} )); then").Append(nl);
        b.Append("        local sec=\"${EPOCHREALTIME%.*}\"").Append(nl);
        b.Append("        local frac=\"${EPOCHREALTIME#*.}\"").Append(nl);
        b.Append("        printf '%s%s' \"$sec\" \"${frac:0:3}\"").Append(nl);
        b.Append("    else").Append(nl);
        b.Append("        printf '%s000' \"$(date +%s)\"").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__ntilde_url_encode_pwd() {").Append(nl);
        b.Append("    local s=\"$PWD\"").Append(nl);
        b.Append("    printf '%s' \"$s\" | LC_ALL=C awk 'BEGIN{for(i=0;i<256;i++)c[sprintf(\"%c\",i)]=i} {for(i=1;i<=length($0);i++){ch=substr($0,i,1); if(ch~/[A-Za-z0-9._~\\/-]/) printf \"%s\",ch; else printf \"%%%02X\",c[ch]}}'").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__ntilde_emit_prompt_ready() {").Append(nl);
        b.Append("    printf '\\033]7;file://%s%s\\a' \"${HOST:-localhost}\" \"$(__ntilde_url_encode_pwd)\"").Append(nl);
        b.Append("    printf '\\033]133;A\\a'").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        // OSC 133;B marks the END of the prompt -- the cell where the user's
        // input starts. precmd runs before PROMPT is expanded, so B cannot be
        // printed from a hook the way A is; it has to be the last thing in
        // PROMPT itself. %{...%} tells zsh the sequence occupies zero columns,
        // so prompt-width arithmetic and ZLE redraw stay correct.
        b.Append("typeset -g __ntilde_prompt_mark=$'%{\\e]133;B\\a%}'").Append(nl);
        // Re-applied every precmd rather than once at startup because prompt
        // frameworks (powerlevel10k, starship, oh-my-zsh themes) reassign
        // PROMPT from their own precmd hooks; ours is appended to
        // precmd_functions last, so we get the final word.
        //
        // Strip-then-append rather than skip-if-present: zsh has no "arm last"
        // invariant like bash's __ntilde_arm, so a precmd hook registered after
        // ours can append to PROMPT and leave our mark buried mid-prompt, where
        // it would report the input cell several columns early. Removing the
        // existing suffix (a no-op when absent, since ${var%pattern} only trims
        // a trailing match) and re-appending is idempotent AND self-correcting.
        b.Append("__ntilde_apply_prompt_mark() {").Append(nl);
        b.Append("    PROMPT=\"${PROMPT%$__ntilde_prompt_mark}$__ntilde_prompt_mark\"").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        // zsh has native preexec/precmd hooks via the function-array convention.
        // preexec receives the about-to-run command as $1, so unlike bash we
        // do not need a one-shot guard around the DEBUG trap.
        b.Append("__ntilde_preexec() {").Append(nl);
        b.Append("    local cmd=\"$1\"").Append(nl);
        b.Append("    local b64").Append(nl);
        b.Append("    b64=$(printf '%s' \"$cmd\" | base64 | tr -d '\\n')").Append(nl);
        b.Append("    printf '\\033]133;C;%s\\a' \"$b64\"").Append(nl);
        b.Append("    __ntilde_command_start_ms=$(__ntilde_now_ms)").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        // Two precmd hooks at opposite ends of precmd_functions, because the
        // two jobs want opposite positions. $? inside a precmd hook is the
        // status of whatever ran immediately before it, and for an APPENDED
        // hook that is the previous precmd hook rather than the user's
        // command -- so on any setup with another precmd registered (oh-my-zsh,
        // powerlevel10k, a vcs_info hook) the reported exit code was that
        // hook's, i.e. almost always 0. Snapshotting $? has to happen in a hook
        // that runs FIRST. Re-applying the prompt mark has to happen in a hook
        // that runs LAST, after any theme has finished rewriting PROMPT.
        b.Append("typeset -g __ntilde_last_status=0").Append(nl);
        b.Append("__ntilde_status_snapshot() {").Append(nl);
        b.Append("    __ntilde_last_status=$?").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__ntilde_precmd() {").Append(nl);
        b.Append("    local exit=$__ntilde_last_status").Append(nl);
        b.Append("    if [ -n \"$__ntilde_command_start_ms\" ]; then").Append(nl);
        b.Append("        local now_ms duration_ms").Append(nl);
        b.Append("        now_ms=$(__ntilde_now_ms)").Append(nl);
        b.Append("        duration_ms=$((now_ms - __ntilde_command_start_ms))").Append(nl);
        b.Append("        printf '\\033]133;D;%s;%s\\a' \"$exit\" \"$duration_ms\"").Append(nl);
        b.Append("        __ntilde_command_start_ms=\"\"").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("    __ntilde_emit_prompt_ready").Append(nl);
        b.Append("    __ntilde_apply_prompt_mark").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("typeset -ag precmd_functions preexec_functions").Append(nl);
        // Prepended, not appended: see __ntilde_status_snapshot above.
        b.Append("precmd_functions=(__ntilde_status_snapshot \"${precmd_functions[@]}\")").Append(nl);
        b.Append("precmd_functions+=(__ntilde_precmd)").Append(nl);
        b.Append("preexec_functions+=(__ntilde_preexec)").Append(nl);
        b.Append(nl);
        // The first prompt is expanded before any precmd runs in some zsh
        // configurations, so seed the mark here too; __ntilde_apply_prompt_mark
        // is idempotent.
        b.Append("__ntilde_apply_prompt_mark").Append(nl);
        b.Append("__ntilde_emit_prompt_ready").Append(nl);
        b.Append(nl);
        // A login shell still has .zlogin to read, from us; otherwise this is the last file and
        // the user's ZDOTDIR (restored above, or as their .zshrc left it) stays.
        b.Append("if [[ -o login ]]; then").Append(nl);
        b.Append("    ").Append(CaptureUserZdotdir.Replace(nl, nl + "    ").TrimEnd(' '));
        b.Append("    ").Append(PointZdotdirAtShim);
        b.Append("else").Append(nl);
        b.Append("    ").Append(ForgetShimState);
        b.Append("fi").Append(nl);
        return b.ToString();
    }

    /// <summary>
    /// Writes all four shims into a zsh-only subdirectory of <paramref name="targetDirectory"/>
    /// and returns the path of the <c>.zshrc</c>, whose directory is the <c>ZDOTDIR</c> to launch
    /// with.
    /// </summary>
    public static string WriteScript(string targetDirectory)
    {
        // ZDOTDIR must point at a directory that only contains zsh startup
        // files; live next to other shells' bootstrap files would let zsh
        // mistakenly source .zshenv-like neighbors. Carve out a zsh-only
        // subdirectory and write the shims into it.
        string zshDir = Path.Combine(targetDirectory, "zsh");
        Directory.CreateDirectory(zshDir);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(zshDir, ".zshenv"), BuildZshenv(), encoding);
        File.WriteAllText(Path.Combine(zshDir, ".zprofile"), BuildZprofile(), encoding);
        File.WriteAllText(Path.Combine(zshDir, ".zlogin"), BuildZlogin(), encoding);
        string path = Path.Combine(zshDir, ".zshrc");
        File.WriteAllText(path, BuildScript(), encoding);
        return path;
    }
}
