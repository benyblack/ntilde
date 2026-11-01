#!/usr/bin/env python3
"""Shrink the test outputs the build job uploads to what a test runner on the same OS and CPU loads.

The build is RID-less, so every test project's output carries every platform's native assets
under runtimes/<rid>/: SkiaSharp, HarfBuzzSharp and ANGLE for win-x64, win-x86 and win-arm64,
thirteen Linux RIDs, and macOS. The Windows packages also ship their native debug symbols there
(libSkiaSharp.pdb is 84 MB, libHarfBuzzSharp.pdb 23 MB, per architecture). Together that was
about 590 MB per output folder that references Skia, which made the build artifact ~774 MB and
its upload ~70 s of the Windows build job, plus the download in every job that consumes it.

Every consumer of `dotnet-build-<os>` runs on that same OS, and the .NET host only probes the
runtimes/ folders on the current RID's fallback chain (win-x64 -> win; linux-x64 -> linux ->
unix; osx-arm64 -> osx -> unix). So this keeps exactly those folders, plus same-arch variants
of the family (linux-musl-x64 and the like - unused on these runners, kept so the rule stays a
simple family-and-arch test), deletes the rest, and deletes native .pdb files under runtimes/.
Managed .pdb files beside the assemblies are kept: they are what puts line numbers in a failing
test's stack trace.

Usage: trim-test-outputs.py <Windows|Linux|macOS> <X64|ARM64|X86|ARM> <root> [<root> ...]
       (the OS and arch are $RUNNER_OS and $RUNNER_ARCH). Prints what it removed.
Tested by scripts/tests/trim_test_outputs_tests.py.
"""

import os
import shutil
import sys

FAMILY = {"Windows": "win", "Linux": "linux", "macOS": "osx"}
ARCH = {"X64": "x64", "ARM64": "arm64", "X86": "x86", "ARM": "arm"}


def keeps(rid: str, family: str, arch: str) -> bool:
    if rid == family:
        return True
    if family != "win" and rid == "unix":
        return True
    return rid.startswith(family + "-") and rid.endswith("-" + arch)


def size(path: str) -> int:
    if os.path.isfile(path):
        return os.path.getsize(path)
    total = 0
    for root, _, files in os.walk(path):
        for name in files:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                pass
    return total


def trim(root: str, family: str, arch: str) -> tuple[int, int]:
    """Returns (bytes removed, entries removed) under root."""
    removed_bytes = removed = 0
    for dirpath, dirnames, _ in os.walk(root):
        if os.path.basename(dirpath) != "runtimes":
            continue
        for rid in list(dirnames):
            rid_dir = os.path.join(dirpath, rid)
            if not keeps(rid, family, arch):
                removed_bytes += size(rid_dir)
                shutil.rmtree(rid_dir)
                dirnames.remove(rid)
                removed += 1
                continue
            native = os.path.join(rid_dir, "native")
            if os.path.isdir(native):
                for name in os.listdir(native):
                    if name.lower().endswith(".pdb"):
                        path = os.path.join(native, name)
                        removed_bytes += size(path)
                        os.remove(path)
                        removed += 1
    return removed_bytes, removed


def main() -> int:
    if len(sys.argv) < 4 or sys.argv[1] not in FAMILY or sys.argv[2] not in ARCH:
        print("usage: trim-test-outputs.py <Windows|Linux|macOS> <X64|ARM64|X86|ARM> <root>...",
              file=sys.stderr)
        return 2
    family, arch = FAMILY[sys.argv[1]], ARCH[sys.argv[2]]
    total_bytes = total = 0
    for root in sys.argv[3:]:
        if not os.path.isdir(root):
            print(f"trim-test-outputs: {root} does not exist; nothing to trim there")
            continue
        removed_bytes, removed = trim(root, family, arch)
        total_bytes += removed_bytes
        total += removed
        print(f"trim-test-outputs: {root}: removed {removed} entries, {removed_bytes / 1e6:.0f} MB")
    print(f"trim-test-outputs: kept {family} / {family}-*-{arch} runtimes; "
          f"removed {total} entries, {total_bytes / 1e6:.0f} MB in all")
    return 0


if __name__ == "__main__":
    sys.exit(main())
