# VS Code OSC 633 aggregate reassessment after #983

The owned effect for extensions.osc-633-vscode is **supported** for A–D lifecycle, E command-line metadata, and P/Cwd on a disposable direct-local macOS App. The six requirements from #984 now have supporting evidence. The [original #984 report](../2026-11-16-shell-integration-aggregates-qualification.md) records the earlier inconclusive result when E and P were pending.

| Exact case | Owned effect | Pinned termless | Pinned terminal | Historical stock |
| --- | --- | --- | --- | --- |
| extensions.osc-633-vscode | Supported for A–E and P/Cwd | Inconclusive | Inconclusive | Inconclusive |

## Reused observations

| Required records | Measured App and collector revision | Owned run | Receipt |
| --- | --- | --- | --- |
| [A–D lifecycle](../2026-11-16-osc633-implementation-qualification.md) | 9c814b13215b6abf9186b0c4c784e7702b3e1828 | 323882a7d7b0e3c86ce4152fa54e6aa3 | Schema 69, source policy v62 |
| [E and P/Cwd](../2026-11-26-osc633-metadata/report.md) | a3319b62029f434b3420ed24fad646d76e163aaa | b106bfc4e964bcdbd07506fcc63efcdb | Schema 1, source policy v86 |

The A–D receipt reissues from the retained case journal and ownership evidence. The four marker effects come from the paired pane and session observations, with unmarked, OSC 133 positive, and wrong-marker controls. The E/P receipt reissues from the separate retained frames and cleanup evidence. E supplied the reported command for one submission and the next submission without E had no reported command. P updated the reported directory twice, including a literal Windows path, and malformed and unknown records preserved the second value.

The E run also observed the shared A–D lifecycle on the newer App. The input window was open after A/B and closed at C as the session entered running. D returned to the prompt with no reported command. These readings connect the lifecycle and metadata criteria across two measured revisions. The aggregate combines their qualified effects. E is unverified status metadata, and P covers Cwd as shell-reported data. The aggregate covers the six listed records, with other P properties and the continuation records outside the evidence set.

## Pinned replay and stock

The exact terminfo.dev 4.0.0 registration was replayed from the retained bundle with both values of the semanticPrompts declaration. The termless callback returns the declaration with an inconclusive verdict. The terminal callback makes no writes or reads and returns inconclusive. The [historical stock baseline](../2026-11-09-stock-baseline.json) supplies the stock grade. This retrospective projection performs no fresh collection.

The [machine-readable report](report.json) binds both carrier manifests, archives, projections, and App binaries. The [summary](summary.json) records both run identities. Both carrier publications must rederive for the current inventory to use this assessment. The inventory retains the earlier inconclusive claim in its history.

Reproduce the two receipt checks, pinned replay, and aggregate verdict with Node 24:

~~~sh
node --test scripts/terminfo/osc633-aggregate-reassessment-publication.test.mjs
~~~
