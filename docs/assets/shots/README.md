# Marketing screenshots & clips — generated assets

Everything in this directory (except `hero/`, see below) is **generated**, not hand-captured.
`tools/Ntilde.Shots` boots Ntilde's real `MainWindow` headless-with-Skia against an
isolated fake workspace (`DemoWorld`), drives each scenario, and rasterizes the result. Nothing
here should be edited by hand — regenerate it instead:

```bash
scripts/shots.sh all --scale 2 --publish --prune
```

(`scripts/shots.ps1` takes the same arguments on Windows.) Runs on Windows and macOS. On a Mac,
main-window captures get the traffic lights AppKit would draw in the space Ntilde leaves for them;
on Windows they show no caption buttons, as before.

`--prune` deletes committed files under this directory that the run did not (re)publish — the
cleanup for a renamed scenario, a dropped variant, or a WebP sibling that stopped qualifying
under the smaller-than-its-PNG gate. It only ever acts on a full catalogue run where every
scenario succeeded; anything else (a scenario-scoped run, or a run with a failure) makes it a
no-op with a console warning instead of deleting anything, so it is safe to pass by habit.

or, inside Claude Code, run the `/shots` command, which builds, reviews every image against its
scenario's stated `Intent` before publishing, and reports what changed.

## Where things live

- **This directory** — the *published* variants: PNGs (and, where it actually beats the PNG, a
  smaller lossless WebP sibling) sized for their destination:
  - `<name>-readme.png[.webp]` — 1280px wide, for `README.md`.
  - `<name>-site.png[.webp]` — 2400px wide, for the marketing site (`site/public/shots/`).
  - `og-card.png` (1200×630) and `social-square.png` (1080×1080) — social-preview cards, derived
    from `hero-single` only.
  - `clip-*.gif` / `clip-*.webm` — short looping clips.
- **`artifacts/shots/`** (gitignored, not in this repo) — the working directory: Tier 1/2
  masters at full capture resolution, individual clip frames, and `shots.json`, the manifest a
  run writes there. Each entry records the scenario name, file, dimensions, the **git commit**
  and **OS** the capture ran on, and a UTC timestamp — so for any published image you can trace
  back to exactly which build and platform produced it. The masters are not committed; only the
  publish step's derived, checked variants above are.
- **`hero/`** — the two exceptions. `hero-real-single` and `hero-real-split` are captured from
  the **real on-screen window** (drop shadow, rounded corners, acrylic blur — a headless
  `RenderTargetBitmap` can't produce those), by hand, with `scripts/capture-hero.ps1`. See
  `hero/README.md` for the capture procedure and leak-check discipline. Never put a headless
  render in that folder, and never put a hand-captured window screenshot anywhere else in this
  directory.

## Scenarios you won't find here

- **`hero-split`** — registered and runnable, but held back from publishing. At 2x scale the
  bottom-right pane draws its last rows shifted to the right edge even though the terminal buffer
  holds them correctly; 1x renders clean. That is a renderer bug in the app, not in this harness,
  and a published hero image should not show it. Publish it again once the bug is fixed.
- **`remote-files`** — needs a live, connected SSH session the offline harness has no way to
  provide, so `ScenarioCatalog` does not register it.

Each scenario's own source file under `tools/Ntilde.Shots/Scenarios/` documents the
evidence trail in detail, and `ScenarioCatalog.cs` explains what re-enabling one would require.

## If you're adding or changing a scenario

Look at an existing `IScenario` implementation and its scenario file's header comment for the
pattern (a stated `Intent`, the settle/wait discipline, and — where relevant — the leak checks
`DemoWorld` performs). Never commit an image you have not looked at against its `Intent`, and
never let a real username, hostname, path, or branch name appear in a published asset.
