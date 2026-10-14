#!/usr/bin/env bash
# Builds ntilde-mux for a Linux RID inside ubuntu:22.04 (glibc 2.34 floor), the release recipe.
# Usage: scripts/docker-publish-mux-daemon.sh linux-x64 artifacts/mux-daemon
#
# The local twin of ci.yml's mux_daemon_aot and release.yml's publish_mux_daemon Linux legs
# (docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md §2 decision 1, §10). The toolchain lines
# are copied from release.yml - the apt list and the checksum-verified .NET SDK from publish_linux,
# the pinned, checksum-verified rustup-init from build_native_linux - so a binary built here is
# built the way the release builds it. Only the $GITHUB_PATH/$GITHUB_ENV lines differ: a plain
# container has neither, so they are exports. Keep the copies in step when release.yml changes.
#
# Writes <outdir>/<rid>/ntilde-mux: one file, rusty_pty linked in statically. Fails when the
# publish leaves anything else beside it, or when the binary needs a glibc newer than 2.34.
#
# Runs from Linux, macOS or Git Bash on Windows. linux-arm64 on an x64 machine runs the container
# under emulation (--platform): NativeAOT does not cross-compile between architectures.
set -euo pipefail

rid="${1:?usage: $0 <linux-x64|linux-arm64> <outdir>}"
out="${2:?usage: $0 <linux-x64|linux-arm64> <outdir>}"
case "$rid" in
    linux-x64) platform=linux/amd64 ;;
    linux-arm64) platform=linux/arm64 ;;
    *) echo "docker-publish-mux-daemon.sh: '$rid' is not a Linux RID this builds (linux-x64, linux-arm64)." >&2; exit 2 ;;
esac

# Docker Desktop on Windows takes a Windows path for -v, while Git Bash's pwd prints /d/...;
# `pwd -W` is Git Bash's Windows spelling, and it fails everywhere else, where the POSIX path is
# the right one. MSYS_NO_PATHCONV stops Git Bash rewriting the container-side paths (/src, /out)
# and the script argument as Windows paths.
export MSYS_NO_PATHCONV=1
host_path() { (cd "$1" && { pwd -W 2>/dev/null || pwd; }); }

repo="$(host_path "$(dirname "$0")/..")"
mkdir -p "$out"
out_host="$(host_path "$out")"

# A quoted heredoc, so the lines copied from release.yml stay as they are there. Read into a
# variable (not through $(cat ...), whose parsing of a heredoc full of `case` arms bash 3.2 gets
# wrong) and passed as bash -c's argument rather than on stdin, where any step that reads stdin
# would swallow the rest of the script.
IFS= read -r -d '' container_script <<'CONTAINER' || true
set -euo pipefail
export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1

# --- release.yml publish_linux: Install container toolchain ---
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y \
  ca-certificates curl git unzip zstd jq \
  clang zlib1g-dev binutils file imagemagick squashfs-tools \
  fontconfig libfreetype6 x11-utils \
  libicu70 libssl3
echo "--- container identity (the glibc floor this lane pins) ---"
head -2 /etc/os-release
sed -n '1p' <<<"$(ldd --version)"

# Only what ntilde-mux builds from, so no bin/obj or cargo target of the host's checkout reaches
# the build. rusty_ssh is left out: Ntilde.Mux never touches it (spec §2 decision 1).
mkdir -p /w && cd /w
tar -C /src \
  --exclude='*/bin' --exclude='*/obj' \
  --exclude='src/Ntilde.App/native/target*' --exclude='src/Ntilde.App/native/rusty_ssh' \
  -cf - \
  global.json Directory.Build.props Directory.Packages.props .editorconfig \
  src/Ntilde.Mux src/Ntilde.Mux.Contracts src/Ntilde.Mux.Daemon src/Ntilde.Pty src/Ntilde.VT src/Ntilde.Replay \
  src/Ntilde.App/native \
  | tar -xf -

# --- release.yml publish_linux: Install .NET SDK (version pinned by global.json) ---
sdk_version="$(jq -re '.sdk.version' global.json)"
case "$(uname -m)" in
  x86_64)  sdk_rid=linux-x64 ;;
  aarch64) sdk_rid=linux-arm64 ;;
  *) echo "no pinned .NET SDK download for architecture $(uname -m)" >&2; exit 1 ;;
