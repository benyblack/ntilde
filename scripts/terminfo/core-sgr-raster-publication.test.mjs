import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, copyFileSync, mkdtempSync, readFileSync, rmSync, writeFileSync, appendFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { root, reportText, extractOwnedArchive, loadMapping } from '../terminfo-core-results.mjs';
import { deriveSgrRasterCoreReport, readableSgrRasterReport, combineConclusion, previousPublicationReport, previousPublicationReportSha256 } from '../terminfo-core-sgr-raster-results.mjs';
import { buildPublication, loadMeasurement, publication, verifyOutput } from '../terminfo-core-sgr-raster-publication.mjs';
import { deriveWrapCoreReport } from '../terminfo-core-wrap-results.mjs';
import { includesSgrRasterEvidence, checkSgrRasterEvidence } from './core-sgr-raster-receipt.mjs';
import { sgrRasterIds, sgrRasterSuiteId } from './core-sgr-raster.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const meta = loadMeasurement();
/** One extraction of the retained owned run; every mutation restores the exact original bytes before the next test. */
const dir = mkdtempSync(join(tmpdir(), 'nt-sr-run-'));
extractOwnedArchive(root, { archives: { 'owned-run.zip': meta.ownedArchiveSha256 } }, dir, join(root, publication, 'owned-run.zip'));
process.on('exit', () => rmSync(dir, { recursive: true, force: true }));
const json = file => JSON.parse(readFileSync(join(dir, file)));
const identity = { context: json('context.json'), receipt: json('cell-receipt.json'), proof: json('ownership-proof.json') };
const mapping = loadMapping();
const runId = meta.measurement.runId;

/** Derive the 80 rows from the retained run with a mutated copy of the raster observations, optionally over a different preceding publication. */
function derive(mutate = () => {}, previous = undefined) {
    const path = join(dir, 'sr-observations.json'), original = readFileSync(path);
    try {
        const observed = JSON.parse(original); mutate(observed);
        writeFileSync(path, JSON.stringify(observed));
        return deriveSgrRasterCoreReport(dir, mapping, identity, meta.measurement, previous);
    } finally { writeFileSync(path, original); }
}
const effectOf = (observed, id) => observed.effects[id];

test('published 80 unique rows rederive from the owned receipt; the three rows close, every other conclusion and the totals are retained', () => {
    const { report, mapping: used } = buildPublication();
    assert.equal(report.rows.length, 80); assert.equal(new Set(report.rows.map(r => r.id)).size, 80);
    assert.equal(Object.values(report.totals).reduce((a, b) => a + b, 0), 80);
    assert.equal(report.corePassPercentage, report.totals.pass * 100 / 80);
    verifyOutput(readFileSync(join(root, publication, 'report.json'), 'utf8'), reportText(report), 'report.json');
    verifyOutput(readFileSync(join(root, publication, 'report.md'), 'utf8'), readableSgrRasterReport(report, used), 'report.md');
    const earlierBytes = readFileSync(join(root, previousPublicationReport));
    assert.equal(hash(earlierBytes), previousPublicationReportSha256);
    const earlier = JSON.parse(earlierBytes);
    for (const row of earlier.rows) if (!sgrRasterIds.includes(row.id)) assert.equal(report.rows.find(r => r.id === row.id).conclusion, row.conclusion, row.id);
    for (const id of sgrRasterIds) {
        const row = report.rows.find(r => r.id === id), r = row.rasterExperiment;
        assert.equal(row.historicalConclusion, 'inconclusive');
        assert.equal(row.storedStateResult.verdict, 'supported');
        assert.ok(row.evidence.some(e => e.file === 'sr-observations.json' && e.caseId === id));
        assert.equal(row.exactFixtureEffects, undefined, 'raster evidence is its own layer, never an exact-fixture substitution');
        assert.equal(r.steps.length, 18); assert.equal(r.experiment.subsets.length, 7);
        assert.equal(row.conclusion, combineConclusion(row.storedStateResult.verdict, r.outcome));
    }
    const md = readableSgrRasterReport(report, used);
    assert.ok(!md.includes('[object Object]'));
    const altered = structuredClone(report); altered.totals.pass++;
    assert.throws(() => verifyOutput(reportText(altered), reportText(report), 'report.json'), /stale report/);
});

