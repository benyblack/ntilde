import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { dclPublication } from './dcl-publication.mjs';
import { buildDclGradingReport } from './dcl-grading.mjs';
import { dclCaseIds } from './dcl-contracts.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-23-deccolm/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    const manifest = JSON.parse(manifestBytes);
    assert.deepEqual(Object.keys(manifest.archives), ['owned-run.zip'], 'the one complete run: the first attempt passed, so nothing is superseded');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-dcl-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'dcl-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'dcl-observations.json'));
        const rebuilt = buildDclGradingReport(frames, JSON.parse(readFileSync(join(root, 'dcl-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = dclPublication(root, { appRevision: JSON.parse(read('summary.json')).appRevision, scriptsRevision: JSON.parse(read('summary.json')).scriptsRevision });
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => item.caseId), dclCaseIds);
        for (const item of projected.report.cases) {
            assert.equal(item.conclusion.outcome, 'inconclusive', 'the pinned verdict stays inconclusive');
            assert.equal(item.conclusion.reason, 'insufficient-evidence');
            assert.equal(item.effectContract.outcome, 'unsupported', 'the separately identified effect contract');
            assert.equal(item.contractResult.outcome, 'unsupported');
            assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-effect-unsupported' });
            assert.equal(item.pinned.termless.observation.evidence, 'parser-state');
            assert.equal(item.pinned.terminal.observation.evidence, 'query');
            assert.equal(item.stock.outcome, 'inconclusive', 'the stock collector grade is unchanged');
            assert.ok(Object.values(item.observed.groups).every(group => group.matches === true), `${item.caseId} every group held, controls included`);
            assert.equal(Object.keys(item.observed.groups).length, 12);
            // The App and the kernel PTY reported the same, unchanged grid at every group, and it was neither of the two DECCOLM widths.
            const { rows, cols } = item.observed.geometry;
            assert.ok(![80, 132].includes(cols));
            assert.deepEqual([item.observed.geometry.kernelRows, item.observed.geometry.kernelCols], [rows, cols]);
            for (const group of Object.values(item.observed.groups)) assert.deepEqual([group.app, group.kernel], [{ rows, cols }, { rows, cols }]);
            assert.ok(Object.entries(item.observed.groups).filter(([, group]) => group.unchanged !== null).every(([, group]) => group.unchanged === true));
        }
        // The live geometry control is the owned resize suite of the same run: parent-selected App resizes, each followed by the App grid and the kernel PTY grid through the DECCOLM suite's own readers.
        const control = projected.summary.geometryControl;
        assert.equal(control.runId, projected.summary.ownedRunId);
        assert.equal(control.passed, 3);
        assert.deepEqual(control.baseline, { rows: 40, cols: 140 });
        assert.deepEqual(control.actions.map(action => [action.caseId, action.stageId]), [['resize.width-roundtrip', 'after'], ['resize.width-roundtrip', 'restored'],
            ['resize.height-history-roundtrip', 'after'], ['resize.height-history-roundtrip', 'restored'], ['resize.combined-reflow-history-roundtrip', 'after'], ['resize.combined-reflow-history-roundtrip', 'restored']]);
        const [shrunk, restoredWidth] = control.actions;
        assert.deepEqual([shrunk.app, shrunk.kernel], [{ rows: 40, cols: 133 }, { rows: 40, cols: 133 }], 'the width moved in the App and in the kernel together');
        assert.deepEqual([restoredWidth.app, restoredWidth.kernel], [control.baseline, control.baseline], 'and returned to the DECCOLM baseline');
        assert.ok(control.actions.every(action => JSON.stringify(action.app) === JSON.stringify(action.kernel) && JSON.stringify(action.app) === JSON.stringify(action.target)));
        // A copy whose control was tampered with is refused rather than projected.
        const tampered = (mutate, pattern) => {
            const copy = mkdtempSync(join(tmpdir(), 'nt-dcl-control-'));
            try {
                extractOwnedArchive(dir, manifest, copy, join(dir, 'owned-run.zip'));
                const file = join(copy, 'resize-actions.jsonl');
                const lines = readFileSync(file, 'utf8').trim().split('\n').map(line => JSON.parse(line));
                mutate(lines, copy);
                writeFileSync(file, lines.map(line => JSON.stringify(line)).join('\n') + '\n');
                assert.throws(() => dclPublication(copy, { appRevision: projected.summary.appRevision, scriptsRevision: projected.summary.scriptsRevision }), pattern);
            } finally { rmSync(copy, { recursive: true, force: true }); }
        };
        tampered(lines => { lines[0].observed.terminalOutput.description = lines[0].observed.terminalOutput.description.replace(' 133 columns', ' 140 columns'); }, /readers disagree with the target/);
        tampered(lines => { lines[0].observed.app.layoutSettled = false; }, /readers disagree with the target/);
        tampered(lines => { lines[1].observed.app.cols = 139; lines[1].target.cols = 139; lines[1].observed.terminalOutput.description = lines[1].observed.terminalOutput.description.replace(' 140 columns', ' 139 columns'); }, /does not move and restore the DECCOLM baseline grid/);
        tampered(lines => { lines.pop(); }, /six parent-selected actions/);
        tampered(lines => { for (const line of lines) { line.target.cols = 140; line.observed.app.cols = 140; line.observed.terminalOutput.description = line.observed.terminalOutput.description.replace(/ 133 columns/, ' 140 columns'); } }, /does not move and restore/);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 157, supported: 97, unsupported: 1 });
        assert.equal(schemaVersion, 79); assert.equal(fixtureRemoved, true);
        assert.match(projected.summary.appRevision, /^[0-9a-f]{40}$/);
        assert.match(projected.summary.scriptsRevision, /^[0-9a-f]{40}$/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