esac
tarball="dotnet-sdk-${sdk_version}-${sdk_rid}.tar.gz"
base_url="https://builds.dotnet.microsoft.com/dotnet/Sdk/${sdk_version}"
if [[ -d "$HOME/.dotnet/sdk/$sdk_version" ]]; then
  # release.yml restores it with actions/cache; a fresh container never takes this branch.
  echo "SDK $sdk_version already present in \$HOME/.dotnet (cache hit); skipping download"
else
  curl --proto '=https' --tlsv1.2 -sSfL -o "/tmp/$tarball"        "$base_url/$tarball"
  curl --proto '=https' --tlsv1.2 -sSfL -o "/tmp/$tarball.sha512" "$base_url/$tarball.sha512"
  expected="$(awk 'NR==1 { print tolower($1) }' "/tmp/$tarball.sha512")"
  [[ "$expected" =~ ^[0-9a-f]{128}$ ]] \
    || { echo "::error::$tarball.sha512 is not a SHA-512 hex digest (got '${expected:0:80}') - refusing to install an unverified SDK" >&2; exit 1; }
  actual="$(sha512sum "/tmp/$tarball" | awk '{ print tolower($1) }')"
  if [[ "$expected" != "$actual" ]]; then
    echo "::error::sha512 mismatch for $tarball" >&2
    echo "  expected: $expected" >&2
    echo "  actual:   $actual" >&2
    exit 1
  fi
  echo "sha512 verified for $tarball: $actual"
  mkdir -p "$HOME/.dotnet"
  tar -xzf "/tmp/$tarball" -C "$HOME/.dotnet"
  [[ -x "$HOME/.dotnet/dotnet" && -d "$HOME/.dotnet/sdk/$sdk_version" ]] \
    || { echo "::error::extracted SDK has no dotnet/sdk/$sdk_version - wrong payload shape" >&2; exit 1; }
fi
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH" DOTNET_ROOT="$HOME/.dotnet"
command -v dotnet
dotnet --version

# --- release.yml build_native_linux: Install Rust ---
RUSTUP_VERSION=1.29.1
triple="$(uname -m)-unknown-linux-gnu"
base_url="https://static.rust-lang.org/rustup/archive/${RUSTUP_VERSION}/${triple}"
curl --proto '=https' --tlsv1.2 -sSf -o /tmp/rustup-init "${base_url}/rustup-init"
curl --proto '=https' --tlsv1.2 -sSf -o /tmp/rustup-init.sha256 "${base_url}/rustup-init.sha256"
(cd /tmp && sha256sum -c rustup-init.sha256)
chmod +x /tmp/rustup-init
/tmp/rustup-init -y --default-toolchain stable --profile minimal
export PATH="$HOME/.cargo/bin:$PATH"
command -v cargo
cargo --version
rustc --version

# --- the build: librusty_pty.a, then the NativeAOT publish that links it ---
(cd src/Ntilde.App/native && cargo build --release --locked)

pub="/out/$RID"
rm -rf "$pub"
dotnet publish src/Ntilde.Mux.Daemon/Ntilde.Mux.Daemon.csproj -c Release -r "$RID" -nodeReuse:false -o "$pub"
rm -f "$pub"/*.dbg

# One file: rusty_pty is inside it, not beside it.
listing="$(ls -A "$pub")"
if [[ "$listing" != "ntilde-mux" ]]; then
  echo "expected exactly ntilde-mux in $pub, found:" >&2
  echo "$listing" >&2
  exit 1
fi

# The glibc floor: no symbol version above GLIBC_2.34 (RHEL 9; ubuntu:22.04 links against 2.35).
glibc_max="$(objdump -T "$pub/ntilde-mux" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1)"
echo "highest GLIBC_ symbol version: $glibc_max"
if [[ "$(printf '%s\n' "$glibc_max" GLIBC_2.34 | sort -V | tail -1)" != "GLIBC_2.34" ]]; then
  echo "ntilde-mux needs $glibc_max, above the GLIBC_2.34 floor" >&2
  exit 1
fi

echo "size: $(stat -c %s "$pub/ntilde-mux") bytes"
"$pub/ntilde-mux" --version --json

# The container runs as root: hand the output back to the caller on a Linux host. A Docker
# Desktop bind mount (Windows, macOS) may refuse, which changes nothing there.
chown -R "$HOST_UID:$HOST_GID" "$pub" 2>/dev/null || true
CONTAINER

docker run --rm --platform "$platform" \
    -v "$repo:/src:ro" -v "$out_host:/out" \
    -e RID="$rid" -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
    ubuntu:22.04 bash -c "$container_script"
