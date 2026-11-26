import assert from 'node:assert/strict';
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { caseIds, contractsVersion, suiteId } from './osc633-metadata-contracts.mjs';
import { deriveRun } from './osc633-metadata-owned.mjs';
import { assertRetainedSourceClosure, capturedOsc633MetadataSourceFiles, capturedOsc633MetadataSourcePolicy } from './owned-sources.mjs';
import { digest, focusedMeasurement, pinnedBundle, readJson, readManifest, withOwnedArchive } from './unicode-publication-inputs.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
export const publication = join(qa, '2026-11-26-osc633-metadata');
const manifestHash = '6d3c8940bbb061c32364e887d1ff3e9e368bd7854183ea78051afd132aed50d8';
const stockFile = '2026-11-09-stock-baseline.json';
const upstream = '2026-11-16-static-images/terminfo-dev-4.0.0.bundle.mjs.gz';

export function withMetadataArchive(callback) {
    return withOwnedArchive(publication, readManifest(publication, manifestHash, assert.ok), callback);
}

function span(source, start, end) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from && !source.includes(start, from + start.length), 'pinned probe source span changed');
    return source.slice(from, to);
}

export function pinnedMetadataProbes(gzip) {
    const bytes = gunzipSync(gzip, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle, 'pinned terminfo.dev bundle changed');
    const source = bytes.toString('utf8');
    const define = span(source, 'function probe(id, termless, term, termObservationEvidence) {', 'function readHyperlinkMetadata(');
    const verdict = span(source, 'function promptConsumptionResult(consumed) {', 'function colorStackError(');
    const registrations = span(source, '    {\n      ...probe("' + caseIds[0] + '",', '    probe("extensions.notifications"');
    const entries = runInNewContext(define + verdict + '[' + registrations + ']', Object.create(null), { timeout: 1000 }); // NOSONAR: digest-pinned upstream code.
    assert.deepEqual(Array.from(entries, probe => probe.id), caseIds);
    return Array.from(entries);
}

async function replayPinned(probe, termless, terminal) {
    let feedCalls = 0, cellReads = 0, writes = 0, queries = 0;
    const termlessResult = probe.termless({
        feed(bytes) {
            assert.equal(feedCalls++, 0);
            assert.deepEqual([bytes], termless.writes);
        },
        getCell(row, col) {
            assert.deepEqual([feedCalls, cellReads++, row, col], [1, 0, 0, 0]);
            return { char: termless.before.screen.lines[0]?.[0] ?? '' };
        },
    });
    const reply = Buffer.from(terminal.sample.hex, 'hex').toString('latin1');
    const position = /^\x1b\[(\d+);(\d+)R$/.exec(reply);
    assert.ok(position);
    const terminalResult = await probe.term({
        write(bytes) {
            assert.equal(queries, 0);
            assert.equal(bytes, terminal.writes[writes++]);
        },
        queryCursorPosition() {
            assert.deepEqual([writes, queries++, terminal.query], [terminal.writes.length, 0, '\x1b[6n']);
            return { row: Number(position[1]), col: Number(position[2]) };
        },
    });
    assert.deepEqual([feedCalls, cellReads, writes, queries], [1, 1, terminal.writes.length, 1]);
    return { termless: structuredClone(termlessResult), terminal: structuredClone(terminalResult),
        evidence: { termlessFeed: termless.writes[0], cell: termless.before.screen.lines[0]?.[0] ?? '',
            terminalWrites: terminal.writes, query: terminal.query, reply } };
}

export function verifySources(directory, context) {
    const retained = join(directory, 'sources'), manifest = readJson(directory, 'source-snapshot.json');
    const entries = Object.entries(capturedOsc633MetadataSourceFiles);
    assert.equal(context.sourceSnapshotPolicy, capturedOsc633MetadataSourcePolicy);
    assert.equal(manifest.policy, context.sourceSnapshotPolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(readdirSync(retained).length, entries.length);
    assert.deepEqual(readJson(retained, 'owned-source-inventory.json'), capturedOsc633MetadataSourceFiles);
    const hashes = Object.fromEntries(entries.map(([field, file]) => [field, digest(readFileSync(join(retained, file)))]));
    assert.deepEqual(hashes, manifest.hashes);
    assert.deepEqual(hashes, Object.fromEntries(entries.map(([field]) => [field, context[field]])));
    assertRetainedSourceClosure(retained, capturedOsc633MetadataSourceFiles);
}

function rederive(directory, manifest) {
    const { context, frames, report, receipt } = deriveRun(directory);
    assert.deepEqual(readJson(directory, 'osc633-metadata-effects.json'), report);
    assert.deepEqual(readJson(directory, 'osc633-metadata-receipt.json'), receipt);
    verifySources(directory, context);
    const measurement = focusedMeasurement(directory, manifest, context, receipt, frames[0].before.screen);
    for (const revision of [measurement.appRevision, measurement.scriptsRevision]) assert.match(revision, /^[a-f0-9]{40}$/);
    assert.ok(Date.parse(measurement.releasedAt) >= Date.parse(readJson(directory, 'ownership-proof.json').sourceSnapshot.removedAt));
    return { context, frames, report, receipt, measurement };
}

export async function buildPublication() {
    const pinned = pinnedMetadataProbes(readFileSync(join(qa, upstream)));
    const stock = readJson(qa, stockFile);
    assert.equal(stock.source.pinnedBundleSha256, pinnedBundle);
    const { context, frames, report, receipt, measurement } = withMetadataArchive(rederive);
    const cases = await Promise.all(report.cases.map(async ({ caseId, ...contractResult }, index) => {
        const prefix = caseId.at(-1);
        const outcomes = await replayPinned(pinned[index], frames.find(frame => frame.stage === prefix + '-termless'),
            frames.find(frame => frame.stage === prefix + '-terminal'));
        return {
            caseId, pinned: outcomes, contractResult,
            stock: { outcome: stock.outcomes[caseId], runId: stock.source.stockCollectorRunId, rerun: false },
            conclusion: { outcome: outcomes.termless.observation.outcome, grading: 'pinned', evidence: 'consumed+pane-metadata' },
            divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: outcomes.termless.observation.outcome, contractOutcome: contractResult.outcome },
        };
    }));
    return {
        summary: { ownedRunId: context.runId, measuredAt: context.startedAt, appRevision: measurement.appRevision,
            scriptsRevision: measurement.scriptsRevision, context, receipt,
            stockCollector: { rerun: false, note: 'Per-case grades carried forward from ' + stockFile } },
        report: { suiteId, contractsVersion, scope: 'One disposable direct-local macOS App pane with fenced child writes and paired App pane metadata and session status.',
            pinnedBundleSha256: pinnedBundle, upstreamCollectorRevision: stock.source.collectorSourceRevision, probeHash: stock.source.probeHash, measurement, cases },
    };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    assert.ok(process.argv.length === 3 && ['--write', '--check'].includes(process.argv[2]));
    for (const [name, value] of Object.entries(await buildPublication())) {
        const file = join(publication, name + '.json');
        if (process.argv[2] === '--write') writeFileSync(file, JSON.stringify(value, null, 2) + '\n');
        else assert.deepEqual(readJson(publication, name + '.json'), value);
    }
}