test('the stored-state verdict is this run\'s re-derived one, never the preceding publication\'s', () => {
    // The preceding publication says supported; the current run's re-derived row says otherwise, so no pass may come from the older verdict.
    for (const [verdict, expected] of [['inconclusive', 'inconclusive'], ['unsupported', 'fail']]) {
        const changed = { derive: (...args) => { const r = deriveWrapCoreReport(...args); r.report.rows.find(x => x.id === 'sgr.reset').effectVerdicts[0].verdict = verdict; return r; },
            report: previousPublicationReport, sha256: previousPublicationReportSha256 };
        const { report } = derive(() => {}, changed), row = report.rows.find(x => x.id === 'sgr.reset');
        assert.equal(row.conclusion, expected); assert.equal(row.storedStateResult.verdict, verdict);
        assert.equal(row.storedStateResult.effectVerdicts[0].verdict, verdict);
        assert.equal(row.historicalConclusion, 'inconclusive');
    }
});

test('the combined conclusion needs both layers; neither a stored flag nor a raster result can stand in for the other', () => {
    assert.equal(combineConclusion('supported', 'supported'), 'pass');
    assert.equal(combineConclusion('supported', 'unsupported'), 'fail');
    assert.equal(combineConclusion('unsupported', 'supported'), 'fail');
    assert.equal(combineConclusion('supported', 'inconclusive'), 'inconclusive');
    assert.equal(combineConclusion('inconclusive', 'supported'), 'inconclusive');
});

test('changed measurement, evidence archive or retained attempt is refused', () => {
    const tmp = mkdtempSync(join(tmpdir(), 'nt-sr-pub-'));
    try {
        const path = join(tmp, publication); cpSync(join(root, publication), path, { recursive: true });
        const attempts = loadMeasurement().attempts.filter(a => a.archive).map(a => a.archive);
        for (const file of ['owned-run.zip', 'app-source-manifest.json', 'measurement.json', ...attempts]) {
            const f = join(path, file), original = readFileSync(f); writeFileSync(f, Buffer.concat([original, Buffer.from('x')]));
            assert.throws(() => loadMeasurement(tmp), /changed/); writeFileSync(f, original);
        }
    } finally { rmSync(tmp, { recursive: true, force: true }); }
});

test('the measured retained run satisfies the receipt check, and the three verdicts follow from retained controls (never a label)', () => {
    assert.doesNotThrow(() => checkSgrRasterEvidence(dir, identity.receipt, runId, identity.context));
    assert.equal(includesSgrRasterEvidence(dir, identity.receipt, identity.context), true);
    const observed = json('sr-observations.json');
    for (const id of sgrRasterIds) {
        const effect = effectOf(observed, id);
        assert.equal(effect.route, 'raster'); assert.equal(effect.decidedBy, 'raster');
        assert.ok(Object.values(effect.rasterControls).every(c => c.passed));
    }
    assert.equal(effectOf(observed, 'sgr.selective-reset.bold').raster.matched, 'italic-only');
    assert.equal(effectOf(observed, 'sgr.reset').raster.matched, 'neutral');
    assert.equal(observed.snapshots.length, 57);
});

