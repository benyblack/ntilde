# Direct-local TERM_FEATURES assessment

Ntilde supplied no feature string on the clean launch and preserved the inherited sentinel on the second launch. The same PTY child that sampled its environment sent the capability request before any shell startup.

| Variable | Clean App input | Clean child | Seeded App input | Seeded child |
| --- | --- | --- | --- | --- |
| `TERM_FEATURES` | absent | absent | `ntilde-qa-outer-features` | `ntilde-qa-outer-features` |
| `TERM_PROGRAM` | absent | `Ntilde` | `ntilde-qa-outer-program` | `Ntilde` |
| `NTILDE_QA_ENVIRONMENT_CONTROL` | `ntilde-qa-neutral-clean` | `ntilde-qa-neutral-clean` | `ntilde-qa-neutral-inherited` | `ntilde-qa-neutral-inherited` |

Each child sent BEL-terminated `OSC 1337 ; RequestCapabilities` after a DA preflight. Each received one ST-terminated `OSC 1337 ; Capabilities=T3LrMSc7UTs3BFGsGoSyHNoP` and a matching DA barrier. Both profiles disabled clipboard writes. The exact bytes and both pane identities are in the [rederived report](report.json).

A live settings change can withdraw or restore `Cw` after the process inherits its environment. The direct-local declaration stays deferred. [Per-code evidence and settings policy](../iterm-feature-reporting.md#direct-local-declaration-assessment)

The pinned `device.term-features` callback read its own process environment. With the key absent it reported “TERM_FEATURES not advertised”. With the sentinel it reported “TERM_FEATURES advertised; source unverified”. Both returned `inconclusive` with `insufficient-evidence`. The registration has no termless path. Historical stock was `inconclusive`. This assessment adds zero inventory, Core, Modern or comparison points.

## Reproduction

App 0.14.0 was built from `6d16bd84bdac25cd8b51a0fa7a4554d74702401d`. The collector ran from `d5ef2e6457a621364fbf5c28981a85dfbd7d12e8` on macOS 27.0.1 arm64, Node v24.22.2. The [manifest](manifest.json) pins the archives. Each contains the collector sources, executable and assembly hashes, private settings, child ancestry, kernel restoration proof and desktop lease release.

- [clean archive](clean-run.zip) `606e3cd7be30ce29e4b28c59cd160d53`, App PID 40681, child PID 40696, pane `7dea9384-9be9-4c0f-8052-be93714395a6`.
- [inherited archive](inherited-run.zip) `b4a499063488f8ac050b59a1589d27c4`, App PID 40879, child PID 40883, pane `a38e1103-b02f-45f5-a2c4-d56a096d69ee`.

Rebuild the publication offline with Node 24 and Python 3.

~~~sh
node scripts/terminfo/rich-term-features-results.mjs --check
~~~

For a new macOS arm64 pair, publish the App from the recorded build revision to a private sidecar directory, then run the recorded collector revision.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -o /absolute/private/sidecar
NTILDE_TERMINFO_TERM_FEATURES_CONTROL=clean node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope rich-term-features
NTILDE_TERMINFO_TERM_FEATURES_CONTROL=inherited node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde --app-version 0.14.0 --scope rich-term-features
~~~
