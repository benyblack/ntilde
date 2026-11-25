import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { dslPublication } from './dsl-publication.mjs';
import { buildDslGradingReport } from './dsl-grading.mjs';
import { dslCaseIds } from './dsl-contracts.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-25-decsclm/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    const manifest = JSON.parse(manifestBytes);
    assert.deepEqual(Object.keys(manifest.archives), ['owned-run.zip'], 'the one complete run: the first attempt passed, so nothing is superseded');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-dsl-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'dsl-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'dsl-observations.json'));
        const rebuilt = buildDslGradingReport(frames, JSON.parse(readFileSync(join(root, 'dsl-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = dslPublication(root, { appRevision: JSON.parse(read('summary.json')).appRevision, scriptsRevision: JSON.parse(read('summary.json')).scriptsRevision });
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => item.caseId), dslCaseIds);
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
            // Every ordinary exchange finished inside the bound, the held control reached its hold, and every burst ended identically.
            const { timing, groups } = item.observed;
            assert.deepEqual([timing.boundMs, timing.pauseMs, timing.burstLines, timing.clock], [2000, 2500, 40, 'parent-monotonic']);
            for (const [role, group] of Object.entries(groups)) {
                if (role === 'paced') assert.ok(group.elapsedMs >= timing.pauseMs, `the instrument control read ${group.elapsedMs} ms for its ${timing.pauseMs} ms hold`);
                else assert.ok(group.elapsedMs <= timing.boundMs, `${role} took ${group.elapsedMs} ms`);
                assert.deepEqual(group.app, { rows: item.observed.geometry.rows, cols: item.observed.geometry.cols });
            }
            assert.ok(['burst-off', 'burst-set', 'burst-held', 'burst-reset', 'paced'].every(role => groups[role].burst === true && groups[role].matches === true));
            assert.ok(Object.entries(groups).filter(([, group]) => group.unchanged !== null).every(([, group]) => group.unchanged === true));
            assert.equal(Math.round(timing.burstLines / timing.smoothLinesPerSecond * 1000), timing.smoothFloorMs);
        }
        // A copy whose retained journal was tampered with is refused rather than projected.
        const tampered = (mutate, pattern) => {
            const copy = mkdtempSync(join(tmpdir(), 'nt-dsl-tamper-'));
            try {
                extractOwnedArchive(dir, manifest, copy, join(dir, 'owned-run.zip'));
                const file = join(copy, 'dsl-observations.json');
                const report = JSON.parse(readFileSync(file));
                mutate(report);
                writeFileSync(file, JSON.stringify(report, null, 2) + '\n');
                assert.throws(() => dslPublication(copy, { appRevision: projected.summary.appRevision, scriptsRevision: projected.summary.scriptsRevision }), pattern);
            } finally { rmSync(copy, { recursive: true, force: true }); }
        };
        tampered(report => { report.cases[0].observed.groups['burst-set'].elapsedMs = 9; }, /Inconsistent decsclm publication run or receipt binding/);
        tampered(report => { report.cases[0].effectContract.outcome = 'supported'; }, /Inconsistent decsclm publication run or receipt binding/);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 157, supported: 97, unsupported: 1 });
        assert.equal(schemaVersion, 82); assert.equal(fixtureRemoved, true);
        assert.match(projected.summary.appRevision, /^[0-9a-f]{40}$/);
        assert.match(projected.summary.scriptsRevision, /^[0-9a-f]{40}$/);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