// ---- Receipt-level refusals on the retained bytes ------------------------------------------------------------------------------------
const framesPath = () => join(dir, 'sr-snapshots.jsonl');
function withFile(file, change, expectation) {
    const path = join(dir, file), original = readFileSync(path);
    try { change(path, original); assert.throws(() => checkSgrRasterEvidence(dir, identity.receipt, runId, identity.context), expectation); }
    finally { writeFileSync(path, original); }
}
const frameLines = () => readFileSync(framesPath(), 'utf8').trim().split('\n');
const png = index => `sr-pixels/frame-${String(index).padStart(3, '0')}.png`;
test('a swapped, substituted or missing subset capture is refused', () => {
    // Two retained PNGs swapped: the journal's digests no longer match the bytes.
    const a = join(dir, png(3)), b = join(dir, png(5)), original = [readFileSync(a), readFileSync(b)];
    try { writeFileSync(a, original[1]); writeFileSync(b, original[0]); assert.throws(() => checkSgrRasterEvidence(dir, identity.receipt, runId, identity.context), /changed/); }
    finally { writeFileSync(a, original[0]); writeFileSync(b, original[1]); }
    // A coherently substituted capture (bytes and journal digests rewritten): the crops change, so the rederived report cannot equal the receipt.
    withFile('sr-snapshots.jsonl', (path, bytes) => {
        const lines = bytes.toString().trim().split('\n'), frames = lines.map(JSON.parse), swap = readFileSync(join(dir, png(4)));
        writeFileSync(a, swap);
        frames[3].pixel.sha256 = hash(swap); frames[3].pixel.byteCount = swap.length;
        writeFileSync(path, frames.map(f => JSON.stringify(f)).join('\n') + '\n');
    }, /rederivation mismatch|changed|renders/);
    writeFileSync(a, original[0]);
    // A missing capture (journal line or PNG).
    withFile('sr-snapshots.jsonl', (path, bytes) => writeFileSync(path, bytes.toString().trim().split('\n').filter((_, i) => i !== 9).join('\n') + '\n'), /Incomplete|renders|changed/);
    const removed = readFileSync(join(dir, png(9)));
    try { rmSync(join(dir, png(9))); assert.throws(() => checkSgrRasterEvidence(dir, identity.receipt, runId, identity.context)); }
    finally { writeFileSync(join(dir, png(9)), removed); }
});

test('an incomplete journal (no restoration frame) or a stale or forged binding is refused', () => {
    withFile('sr-snapshots.jsonl', (path, bytes) => writeFileSync(path, bytes.toString().trim().split('\n').slice(0, -1).join('\n') + '\n'), /Incomplete|renders|changed/);
    withFile('sr-snapshots.jsonl', path => writeFileSync(path, ''));
    const stale = structuredClone(identity.receipt); stale.sgrRasterEvidence.runId = 'c'.repeat(32);
    assert.throws(() => checkSgrRasterEvidence(dir, stale, runId, identity.context), /binding mismatch/);
    const noPredecessor = structuredClone(identity.receipt); delete noPredecessor.tabStopsEvidence;
    assert.throws(() => checkSgrRasterEvidence(dir, noPredecessor, runId, identity.context), /predecessor/);
    assert.throws(() => checkSgrRasterEvidence(dir, identity.receipt, runId, { ...identity.context, sgrRasterSuiteId: 'ntilde-core-other-v1' }), /suite or predecessor/);
    withFile('sr-observations.json', (path, bytes) => writeFileSync(path, Buffer.concat([bytes, Buffer.from(' ')])), /digest mismatch/);
});

test('altered fixture bytes (SGR 0 standing in for DECRC, or SGR 99 replaced) are refused even with rehashed run logs', () => {
    const path = join(dir, 'sr-run.json'), original = readFileSync(path), binding = identity.receipt.sgrRasterEvidence, saved = binding.runSha256;
    try {
        const run = JSON.parse(original), index = run.steps.findIndex(step => step.kind === 'feed' && step.bytes.startsWith('\x1b8YY'));
        assert.ok(index > 0);
        run.steps[index].bytes = '\x1b[0mYY';
        const forged = Buffer.from(JSON.stringify(run) + '\n'); writeFileSync(path, forged);
        const evidence = structuredClone(identity.receipt); evidence.sgrRasterEvidence.runSha256 = hash(forged);
        assert.throws(() => checkSgrRasterEvidence(dir, evidence, runId, identity.context), /fixture bytes changed/);
        const sgr99 = JSON.parse(original), at = sgr99.steps.findIndex(step => step.kind === 'feed' && step.bytes.includes('\x1b[99m'));
        sgr99.steps[at].bytes = sgr99.steps[at].bytes.replace('\x1b[99m', '\x1b[98m');
        const second = Buffer.from(JSON.stringify(sgr99) + '\n'); writeFileSync(path, second); evidence.sgrRasterEvidence.runSha256 = hash(second);
        assert.throws(() => checkSgrRasterEvidence(dir, evidence, runId, identity.context), /fixture bytes changed/);
    } finally { writeFileSync(path, original); assert.equal(hash(readFileSync(path)), saved); }
});

