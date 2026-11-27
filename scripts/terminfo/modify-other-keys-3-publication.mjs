import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { caseIds, contractsVersion as effectContract, suiteId, baselineScope } from './modify-other-keys-3-contracts.mjs';
import { deriveRun } from './modify-other-keys-3-owned.mjs';
import { modifyOtherKeysRecordingContext, servedDeclaration } from './modify-other-keys-contracts.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive } from './unicode-publication-inputs.mjs';

export { suiteId, caseIds };
export const contractsVersion = 1;
const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-27-modify-other-keys-3');
const hashes = {
    fixed: '698976746463e6c8dc2b783be4cd1df34d85fac0fe5e0d66b700ef651afc3aa6',
    baseline: 'c5ed87beb967bd995da845287c92782e0702d0fdb56652cea402f23edef0c7bc',
};

export function withRun(baseline, callback) {
    const dir = baseline ? join(publication, 'baseline') : publication;
    return withOwnedArchive(dir, readManifest(dir, baseline ? hashes.baseline : hashes.fixed, assert.ok), callback);
}

function replayRun(baseline) {
    return withRun(baseline, (dir, manifest) => {
        const { context, receipt, observations, frames } = deriveRun(dir);
        assert.equal(receipt.suiteId, baseline ? baselineScope.id : suiteId);
        assert.deepEqual(readJson(dir, 'mok3-receipt.json'), receipt);
        const measurement = focusedMeasurement(dir, manifest, context, receipt, frames[0].screen);
        assert.match(measurement.appRevision, /^[0-9a-f]{40}$/);
        assert.match(measurement.scriptsRevision, /^[0-9a-f]{40}$/);
        assert.ok(Date.parse(measurement.releasedAt) >= Date.parse(readJson(dir, 'ownership-proof.json').sourceSnapshot.removedAt));
        return { context, receipt, observations, measurement, declaration: frames.find(f => f.kind === 'input-control' && f.caseId === 'declaration')?.capture.hex };
    });
}

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length));
    return source.slice(from, to);
}

async function replayPinned(controlHex) {
    const bytes = gunzipSync(readFileSync(join(qa, '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz')), { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle);
    const source = bytes.toString('utf8');
    const helper = span(source, 'function probe(id, termless, term, termObservationEvidence) {', 'function readHyperlinkMetadata(');
    const note = span(source, 'var absentEventNote = ', '\n');
    const registration = span(source, '    probe("input.modify-other-keys-3",', ',\n    mouseInputProbe("input.button-event-mouse"');
    const probe = runInNewContext(helper + note + '\n' + registration, Object.create(null), { timeout: 1000 }); // NOSONAR: source spans of the digest-pinned upstream collector.
    assert.equal(probe.id, caseIds[0]);
    const declaration = servedDeclaration(controlHex), context = modifyOtherKeysRecordingContext(declaration);
    const termless = structuredClone(probe.termless(context));
    assert.equal(context.readCount, 1);
    const terminal = structuredClone(await probe.term(new Proxy({}, { get() { assert.fail('unexpected terminal access'); } })));
    return { termless, terminal, declaration };
}

export async function buildPublication() {
    const fixed = replayRun(false), baseline = replayRun(true);
    assert.notEqual(fixed.context.runId, baseline.context.runId);
    assert.equal(baseline.receipt.verdict, 'supported', 'ignored-request reproduction controls failed');
    const [caseId] = caseIds, pinned = await replayPinned(fixed.declaration);
    const stock = readJson(qa, '2026-11-09-stock-baseline.json');
    assert.equal(stock.source.pinnedBundleSha256, pinnedBundle);
    const outcome = fixed.receipt.verdict, pinnedOutcome = pinned.termless.observation.outcome;
    return {
        summary: { ownedRunId: fixed.context.runId, measuredAt: fixed.context.startedAt, appRevision: fixed.measurement.appRevision,
            scriptsRevision: fixed.measurement.scriptsRevision, context: fixed.context, receipt: fixed.receipt,
            stockCollectorRunId: stock.source.stockCollectorRunId,
            stockCollector: { rerun: false, counts: stock.counts, suiteComplete: stock.source.suiteComplete, probeHash: stock.source.probeHash,
                rawSha256: stock.source.rawSha256, sourceRevision: stock.source.collectorSourceRevision,
                note: 'Stock counts carried forward from the retained November 9 baseline.' } },
        report: { suiteId, contractsVersion, scope: 'Generated Avalonia events and fenced child stdin in one disposable macOS direct-local pane per run.',
            pinnedBundleSha256: pinnedBundle, probeHash: stock.source.probeHash, measurement: fixed.measurement,
            baseline: { measurement: baseline.measurement, observations: baseline.observations },
            cases: [{ caseId, pinned, contractResult: { contract: effectContract, outcome, observations: fixed.observations },
                stock: { outcome: stock.outcomes[caseId], runId: stock.source.stockCollectorRunId, rerun: false },
                conclusion: { outcome: pinnedOutcome, grading: 'pinned', evidence: 'generated-input-child-bytes' },
                divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: outcome } }] },
    };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]));
    for (const [name, value] of Object.entries(await buildPublication())) {
        if (process.argv[2] === '--write') writeFileSync(join(publication, name + '.json'), JSON.stringify(value, null, 2) + '\n');
        else assert.deepEqual(readJson(publication, name + '.json'), value);
    }
}
