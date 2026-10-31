#!/usr/bin/env bash
# Decides whether a CI job's `dotnet build` / `dotnet publish` may skip compiling the Rust native
# libraries (SKIP_RUST_NATIVE_BUILD=1), and prints exactly one line for $GITHUB_OUTPUT:
#
#   skip=1   every library this OS loads is already on disk (restored from the native-bin cache,
#            or just built by the job's own cargo step)
#   skip=    something is missing, so MSBuild's BuildRustNative / BuildRustSshNative targets must
#            build it, exactly as they did before this script existed
#
# Usage: native-libs-present.sh <Windows|Linux|macOS> [repo-root]   (the OS is $RUNNER_OS)
#
# Why it exists: with the libraries already restored, MSBuild still ran cargo inside the build
# step - rusty_ssh because it was never cached and its up-to-date check compares timestamps that
# a fresh checkout always makes newer, rusty_pty because its stamp check called the cached binary
# stale. That cost about 2.5 minutes of the Windows build on every PR, on the critical path, with
# no cargo registry or target cache behind it. Checking for the files rather than trusting the
# cache-hit flag means an incomplete payload falls back to the old behaviour instead of shipping
# a build with no native library (the DllNotFoundException IncludeNativeLibrariesInOutput in
# Ntilde.App.csproj describes).
#
# Always exits 0: the fallback is a slower build, never a failed one.
# Tested by scripts/tests/native_libs_present_tests.py.
set -u

os="${1:-}"
root="${2:-.}"
native="$root/src/Ntilde.App/native"

case "$os" in
  Windows)
    libs=("$native/target/release/rusty_pty.dll" "$native/rusty_ssh/target/release/rusty_ssh.dll") ;;
  Linux)
    # Linux consumes the staged copy of rusty_pty, not cargo's own output (see
    # RustPtyNativeStagedOutput in Ntilde.App.csproj).
    libs=("$native/target_linux/release/librusty_pty.so" "$native/rusty_ssh/target/release/librusty_ssh.so") ;;
  macOS)
    libs=("$native/target/release/librusty_pty.dylib" "$native/rusty_ssh/target/release/librusty_ssh.dylib") ;;
  *)
    echo "native-libs-present: unknown OS '$os'; letting MSBuild build the native libraries." >&2
    echo "skip="
    exit 0 ;;
esac

missing=0
for lib in "${libs[@]}"; do
  if [[ -s "$lib" ]]; then
    echo "native-libs-present: found $lib" >&2
  else
    echo "native-libs-present: missing $lib; MSBuild will build the native libraries itself." >&2
    missing=1
  fi
done

if [[ $missing -eq 0 ]]; then
  echo "skip=1"
else
  echo "skip="
fi
