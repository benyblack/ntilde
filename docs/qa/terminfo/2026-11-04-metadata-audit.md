# Complete metadata archive reproduction — 2026-11-04

Both published metadata archives were extracted into fresh temporary directories.
Before loading their archived modules, all entry SHA-256 digests, source bytes
against the recorded Git commit, archive digest and PNG/report bindings were
checked. The complete original archives remain unchanged; the audit removes and
recreates the receipt only in the extracted temporary copy.

- Original measurement: `dc32fc8ee9ee8704f6066da8be057f6aa355ed94`, 164 files,
  archive SHA-256 `21fb61ba9c21dd59480a026a7391a37ec97821bd26ef8f62b3ae2d3ffdc1b675`.
- Captured-source measurement: `1f4403f222ee0095d9b2d8a486009fd38254ed71`, 166 files,
  archive SHA-256 `688361762a0c99cbb95ac92527c11d3b7d2e0947f8bf0e637edf305334204863`.

The following exact derivation ran with Node v24.22.1 in the authorized local
environment. Save it as an `.mjs` file and supply a fresh extracted archive root
as its only argument. Every import resolves to the archived source graph, not
the current checkout. This complements the independent reviewer calculations;
it does not independently attest the runtime, binary/source association, or the
original process/filesystem after teardown.

```js
import assert from 'node:assert/strict';
import { readFileSync, unlinkSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
const root = process.argv[2];
const load = file => JSON.parse(readFileSync(join(root, file), 'utf8'));
const context = load('context.json'), receipt = load('cell-receipt.json');
const runId = context.runId;
for (const [prefix, module, method] of [
    ['cell', 'cells', 'buildCellReport'], ['sgr', 'sgr', 'buildSgrReport'], ['rgb', 'rgb', 'buildRgbReport'],
    ['rendition', 'rendition', 'buildRenditionReport'], ['cursor', 'cursor', 'buildCursorReport'],
    ['pixel', 'pixels', 'buildPixelReport'], ['color', 'colors', 'buildColorReport'],
    ['erase', 'editing', 'buildEraseReport'], ['editing', 'editing', 'buildCellEditingReport'],
    ['history', 'history', 'buildHistoryReport'], ['resize', 'resize', 'buildResizeReport'],
    ['saved', 'saved', 'buildSavedReport'], ['metadata', 'metadata', 'buildMetadataReport'],
]) {
    const report = load(prefix + '-observations.json');
    const journal = readFileSync(join(root, prefix + '-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(journal, report.snapshots, prefix + ' complete journal');
    const source = await import(pathToFileURL(join(root, 'sources', module + '.mjs')).href);
    const args = prefix === 'resize' ? [journal, report.actions, runId] : [journal, runId];
    if (prefix === 'pixel' || prefix === 'color') args.push(root);
    assert.deepEqual(source[method](...args), report, prefix + ' exact source-derived report');
    console.log('PASS full archived-source report and complete journal:', prefix, journal.length);
}
const { completeOwnedReceipt } = await import(pathToFileURL(join(root, 'sources/receipt.mjs')).href);
const evidence = { ...receipt };
for (const key of ['schemaVersion', 'ownershipProofSha256', 'contextSha256', 'rawSha256', 'fixtureRemoved', 'completedAt']) delete evidence[key];
unlinkSync(join(root, 'cell-receipt.json'));
const derived = completeOwnedReceipt(root, dirname(load('initial-settings.json').Profiles[0].Command), evidence);
assert.deepEqual({ ...derived, completedAt: receipt.completedAt }, receipt);
console.log('PASS complete v8 finalizer: every required binding/hash/count and cleanup check reproduced');
```

Both archives produced the following complete comparison results:

```text
PASS full archived-source report and complete journal: cell 22
PASS full archived-source report and complete journal: sgr 44
PASS full archived-source report and complete journal: rgb 48
PASS full archived-source report and complete journal: rendition 50
PASS full archived-source report and complete journal: cursor 22
PASS full archived-source report and complete journal: pixel 40
PASS full archived-source report and complete journal: color 35
PASS full archived-source report and complete journal: erase 29
PASS full archived-source report and complete journal: editing 47
PASS full archived-source report and complete journal: history 63
PASS full archived-source report and complete journal: resize 12
PASS full archived-source report and complete journal: saved 57
PASS full archived-source report and complete journal: metadata 46
PASS complete v8 finalizer: every required binding/hash/count and cleanup check reproduced
```

All thirteen source-defined reports equal their complete retained journals and
original reports, including the PNG-dependent reports. All v8 fields and bindings
match exactly except the newly generated `completedAt`, which is compared using
the original timestamp. The historical archive retains its original 51-file
source-integrity scope; it does not acquire capture-before-import protection.
Only the newer archive binds the 52-file captured graph and source-tree removal.

These checks preserve the RGB-bold pixel negative, RIS/both-banks negative,
empty-title negative, both inconclusive P probes, and the frozen stock grades.
