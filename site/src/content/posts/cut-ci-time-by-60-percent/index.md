---
title: "How we cut ntilde's CI time by 60%"
description: "18.7 minutes to about 7, over eight pull requests in one day, without running fewer tests: what each change found and what it saved."
date: 2026-11-01
author: Behnam
---

A typical CI run for ntilde, a cross-platform terminal emulator, went from about 18.7 minutes to about 7 minutes over eight pull requests merged in one day. Every test still runs: each step was checked against the test counts of the run before it, on Windows and Linux. None of the changes was clever. Each one came from reading step timings, finding the next thing on the critical path, and asking why it was there.

## CI time after each change

![A waterfall chart: CI time starts at 18.7 minutes and falls with each change, by 2.5 minutes for #682, 4.8 for #694, nothing for #702, 1.4 for #704, 0.4 for #707, 0.6 for #710, 0.4 for #715 and 1.4 for #717, ending at 7.2 minutes, below a dashed line at half the starting time.](./chart.svg)

*ntilde CI wall time per merged change, 1 November 2026. The start and #682–#704 are typical runs; #707–#717 are single measured runs (9m34s, 8m57s, 8m34s, 7m11s). Runs vary by 10–20 s.*

Two changes did most of the work: caching the Rust libraries and sharding the tests. The others saved between 0.4 and 1.4 minutes each, and several of them removed whole kinds of setup from the test jobs, which keeps the next shard cheap to add.

The last two steps need one note. #715 started the test shards two minutes earlier, but the run then waited on the Linux packaging job instead, so its whole-run saving only shows once #717 shortened that job.

## Where the time went

CI's wall time is its critical path: the longest chain of jobs that wait on each other. For ntilde that chain was two jobs long, and both were slow on Windows.

1. **Build (Windows), about 6.6 minutes.** Set up the .NET SDK and NuGet packages, compile the solution, upload the test outputs. Hidden inside `dotnet build` was a full Rust compile of two native crates, because MSBuild targets call `cargo` whenever the native libraries are missing.
2. **Unit Tests (Windows), about 6 minutes of tests.** The headless App.Tests lane is about 7,200 tests that run strictly one after another, in one process, on one runner.

Everything else (packaging, coverage, Linux, macOS) either ran beside that chain or waited for the build too. So the work was simple to state: make the build job shorter, and make the test job shorter, without running fewer tests.

The tool for finding the next fix was the same every time: step timestamps from the job logs. GitHub's job-level times were skewed by up to an hour in places, so we computed durations from each step's start and end instead.

## Step 1: stop rebuilding Rust on every run ([#682](https://github.com/benyblack/ntilde/pull/682))

**18.7 → about 16.2 minutes.** ntilde's PTY and SSH layers are two Rust crates, compiled to native libraries that the .NET app loads. On every run, `dotnet build` noticed the libraries were missing and compiled both crates from scratch, dependencies and all. The step was called "Build", so the Rust time hid inside it; it only showed up when someone read the Windows log line by line.

The fix caches the finished libraries, not just Cargo's intermediate files:

- A cache keyed on the OS plus a hash of both crates' sources, `Cargo.toml` and `Cargo.lock`. Change a line of Rust and the key changes; otherwise the libraries are restored as they were.
- A small script, `native-libs-present.sh`, checks that every library the OS needs is present and non-empty. Only then does the build skip MSBuild's Rust targets. A half-restored cache falls back to building, never to a broken app.
- An explicit save step. Our first version saved the cache in the post-job phase, and post steps run in reverse order: the Rust cache action's own cleanup had already deleted one crate's output by the time ours saved. The cached payload was missing the SSH library. Saving as a normal step, right after the build, fixed it.

The lesson that stuck: when a step is slow, read what it actually runs. A tidy step name can cover two very different jobs.

## Step 2: split the slowest test lane into shards ([#694](https://github.com/benyblack/ntilde/pull/694))

**About 16.2 → 11.4 minutes.** The App.Tests lane has to run serially inside one process: Avalonia's headless platform is process-wide state, and parallel test collections had caused hangs before. But nothing stops several processes from running on several runners. So the lane now runs as three shards per OS, each its own job.

The risk with sharding is a test that quietly belongs to no shard and stops running. Three rules keep that from happening:

- **Shards are cut by namespace, and the last shard is the complement.** A script lists namespace prefixes for each shard; the final shard's filter is generated as "none of the above". A new namespace lands in that shard automatically.
- **The partition is tested.** A self-test evaluates every shard's real filter against every class declared in the test project, about 745 of them, plus edge cases like `CoreX` (which must not match the `Core.` prefix). It runs at the start of every shard job.
- **Every shard has a floor.** The existing gate already rejects a run that executes fewer tests than expected; this repo once had a run execute 1,112 of 3,434 tests and report green. Each shard now carries its own floor, measured count minus about 2%.

Two smaller changes rode along. A roll-up job, `Unit Tests (<os>)`, gives branch protection one stable check name however the shards change. And the coverage job, which builds for itself, no longer waits for the build job at all.

Sharding has a cost: every shard pays runner setup again, about 80 seconds on Windows. We stopped at three, because each extra shard buys less test time while paying that setup again in full.

## Step 3: fix the flaky tests the extra runs exposed ([#702](https://github.com/benyblack/ntilde/pull/702))

**No time saved, but no time lost to re-runs either.** A faster CI gets run more, and more runs surfaced two tests that failed on timing, not on a bug. A red build that passes on re-run costs a whole CI cycle, so both were fixed at the cause, not retried or skipped.

- **A 2-second deadline against a starved thread pool.** An SSH test waited up to 2 seconds for work the code under test starts with `Task.Run`. While dozens of tests started at once, the thread pool had no free thread to start it, and the work began late. The deadline is now 30 seconds. It is only a cap: a passing test still returns the moment the condition holds.
- **A throughput benchmark judged on one sample.** It failed once at 1.73 MB/s against a 1.75 MB/s threshold, with no parser change, and passed on the next run. It now times three passes and keeps the fastest. A real regression slows every pass; a noisy neighbour on the runner slows only the pass it lands in. The threshold did not move.

## Step 4: upload less ([#704](https://github.com/benyblack/ntilde/pull/704))

**About 11.4 → 10.0 minutes; the Windows build job from about 6.6 to 5.0 minutes.** The build job uploads the compiled tests for the test jobs to download. On Windows that artifact was 774 MB, and the upload alone took about 71 seconds of the critical path.

Most of it was other platforms. The build is RID-less, meaning it targets no single OS and CPU, so every test output folder carried native libraries for every platform the packages support: SkiaSharp, HarfBuzz and ANGLE for three Windows architectures, thirteen Linux variants and macOS. The Windows packages also ship native debug symbols beside their DLLs: 84 MB for SkiaSharp and 23 MB for HarfBuzz, per architecture.

Every job that downloads the artifact runs on the same OS and CPU that built it, and .NET only loads native assets from that platform's own fallback chain (win-x64, then win; linux-x64, then linux, then unix). So a script now deletes every other platform's folder, and the native debug symbols, before the upload. It keeps the managed debug symbols, which are what put line numbers in a failing test's stack trace. Its tests check what must survive as carefully as what must go, because deleting too much fails a test job far from the cause.

| | Before | After |
| --- | --- | --- |
| Windows artifact | 774 MB | 130 MB |
| Upload time | about 71 s | about 12 s |

The same change moved about 40 seconds of Python script self-tests off the build job into a job of their own. They need neither the SDK nor the build, so they had no reason to sit on the critical path.

## Step 5: run the tests, don't re-evaluate the projects ([#707](https://github.com/benyblack/ntilde/pull/707))

**10m02s → 9m34s; the slowest Windows shard from 286 to 252 seconds.** Each shard job only runs tests the build job already compiled. Yet every shard restored a 1 GB NuGet package cache and ran `dotnet restore`, about 35 seconds on Windows. The reason was one argument. `dotnet test <project folder>` evaluates the project through MSBuild, and that fails without the restore output, even with `--no-build --no-restore`. `dotnet test <path to the .dll>` hands the compiled assembly straight to the test runner.

Switching to the DLL form took "Setup .NET" from 37–39 seconds to 3–4 on the shards that no longer restore. Linux shards that run no shell-integration tests also skip an 11–18 second install of zsh and fish.

Which shard needs what is not hard-coded in the workflow. The shard script reports `restore` and `shells` for each shard, worked out from the same filter that picks its tests, so moving tests between shards moves their setup too. Its self-test reads the source: any test class that runs `dotnet build`, or spawns real shells, must land on a shard that sets that up.

### The bug that fell out

The first run of this change failed one test, on Linux only: `Path.GetFullPath("")` in a test of the app's data folder. Nothing in that code had changed. What changed was that `dotnet restore` no longer ran, and restore had been creating `~/.local/share` as a side effect, for NuGet's HTTP cache.

On Linux and macOS, `Environment.GetFolderPath(LocalApplicationData)` returns an empty string when that folder does not exist. Every app-data root in the product was built on it, so on an account that never had `~/.local/share`, the root silently became a relative `ntilde/` folder in whatever directory the process started in. The app and its multiplexer daemon start in different directories, so they would look for each other in different places.

The fix passes `SpecialFolderOption.DoNotVerify` at all eight lookups, in five assemblies, so .NET returns the real path and the app creates it. It changes nothing when the folder already exists. A Linux test now points `XDG_DATA_HOME` at a missing folder, and an architecture test fails if the old lookup comes back. CI had been hiding a real bug, and removing work from CI is what exposed it.

## Step 6: put build-checking tests where the build is ([#710](https://github.com/benyblack/ntilde/pull/710))

**9m34s → 8m57s; the Windows shard jobs from 252 / 245 / 236 seconds to 189 / 182 / 195.** After Step 5, one shard still restored packages, for three tests. They check what a real `dotnet build` produces: the App and the CLI answer `--vt-report`, and the App's output carries the CLI as a named sidecar. In a shard, with no build state, each run compiled the App and its references from scratch. That was about 76 seconds of test time, plus the restore only that shard needed.

The build job had just run that exact build. So those three tests now carry their own category and run there, straight after the build. An environment variable tells them to check the binaries the build produced instead of building again. Without it, on a developer machine or in any other lane, they build first, exactly as before. In the build job they take 7 seconds on Windows and 4 on Linux. The step also requires at least three results, because the test runner treats a filter that matches nothing as a pass.

With that weight gone, the shards were rebalanced from measured per-namespace timings:

| Shard | Holds | Tests | Windows test step |
| --- | --- | --- | --- |
| assist-shell | CommandAssist, Shell, Controls | 2,942 | 149 s |
| core | Core | 1,610 | 132 s |
| rest | everything else, plus the other test projects | 2,817 + 79 | 142 s |

The timing report each shard prints now includes test counts per namespace, so a shard's floor comes from measurement, not an estimate. The counts were identical on Windows and Linux, and with the three build-job tests they total 7,372: exactly what the lane ran on the commit before this change.

## Step 7: use the runner's SDK, and move analyzers out of the build ([#715](https://github.com/benyblack/ntilde/pull/715))

**The Windows build job went from 306 to 180 seconds, and the test shards now start at 3m05s instead of about 5m10s.** One measurement run produced two findings:

- **The SDK was already there.** Every job restored a cached copy of the .NET SDK: 394 MB, about 19 s to unpack on Windows. The hosted images already ship SDK 10.0.403, and `global.json` (10.0.400, `rollForward: latestPatch`) accepts it. Jobs now check `dotnet --version` first and install only when it doesn't resolve, so macOS, arm64 and future images still work. A cold install was no faster, at about 20 s.
- **Analyzers were 45% of the Windows compile.** A timing summary of the build showed 236 s of compile across 28 projects. Turning analyzers off in the Windows build took that step from 180 s to 98 s. They now run in their own `Analyzers` job, in parallel with the build, and nothing waits on it. The roll-up check still requires it, so an analyzer warning still blocks the PR. A guard script also fails if the build skips analyzers while no other job runs them.

One OS is enough for the analyzer job, because no project compiles differently per OS. At first the whole run barely moved, to 8m34s on main: with the tests now finishing earlier, the Linux packaging job had become the last thing to finish.

## Step 8: cache the packaging job's Rust ([#717](https://github.com/benyblack/ntilde/pull/717))

**The Linux Packaging dry run went from about 400 to 246 seconds, and the whole run from about 8.6 to 7.2 minutes.** That job builds inside an `ubuntu:22.04` container on purpose: its glibc is the oldest Linux the .deb supports, so it can't reuse the build job's libraries. Its log showed `rusty_ssh` compiling 164 crates from scratch on every run, about 60 s, because its Rust cache step listed only the other crate's folder.

It now caches the two finished libraries under a key of its own, and on a hit skips installing Rust and cargo entirely. The rest of that run's drop came from a faster AOT publish (118 s against about 190 s), which varies from run to run.

## What we'd tell another team

Most of the eleven and a half minutes we saved came from work CI did that nobody had asked it to do. Six habits found it:

1. **Time the critical path from step timestamps.** Job totals hide which step grew, and a job off the critical path can take as long as it likes.
2. **Read what a step actually runs.** Our "Build" step contained a full Rust compile.
3. **Make coverage provable before you split tests.** A self-tested partition, a per-shard floor and identical counts across OSes are what let us move thousands of tests between jobs without losing one.
4. **Don't pay for setup a job never uses.** Running the compiled test DLLs removed a 1 GB package restore from every shard but one; moving three tests then removed it from that one too.
5. **Run each check where its inputs already are.** The upload carried other platforms' files; the build tests ran far from the build.
6. **Treat a new failure after a CI change as a finding.** One red run in this series was a real product bug that CI's own setup had been hiding.

The next step would be the same loop: read the timings of the newest run, find the longest chain of jobs, and ask why each step in it is there.
