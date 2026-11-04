# Pinned collector completeness

The `terminfo.dev@4.0.0` bundle used by the owned adapter schedules 256 app
probes. The retained [input publication](2026-11-04-input-observations.md)
contains 255 graded observations and an ungraded `cursor.reverse-wrap`
collector error. Its `suiteComplete=false` is correct. Successful supplemental
suites and completed ownership receipts do not make this stock suite complete.

## Reproduced geometry declaration defect

In the pinned bundle, `cursor.reverse-wrap` has an app callback that first reads
`ctx.rows`, then `ctx.cols`. Its probe record lacks `termNeedsGeometry: true`.
The shared `createTermContext` getters require both that declaration and an
available geometry measurement. The runner only obtains the per-probe geometry
when the declaration is present. The callback therefore throws
`UndeclaredTerminalGeometry` before sending its first terminal byte.

The collector catch branch deliberately leaves this error ungraded. Its final
completeness calculation requires every expected probe to have an observation
or an explicit not-tested record, and requires no ungraded diagnostics. This
explains the preserved partial result; it does not establish whether Ntilde
supports reverse wrapping.

The independent [offline reproduction](reproduce-reverse-wrap.mjs) extracts the
actual probe factory, reverse-wrap callback and context implementation from a
SHA-256-verified bundle. It does not import the bundle, dispatch its CLI, launch
an App or write terminal bytes. It asserts these controls:

- The original probe throws even when supplied valid 24-by-80 geometry.
- A synthetic copy with the declaration still throws without geometry.
- That copy with a one-row grid returns the callback's explicit inconclusive
  result without sending bytes.
- Declared, available geometry can be read through the original context getters.

The synthetic copy is a diagnostic control, not an adapter modification or a
new reverse-wrap measurement. The [retained diagnostic JSON](2026-11-04-collector-diagnostic.json)
records the results and SHA-256 hashes of all three extracted source fragments.

From the repository root, after preparing the pinned cache as described in
[the QA guide](../QA_TERMINFO.md):

```sh
node docs/qa/terminfo/reproduce-reverse-wrap.mjs artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs
```

The command reads the bundle and prints JSON; it creates no files. It refuses
any bundle digest other than
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`.
The probe hash remains `16a690f34c0e` and collector revision remains
`cf7d8759c7e637aa8dba1e429f3dddd1c0124158`.

## Remaining boundary

This is an explicitly documented pinned-collector limitation tracked by
[#776](https://github.com/benyblack/ntilde/issues/776). An intentional upstream
upgrade or separately retained derivation must add the declaration, prove
owned geometry before and after the callback, and collect a fresh result with
its actual source/receipt identity. It must not manufacture a grade or set
`suiteComplete=true` in an existing artifact. No upstream fix or report is
claimed here.

The original **78 supported / 0 unsupported / 177 inconclusive** baseline and
later **82 / 0 / 173** stock result remain separate, unchanged observations.
Both exclude this ungraded diagnostic. The supplemental state, query, pixel and
generated-input measurements remain separately scoped; none supplies a combined
terminal-emulator compliance score or Windows/Linux desktop qualification.
