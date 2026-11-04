---
description: Regenerate marketing screenshots and clips, then review every image
---

Regenerate Ntilde's marketing assets and verify each one visually.

Scenarios requested: $ARGUMENTS (empty means `all`).

1. Build and run the harness. Never use raw `dotnet build`:

   `scripts/shots.sh $ARGUMENTS --scale 2` (macOS/Linux) or `scripts/shots.ps1 $ARGUMENTS --scale 2` (Windows)

   `all` runs every scenario `ScenarioCatalog` registers. `remote-files` needs Docker running (it
   connects to a throwaway SSH container); if Docker is down it fails and `--prune` will refuse.
   `--publish` ships only the files `README.md` or `site/scripts/sync-screenshots.mjs` reference;
   review those, and reference a new variant there before expecting it to publish.

2. Read `artifacts/shots/shots.json` to get the produced asset list.

3. **Look at every PNG you produced.** List each scenario's `Intent` with
   `scripts/shots.sh --list`. Two things complicate a naive "one image, one Intent" match:

   - **A scenario can produce more than one image.** `agent-session` produces both
     `agent-session` and `agent-session-journal` from a single `Intent` string. Judge each
     image against the clause of that Intent that actually describes *its own* suffix, not
     the whole string as one blob — an image named `-journal` should not be marked wrong for
     not showing what the base image's clause describes, and vice versa.
   - **Some Intents disclose engineering facts, not visual ones.** `clip-split`'s names the leading-space
     mitigation it uses for a known product race. Judge what the image actually shows against
     the *visual* part of the clause — a disclosed implementation detail is not itself a
     defect to flag.

   For each image, judge:
   - Is the pane full of content, or mostly empty?
   - Is the overlay the scenario opened actually open?
   - Is any text clipped at an edge?
   - Did the intended theme apply?
   - Does the frame look half-drawn — a partial redraw, a missing tab strip?
   - Does anything real leak in: a real username, hostname, path, or branch name?

4. Re-run only the scenarios that failed judgement. The usual causes, in order of likelihood:
   a command captured before its output settled (extend the settle wait), a split taken before
   the previous pane filled, or a theme re-seeded after the window was constructed.

5. When every image passes, publish and report:

   `scripts/shots.sh all --scale 2 --publish --prune`

   `--prune` deletes committed files under `docs/assets/shots/` that this run did not
   (re)publish — the cleanup for a renamed scenario, a dropped variant, or a file the README
   and site stopped referencing. It refuses (a no-op with a warning, not an error) on anything but a full,
   all-scenarios-succeeded run, so it is safe to always include here: a `$ARGUMENTS`-scoped
   `/shots` invocation, or one where a scenario failed, simply skips pruning that time.

   Then give a table of what changed under `docs/assets/shots/`.

Never publish an image you have not looked at.
