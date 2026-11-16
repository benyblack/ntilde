import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync, gunzipSync } from 'node:zlib';
import { completeOwnedReceipt } from './receipt.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { extensionTruecolorPublication, replayExtensionTruecolor, sourcePublication } from './extensions-truecolor-publication.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const carrier = join(qa, sourcePublication), destination = join(qa, '2026-11-16-extensions-truecolor');
const read = (root, name) => readFileSync(join(root, name));
const json = (root, name) => JSON.parse(read(root, name));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const manifest = json(carrier, 'manifest.json');
const source = () => ({ ...json(carrier, 'summary.json'), summarySha256: hash(read(carrier, 'summary.json')), archiveSha256: manifest.archives['owned-run.zip'] });
function withRun(body) {
    const root = mkdtempSync(join(tmpdir(), 'nt-extensions-truecolor-'));
    try { extractOwnedArchive(carrier, manifest, root, join(carrier, 'owned-run.zip')); return body(root); }
    finally { rmSync(root, { recursive: true, force: true }); }
}
const mutate = (root, file, change) => { const path = join(root, file), value = JSON.parse(readFileSync(path)); change(value); writeFileSync(path, JSON.stringify(value)); };

test('retained complete receipt and projected publication independently rederive', () => withRun(root => {
    assert.equal(hash(read(carrier, 'owned-run.zip')), manifest.archives['owned-run.zip']);
    const original = json(root, 'cell-receipt.json');
    const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
    rmSync(join(root, 'cell-receipt.json'));
    assert.deepEqual({ ...completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence), completedAt }, original);
    writeFileSync(join(root, 'cell-receipt.json'), JSON.stringify(original));
    const projection = extensionTruecolorPublication(root, source());
    for (const name of ['summary', 'report']) assert.deepEqual(projection[name], json(destination, `${name}.json`));
    const item = projection.report.cases[0];
    assert.equal(item.caseId, 'extensions.truecolor');
    assert.equal(item.pinned.observation.outcome, 'supported');
    assert.equal(item.contractResult.outcome, 'supported');
    assert.equal(item.divergence, undefined);
    assert.deepEqual(item.observed.fixture.slice(3).map(cell => cell.fg), [{ r: 255, g: 128, b: 0 }, { r: 17, g: 97, b: 201 }]);
    assert.deepEqual(item.observed.fixture.slice(3).map(cell => cell.fgSource), ['literal', 'literal']);
    assert.match(projection.report.scope, /no fresh measurement/);
    const stock = json(root, 'raw.json').observations.find(value => value.featureId === 'extensions.truecolor');
    assert.equal(stock.outcome, 'inconclusive');
    assert.equal(stock.evidence, 'consumed');
    assert.equal(stock.note, 'SGR was consumed; RGB cell color was not measured');
}));

test('exact callback preserves conclusive mismatch and inconclusive mimic behavior', () => withRun(root => {
    const raw = read(root, 'collector-sgrgb.mjs').toString('utf8');
    const item = json(root, 'sgtc-observations.json').cases.find(c => c.caseId === 'sgr.fg.truecolor');
    const state = JSON.parse(item.pinned.response);
    const cells = ['baseline', 'controlFirst', 'controlSecond', 'targetFirst', 'targetSecond'].map(k => state[k]);
    const fixture = item.pinnedFeeds[0];
    assert.equal(replayExtensionTruecolor(raw, fixture, cells).observation.outcome, 'supported');
    const mismatch = structuredClone(cells); mismatch[3].fg = { r: 8, g: 7, b: 6 };
    assert.equal(replayExtensionTruecolor(raw, fixture, mismatch).observation.outcome, 'unsupported');
    const mimic = structuredClone(cells); mimic[2].fg = { ...mimic[3].fg };
    assert.equal(replayExtensionTruecolor(raw, fixture, mimic).observation.outcome, 'inconclusive');
    assert.throws(() => replayExtensionTruecolor(raw, fixture + '!', cells), /feed differs/);
    const bundle = gunzipSync(read(destination, 'terminfo-dev-4.0.0.bundle.mjs.gz'));
    bundle[50] ^= 1;
    assert.throws(() => replayExtensionTruecolor(raw, fixture, cells, gzipSync(bundle)), /upstream bundle differs/);
    assert.throws(() => replayExtensionTruecolor(raw.replace('The preceding color can mimic an ignored RGB request', 'changed'), fixture, cells), /pinned source bytes differ/);
}));

test('projection rejects altered source, journal, receipt, or run identity', () => withRun(root => {
    const refuses = (change, pattern) => {
        const copy = mkdtempSync(join(tmpdir(), 'nt-extensions-truecolor-mutated-'));
        try { cpSync(root, copy, { recursive: true }); change(copy); assert.throws(() => extensionTruecolorPublication(copy, source()), pattern); }
        finally { rmSync(copy, { recursive: true, force: true }); }
    };
    refuses(copy => mutate(copy, 'sgtc-observations.json', v => { v.cases[0].contractResult.outcome = 'unsupported'; }), /receipt evidence differs/);
    refuses(copy => mutate(copy, 'cell-receipt.json', v => { v.fixtureRemoved = false; }), /cleanup missing/);
    refuses(copy => mutate(copy, 'context.json', v => { v.runId = '0'.repeat(32); }), /run identity mismatch/);
    refuses(copy => writeFileSync(join(copy, 'collector-sgrgb.mjs'), read(copy, 'collector-sgrgb.mjs') + '\n// changed'), /source adapter differs/);
    refuses(copy => writeFileSync(join(copy, 'sgtc-snapshots.jsonl'), read(copy, 'sgtc-snapshots.jsonl').toString('utf8').replace('"sgtc-read"', '"invalid"')), /./);
}));
