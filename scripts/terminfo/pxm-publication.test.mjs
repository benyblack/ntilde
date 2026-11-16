import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { inputPlanForSuiteId } from './input-plan.mjs';
import { pxmPublication, measuredAppRevision, measuredScriptsRevision } from './pxm-publication.mjs';
import { buildPxmGradingReport } from './pxm-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-sgr-pixel-mouse/', import.meta.url));
// The measured publication is retained after the GUI collection; until it exists these checks have nothing to rederive.
const skip = existsSync(join(dir, 'manifest.json')) ? false : 'the SGR-pixel publication has not been retained yet';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
const jsonLines = (root, name) => readFileSync(join(root, name), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
const hex = text => Buffer.from(text, 'latin1').toString('hex');

/** The retained final run, extracted once into a fresh directory and re-issued without the collector. */
function withRetainedRun(body) {
    const manifest = JSON.parse(read('manifest.json'));
    const root = mkdtempSync(join(tmpdir(), 'nt-pxm-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        return body(root, manifest);
    } finally { rmSync(root, { recursive: true, force: true }); }
}

test('the manifest binds the final run archive', { skip }, () => {
    const manifest = JSON.parse(read('manifest.json'));
    assert.ok(Object.keys(manifest.archives).includes('owned-run.zip'));
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
});

test('retained run independently rederives the complete receipt and published projection', { skip }, () => withRetainedRun(root => {
    const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
    const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
    rmSync(join(root, 'cell-receipt.json'));
    const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
    assert.deepEqual({ ...derived, completedAt }, original);
    // The retained report is rebuilt from the retained input journal, action log, ownership seal and worker run log, byte for byte.
    const context = JSON.parse(readFileSync(join(root, 'context.json'))), proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json')));
    const retained = readFileSync(join(root, 'pxm-observations.json')), published = JSON.parse(retained);
    const input = { frames: jsonLines(root, 'input-snapshots.jsonl'), actions: jsonLines(root, 'input-actions.jsonl'), seal: proof.proofs[0].input.seal };
    const rebuilt = buildPxmGradingReport(input, JSON.parse(readFileSync(join(root, 'pxm-run.json'))), published.runId, published.adapter, {
        contractsVersion: published.contractsVersion, inputPlan: inputPlanForSuiteId(context.inputSuiteId), inputReportSha256: original.inputEvidence.sha256 });
    assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
    assert.equal(original.pxmEvidence.sha256, hash(retained));
    const projected = pxmPublication(root);
    // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
    projected.summary.receipt = original;
    assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
    assert.deepEqual(projected.report, JSON.parse(read('report.json')));
    assert.equal(fixtureRemoved, true); assert.equal(schemaVersion, original.schemaVersion);
    assert.equal(projected.summary.appRevision, measuredAppRevision);
    assert.equal(projected.summary.scriptsRevision, measuredScriptsRevision);
    assert.equal(projected.report.inputSuiteId, 'ntilde-owned-routed-input-v5');
    assert.equal(projected.report.restoredSnapshot, input.frames.length - 1);
}));

test('the pinned verdict is inconclusive, the effect contract is supported, and the divergence is recorded', { skip }, () => {
    const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json'));
    const [item] = report.cases;
    assert.deepEqual(report.cases.map(entry => entry.caseId), ['input.pixel-mouse']);
    assert.deepEqual(item.pinnedFeeds, []);
    assert.deepEqual(item.pinned.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: 'No generated input event and encoded report were validated' });
    assert.equal(item.pinned.pass, false);
    assert.equal(item.conclusion.outcome, 'inconclusive'); assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.equal(item.effectContract.id, 'ntilde-sgr-pixel-mouse-1016-effect-v1');
    assert.equal(item.effectContract.outcome, 'supported'); assert.equal(item.contractResult.outcome, 'supported');
    assert.match(item.effectContract.attribution, /not the pinned probe/);
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(item.stock.outcome, 'inconclusive');
    assert.deepEqual(summary.receipt.pxmEvidence.counts, { 'inconclusive/supported': 1 });
    assert.equal(summary.receipt.inputEvidence.counts.passed, 196);
});

test('every experiment matched its literal expectation and the independent oracle, with the decisive pixel bytes the child received', { skip }, () => {
    const [item] = JSON.parse(read('report.json')).cases;
    const roles = item.experiments.reduce((accumulator, experiment) => { accumulator[experiment.role] = (accumulator[experiment.role] ?? 0) + 1; return accumulator; }, {});
    assert.deepEqual(roles, { control: 2, required: 30 });
    for (const experiment of item.experiments) {
        assert.equal(experiment.matched, true, experiment.id);
        assert.equal(experiment.actualHex, experiment.expectedHex, experiment.id);
        assert.equal(experiment.oracleHex, experiment.expectedHex, experiment.id);
        assert.equal(experiment.appFacts.boundary, 'avalonia-routed'); assert.equal(experiment.appFacts.eventDispatched, true);
    }
    const bytes = id => item.experiments.find(experiment => experiment.id === 'input.pxm.' + id).actualHex;
    assert.equal(bytes('control.legacy.press'), hex('\x1b[M \x80#'));
    assert.equal(bytes('control.sgr.press'), hex('\x1b[<0;96;3M'));
    assert.equal(bytes('press.left.origin'), hex('\x1b[<0;1;1M'));
    assert.equal(bytes('press.left.mid'), hex('\x1b[<0;144;58M'));
    assert.equal(bytes('press.mods7'), hex('\x1b[<28;144;58M'));
    assert.equal(bytes('press.far'), hex('\x1b[<0;558;262M'));
    assert.equal(bytes('release.left'), hex('\x1b[<0;144;58m'));
    assert.equal(bytes('wheel.up'), hex('\x1b[<64;144;58M'));
    assert.equal(bytes('motion.next-pixel'), hex('\x1b[<32;145;58M'), 'an adjacent pixel in the same cell is reported');
    assert.equal(bytes('hover.same-pixel'), '', 'the same pixel again is not');
    assert.equal(bytes('x10.press'), hex('\x1b[<0;144;58M'), 'X10 press-only tracking carries no modifier bits');
    assert.equal(bytes('slot.pixel.replaces.1006'), hex('\x1b[<0;144;58M'));
    assert.equal(bytes('slot.sgr.replaces'), hex('\x1b[<0;96;3M'));
    assert.equal(bytes('slot.reset.active'), hex('\x1b[M \x80#'), 'the reset of the active pixel encoding falls back to legacy bytes');
});
