# CLAUDE.md

## Build & test commands

**Always use the wrapper scripts, not raw `dotnet`:**

- Windows / PowerShell: `scripts/build.ps1 <args...>`
- Linux / macOS / Git Bash: `scripts/build.sh <args...>`

Examples:
- `scripts/build.ps1 build src/Ntilde.App`
- `scripts/build.ps1 test`
- `scripts/build.sh build -c Release src/Ntilde.App`

The wrappers pass `-nodeReuse:false` and set `DOTNET_CLI_USE_MSBUILD_SERVER=0`, which is required when stdout/stderr is captured by a parent (Claude Code's Bash tool, CI runners, test harnesses).

### Running the App.Tests gating lanes

Never run `tests/Ntilde.App.Tests` unfiltered. CI runs it as two separate processes, and mixing them makes the mux/remote/window tests time out and the testhost hang (see the PlatformBoot comment in `.github/workflows/ci.yml`). `ci/run.sh` / `ci/run.ps1` run every lane; to run just App.Tests, use the same two lanes CI does:

- `scripts/build.sh test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Lane!=PlatformBoot"`
- `scripts/build.sh test tests/Ntilde.App.Tests --no-build --blame-hang-timeout 5m --filter "Lane=PlatformBoot&Category!=GoldenSharedPng"`

### Why this matters

A raw `dotnet build` invocation spawns long-lived daemons — the MSBuild worker node pool (24h `nodeReuse` default), the dotnet MSBuild build server, and `VBCSCompiler`. These daemons inherit the caller's stdout/stderr handles, so a parent process reading via pipes never sees EOF and hangs indefinitely. The hang typically appears stuck in `BuildCliShim` because that's the last target to emit output before silence.

`<UseSharedCompilation>false</UseSharedCompilation>` in `Directory.Build.props` already kills VBCSCompiler globally, and the `BuildCliShim` / `PublishCliShim` targets in `src/Ntilde.App/Ntilde.App.csproj` pass `-nodeReuse:false` to their nested `dotnet build`. But the **outer** invocation must also pass `-nodeReuse:false`; the project-level settings cannot influence the driver's node-pool startup. The wrapper scripts encode this.

If a build appears stuck in BuildCliShim and you reached for raw `dotnet build`: stop, kill the orphan MSBuild/dotnet daemons (`dotnet build-server shutdown` + Stop-Process on any stale `MSBuild.exe` / `dotnet.exe`), and re-run via the wrapper.

<!-- rtk-instructions v2 -->
# RTK (Rust Token Killer) - Token-Optimized Commands

## Golden Rule

**Always prefix commands with `rtk`**. If RTK has a dedicated filter, it uses it. If not, it passes through unchanged. This means RTK is always safe to use.

If `rtk` is not on PATH (e.g. cloud sessions), run commands without the prefix.

**Important**: Even in command chains with `&&`, use `rtk`:
```bash
# ❌ Wrong
git add . && git commit -m "msg" && git push

# ✅ Correct
rtk git add . && rtk git commit -m "msg" && rtk git push
```

<!-- /rtk-instructions -->