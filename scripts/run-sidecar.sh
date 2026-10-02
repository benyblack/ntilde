#!/usr/bin/env bash
# Build Ntilde, mirror the fresh output to a fixed sidecar directory, and launch it from
# there. Also mirrors the MCP dev-companion server, which is launched separately by an MCP
# client rather than by this script.
#
# This is the macOS / Linux counterpart of run-sidecar.ps1 - same layout, same flags, no pwsh
# required. See that script's header for the full rationale; the short version: a running
# instance (or a long-lived MCP server) launched from the repo's bin/ ties that output to the
# process, a hand-made copy goes stale silently, and this script makes "rebuild, re-mirror,
# launch the copy" one step so the repo bin stays free for build.sh build/test.
#
# Unix doesn't lock loaded DLLs the way Windows does, but the other half still applies here:
# rebuilding underneath a running instance swaps assemblies out from under it, and you can't
# tell from the window which build you're on. The running build is identifiable in debug.log
# via the "Build: sha=... built=... path=..." line.
#
# Kept bash 3.2 compatible: that is still what macOS ships as /bin/bash.
#
# Usage:
#   scripts/run-sidecar.sh                      # Debug build, build + mirror + launch
#   scripts/run-sidecar.sh -c Release           # or --configuration Release
#   scripts/run-sidecar.sh --no-build           # skip the build; mirror current output + launch
#   scripts/run-sidecar.sh --skip-mcp-server    # app only; don't build/mirror the MCP server
#   scripts/run-sidecar.sh --no-launch          # build + mirror only
#   scripts/run-sidecar.sh --sidecar-root DIR   # override sidecar location
#   scripts/run-sidecar.sh --framework net10.0  # which bin/<Configuration>/<tfm>/ to mirror
#
# Defaults can also come from the environment: NTILDE_SIDECAR_ROOT.

set -euo pipefail

configuration=Debug
# Picks which bin/<Configuration>/<tfm>/ folder is mirrored; it is deliberately NOT passed to
# the build as -f. Every project here has a single TargetFramework, and forcing one as a
# global property makes the outer restore disagree with the nested BuildCliShim build
# (NETSDK1064) - and leaves obj/ needing a fresh restore. Change it only alongside the
# projects' own TargetFramework.
target_framework=net10.0
no_build=0
skip_mcp=0
no_launch=0
# Outside the repo so it never collides with bin/obj globbing, IDE watchers, or git status.
# Exactly the default the .ps1 uses off-Windows, so both scripts share one sidecar and the
# documented MCP path holds. Deliberately not $XDG_DATA_HOME: the .ps1 doesn't read it, and
# honouring it here alone would split the two scripts onto different directories.
sidecar_root="${NTILDE_SIDECAR_ROOT:-$HOME/.local/share/ntilde-sidecar}"

usage() {
    sed -n '/^# Usage:/,/^# Defaults/p' "$0" | sed 's/^# \{0,1\}//'
}

die() {
    echo "[sidecar] ERROR: $*" >&2
    exit 1
}

warn() {
    echo "[sidecar] WARNING: $*" >&2
}

info() {
    # Cyan when attached to a terminal, plain otherwise (CI logs, captured output).
    if [ -t 1 ]; then
        printf '\033[36m[sidecar] %s\033[0m\n' "$*"
    else
        printf '[sidecar] %s\n' "$*"
    fi
}

while [ $# -gt 0 ]; do
    case "$1" in
        -c|--configuration)
            [ $# -ge 2 ] || die "$1 needs a value (Debug or Release)"
            configuration="$2"; shift 2 ;;
        -f|--framework)
            [ $# -ge 2 ] || die "$1 needs a value"
            target_framework="$2"; shift 2 ;;
        --sidecar-root)
            [ $# -ge 2 ] || die "$1 needs a value"
            sidecar_root="$2"; shift 2 ;;
        --no-build) no_build=1; shift ;;
        --skip-mcp-server) skip_mcp=1; shift ;;
        --no-launch) no_launch=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "unknown argument: $1" ;;
    esac
done

case "$configuration" in
    Debug|Release) ;;
    *) die "configuration must be Debug or Release, got '$configuration'" ;;
esac

# Absolute before anything is derived from it: the launch below cd's into the sidecar, so a
# relative --sidecar-root would leave $exe and $log_file pointing nowhere - and the
# backgrounded launch would fail after the script had already printed a PID.
mkdir -p "$sidecar_root" || die "cannot create sidecar root: $sidecar_root"
sidecar_root="$(cd "$sidecar_root" && pwd)"

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"

app_project="$repo_root/src/Ntilde.App"
source_dir="$app_project/bin/$configuration/$target_framework"
dest_dir="$sidecar_root/$configuration/$target_framework"

mcp_project="$repo_root/src/Ntilde.McpServer"
mcp_source_dir="$mcp_project/bin/$configuration/$target_framework"
# Separate destination from the app's: both outputs carry the same shared assemblies, and a
# --delete mirror removes anything not in its source, so sharing one directory would have
# each mirror delete the other's private files.
mcp_dest_dir="$sidecar_root/McpServer/$configuration/$target_framework"