test('a wrong presentation phase, font, geometry or render parameter in any frame is refused', () => {
    for (const mutate of [
        frame => { frame.pixel.presentationTimeMs = 530; },
        frame => { frame.pixel.renderDetails.font.primarySha256 = 'f'.repeat(64); },
        frame => { frame.pixel.renderDetails.grid.cellWidthPx += 1; },
        frame => { frame.pixel.scale = 2; },
        frame => { frame.pixel.downscaled = true; },
        frame => { frame.pixel.file = frame.pixel.file.replace('frame-', 'frame-0'); },
    ]) {
        withFile('sr-snapshots.jsonl', (path, bytes) => {
            const frames = bytes.toString().trim().split('\n').map(JSON.parse); mutate(frames[7]);
            writeFileSync(path, frames.map(f => JSON.stringify(f)).join('\n') + '\n');
        });
    }
});

// ---- Derivation-level refusals ---------------------------------------------------------------------------------------------------
test('wrong crop coordinates, geometry, font or presentation phase in the evidence are refused', () => {
    assert.throws(() => derive(o => { o.specimenRegion.col += 1; }), /wrong crop coordinates/);
    assert.throws(() => derive(o => { o.controlRegion.row += 1; }), /wrong crop coordinates/);
    assert.throws(() => derive(o => { o.geometry.rows += 1; }), /geometry differs/);
    assert.throws(() => derive(o => { o.geometry.presentationTimeMs = 530; }), /render context incomplete/);
    assert.throws(() => derive(o => { delete o.geometry.font.primarySha256; }), /render context incomplete/);
});
test('a raster verdict without its controls is refused: failed restoration, missing italic-only separation, SGR 99 change, pixels replaced by stored state', () => {
    const id = 'sgr.selective-reset.bold';
    assert.throws(() => derive(o => { effectOf(o, id).rasterControls['every-DECRC-restore-reproduces-the-neutral-crop'].passed = false; }), /raster route without qualified controls/);
    assert.throws(() => derive(o => { effectOf(o, id).rasterControls['italic-only-crop-differs-from-neutral-and-every-other-subset'].passed = false; }), /raster route without qualified controls/);
    assert.throws(() => derive(o => { delete effectOf(o, id).rasterControls['italic-only-crop-differs-from-neutral-and-every-other-subset']; }), /missing or substituted/);
    assert.throws(() => derive(o => { effectOf(o, id).rasterControls['sgr99-leaves-the-crop-unchanged'].passed = false; }), /final-crop classification/);
    assert.throws(() => derive(o => { for (const k of Object.keys(effectOf(o, 'sgr.reset').rasterControls)) if (k.startsWith('every-')) delete effectOf(o, 'sgr.reset').rasterControls[k]; }), /missing or substituted/);
    assert.throws(() => derive(o => { effectOf(o, 'sgr.reset').rasterControls = { 'stored-flags-clear': { passed: true } }; }), /missing or substituted/);
});
test('an incorrect final-crop classification, route or fallback claim is refused', () => {
    assert.throws(() => derive(o => { effectOf(o, 'sgr.reset').raster.outcome = 'unsupported'; }), /final-crop classification/);
    assert.throws(() => derive(o => { effectOf(o, 'sgr.reset').raster.matched = 'subset:bold'; }), /supported without the expected crop/);
    assert.throws(() => derive(o => { effectOf(o, 'sgr.reset').route = 'decrqss-fallback'; }), /invalid fallback conclusion/);
    assert.throws(() => derive(o => { effectOf(o, 'reset.sgr').route = 'none'; }), /without an admitted route/);
    assert.throws(() => derive(o => { effectOf(o, 'reset.sgr').route = 'telepathy'; }), /invalid experiment/);
    // A fallback verdict must be identified as query-based and carry a qualified valid reply (never silence, a refusal or malformed bytes).
    for (const bad of [{ kind: 'absent' }, { kind: 'invalid-request', raw: '\x1bP0$r\x1b\\' }, { kind: 'malformed', raw: 'x' }]) {
        assert.throws(() => derive(o => {
            const effect = effectOf(o, 'sgr.reset'), controls = effect.rasterControls;
            controls['every-DECRC-restore-reproduces-the-neutral-crop'].passed = false;
            Object.assign(effect, { route: 'decrqss-fallback', decidedBy: 'query', claim: 'query-based conclusion', fallbackReason: 'x', outcome: 'supported' });
            effect.fallback = { outcome: 'supported', reply: bad, controls: { a: true } };
        }), /fallback verdict without a qualified valid reply/);
    }
    assert.throws(() => derive(o => {
        const effect = effectOf(o, 'sgr.reset'); effect.rasterControls['every-DECRC-restore-reproduces-the-neutral-crop'].passed = false;
        Object.assign(effect, { route: 'decrqss-fallback', decidedBy: 'query', claim: 'raster result', outcome: 'supported' }); effect.fallback = { outcome: 'supported', reply: { kind: 'sgr' }, controls: {} };
    }), /invalid fallback conclusion/);
});
test('stale, duplicate, missing or reordered experiments and identities are refused', () => {
    assert.throws(() => derive(o => { o.runId = 'c'.repeat(32); }), /stale SGR raster/);
    assert.throws(() => derive(o => { o.suiteId = 'ntilde-core-erase-v1'; }), /stale SGR raster/);
    assert.throws(() => derive(o => { delete o.effects['reset.sgr']; }), /missing, duplicate/);
    assert.throws(() => derive(o => { o.effects = { 'sgr.reset': o.effects['sgr.reset'], 'sgr.selective-reset.bold': o.effects['sgr.selective-reset.bold'], 'reset.sgr': o.effects['reset.sgr'] }; }), /reordered/);
    assert.throws(() => derive(() => {}, { derive: deriveWrapCoreReport, report: previousPublicationReport, sha256: '0'.repeat(64) }), /preceding publication changed/);
    assert.equal(sgrRasterSuiteId, 'ntilde-core-sgr-raster-v1');
});
test('an unsupported or inconclusive row is never reported as a pass, and totals are recomputed from verified conclusions', () => {
    const unsupported = derive(o => {
        const effect = effectOf(o, 'sgr.reset'); Object.assign(effect, { outcome: 'unsupported' }); effect.raster = { ...effect.raster, outcome: 'unsupported', matched: 'subset:italic' };
    });
    assert.equal(unsupported.report.rows.find(r => r.id === 'sgr.reset').conclusion, 'fail');
    assert.deepEqual(unsupported.report.totals, { pass: 77, fail: 1, inconclusive: 2 });
    const invalid = derive(o => {
        const effect = effectOf(o, 'reset.sgr'); Object.assign(effect, { outcome: 'inconclusive', reason: 'probe-invalid-final-crop-matches-no-admitted-crop' });
        effect.raster = { ...effect.raster, outcome: 'inconclusive', matched: null, reason: effect.reason };
    });
    const row = invalid.report.rows.find(r => r.id === 'reset.sgr');
    assert.deepEqual([row.conclusion, row.criterionMatch], ['inconclusive', 'partial']);
    assert.match(row.blocker, /PROBE INVALID/);
    assert.deepEqual(invalid.report.totals, { pass: 77, fail: 0, inconclusive: 3 });
    const open = derive(o => {
        const effect = effectOf(o, 'sgr.selective-reset.bold'); Object.assign(effect, { outcome: 'inconclusive', route: 'none', reason: 'observation-controls-invalid', decidedBy: 'structural-control-failure' });
    });
    assert.equal(open.report.rows.find(r => r.id === 'sgr.selective-reset.bold').conclusion, 'inconclusive');
});
test('a conclusion of the preceding publication cannot be lost or silently changed, and merged closures are retained', () => {
    const lossy = { derive: (...args) => { const r = deriveWrapCoreReport(...args); r.report.rows.find(x => x.id === 'text.tab').conclusion = 'inconclusive'; return r; },
        report: previousPublicationReport, sha256: previousPublicationReportSha256 };
    assert.throws(() => derive(() => {}, lossy), /unattributed conclusion change or lost merged conclusion: text.tab/);
    const short = { ...lossy, derive: (...args) => { const r = deriveWrapCoreReport(...args); r.report.rows.pop(); return r; } };
    assert.throws(() => derive(() => {}, short), /80 exact unique/);
    const { report } = derive();
    assert.ok(['text.tab', 'unicode.tab-stops', 'erase.line.right', 'text.wrap', 'modes.auto-wrap'].every(id => report.rows.find(r => r.id === id).conclusion === 'pass'));
});
