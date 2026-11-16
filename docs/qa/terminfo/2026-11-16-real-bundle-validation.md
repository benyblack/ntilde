# Real-bundle validation repair (#977)

The three reported `Unexpected identifier 'render'` failures reproduce on Node
22.23.4, with a fresh `npm ci --ignore-scripts` installation from the retained
manifest/lockfile. The same full tests pass on Node 24.22.2: 16 passed, no failures
or skips before the repair. This is a runtime-version failure, not a malformed
derived collector, changed bundle pin, or terminal protocol defect.

The earliest failing import is
`node_modules/silvery/dist/ag-KPSRZKXk.mjs:10239` (`silvery@0.26.0`):

```js
using render = baseLog.span?.("pipeline", {
```

The locked dependency uses explicit resource management syntax that Node 22 cannot
parse. Syntax checking the derived entry point alone succeeds on both runtimes:
it does not traverse imports. Executing that entry point directly on Node 22
provides the dependency filename and line that the test runner's stack omits.
The full failing stack starts at `compileSourceTextModule`, then
`ModuleLoader.moduleStrategy`, `#translate`, `afterLoad`, `loadAndTranslate`,
`#createModuleJob`, `#getJobFromResolveResult`, `getModuleJobForImport`.

Verified fresh-input identities:

- Bundle SHA-256: `aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`.
- Lockfile SHA-256: `837e8d2efc15e544a3a3e900ec0617854d9cceae0887ad4081bf3b8bfedb0508`.
- Failing dependency module SHA-256: `f559db8d788bf3ed518a4a146e35bb48c33d0adecf0c34021c63fda584f8168d`.

The three tests now refuse unsupported runtimes with a Node 24 requirement before
dependency installation/import. Each executed test creates a fresh, separate
integrity-checked installation, validates the bundle/receipt and removes its
installation afterward; cached dependency bytes never execute. A local cache
still selects optional coverage for ordinary local runs. The dedicated
`terminfo_real_bundle` job in the main CI workflow sets
`NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1`, so the three tests run without a cache
and failed preparation is a failed test, never a skip. Its result gates the
branch-protected Unit Tests roll-up, including failures, skips and cancellations.
They move out of the
portable shards into that required Node 24 lane. A regression proves the
preparation-failure refusal.

Reproduce with Node 24 selected:

```sh
NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED=1 node --test \
  scripts/terminfo/kcl-grading-collector.test.mjs \
  scripts/terminfo/devq-grading-collector.test.mjs \
  scripts/terminfo/mex-grading-collector.test.mjs
node --test scripts/terminfo/real-bundle-test.test.mjs
python3 scripts/tests/node_tests_shard_tests.py
```

No measured production, collector, contract, grader, receipt or source-policy bytes
change. All historical publications retain their evidence and outcomes. This
repair adds validation coverage and makes no feature-support claim.