# Mirrors $1 onto $2 (adds new, updates changed, deletes stale). Returns non-zero on failure
# rather than exiting: callers decide whether a failed mirror is fatal.
#
# The mirror is staged in a sibling directory and swapped in only once it is complete, so a
# failure (disk full, permissions, an interrupted copy) leaves the previous copy intact rather
# than deleted or half-updated - a partial MCP mirror would otherwise be a broken server that
# the caller only warns about. --link-dest keeps this cheap: files unchanged since the last
# mirror are hard-linked from it instead of copied, which matters for a ~600 MB app output.
sync_directory() {
    local from="$1" to="$2"
    local staging="$to.staging.$$" previous="$to.previous.$$"

    mkdir -p "$(dirname "$to")" || return 1
    rm -rf "$staging" || return 1

    if command -v rsync >/dev/null 2>&1; then
        local link_dest=()
        # rsync resolves --link-dest relative to the destination, so pass it absolute.
        [ -d "$to" ] && link_dest=(--link-dest="$(cd "$to" && pwd)")
        # Trailing slashes => mirror the contents of source into staging.
        if ! rsync -a ${link_dest[@]+"${link_dest[@]}"} "$from/" "$staging/"; then
            rm -rf "$staging"
            return 1
        fi
    else
        # No rsync (minimal Linux images): a full copy. Not incremental, but correct.
        if ! { mkdir -p "$staging" && cp -Rp "$from/." "$staging/"; }; then
            rm -rf "$staging"
            return 1
        fi
    fi

    # Swap: move the old copy aside first so a failed rename can be rolled back. A process
    # already running from the old copy keeps its open files; only the directory entry moves.
    if [ -e "$to" ] && ! mv "$to" "$previous"; then
        rm -rf "$staging"
        return 1
    fi
    if ! mv "$staging" "$to"; then
        [ -e "$previous" ] && mv "$previous" "$to"
        rm -rf "$staging"
        return 1
    fi
    rm -rf "$previous"
}

if [ "$no_build" -eq 0 ]; then
    info "Building Ntilde.App ($configuration)..."
    # Through the wrapper so MSBuild/dotnet daemons don't outlive the build and hang a parent
    # that captures stdout (see CLAUDE.md).
    if ! "$script_dir/build.sh" build "$app_project" -c "$configuration"; then
        die "Build failed. Not launching."
    fi

    if [ "$skip_mcp" -eq 0 ]; then
        info "Building Ntilde.McpServer ($configuration)..."
        if ! "$script_dir/build.sh" build "$mcp_project" -c "$configuration"; then
            # Non-fatal: a stale MCP sidecar must not stop you launching the app.
            warn "MCP server build failed; its sidecar copy may be stale."
            skip_mcp=1
        fi
    fi
fi

[ -d "$source_dir" ] || die "Build output not found: $source_dir. Run without --no-build first."

info "Mirroring fresh output -> $dest_dir"
sync_directory "$source_dir" "$dest_dir" || die "Mirror failed for the app output."

if [ "$skip_mcp" -eq 0 ]; then
    if [ -d "$mcp_source_dir" ]; then
        info "Mirroring MCP server -> $mcp_dest_dir"
        if sync_directory "$mcp_source_dir" "$mcp_dest_dir"; then
            echo "[sidecar] MCP client should point at: dotnet \"$mcp_dest_dir/Ntilde.McpServer.dll\""
        else
            warn "Could not refresh the MCP sidecar. Restart your MCP client and re-run; the repo build is unaffected."
        fi
    else
        warn "MCP server output not found: $mcp_source_dir (skipping its mirror)."
    fi
fi

exe="$dest_dir/Ntilde"
[ -f "$exe" ] || die "Expected executable not found after mirror: $exe"

# A copy doesn't always preserve the execute bit; make sure the apphost is runnable.
chmod +x "$exe" 2>/dev/null || true

if [ "$no_launch" -eq 1 ]; then
    info "Sidecar ready at $exe (--no-launch; not starting it)."
    exit 0
fi

# `date -r FILE` reads a file's mtime on both BSD (macOS) and GNU date; stat's flags differ.
built_at="$(date -r "$exe" '+%Y-%m-%d %H:%M:%S' 2>/dev/null || echo unknown)"
log_file="$sidecar_root/sidecar-launch.log"
info "Launching $exe (built $built_at)"
echo "[sidecar] stdout/stderr -> $log_file"

# Detach fully so this shell returns immediately. Redirecting all three standard handles
# matters: an app that inherits a captured stdout/stderr keeps the caller's pipe open and
# hangs it - the same failure build.sh guards against for MSBuild daemons. setsid (Linux)
# also drops the controlling terminal so closing this shell doesn't SIGHUP the app; macOS has
# no setsid, where nohup covers the hangup.
cd "$dest_dir"
if command -v setsid >/dev/null 2>&1; then
    setsid nohup "$exe" </dev/null >>"$log_file" 2>&1 &
else
    nohup "$exe" </dev/null >>"$log_file" 2>&1 &
fi
echo "[sidecar] Started (pid $!)."
