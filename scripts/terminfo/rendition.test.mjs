import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildRenditionReport, collectRendition } from './rendition.mjs';
import { normal, rehash, runId, stateFrames, stateScreen } from './fixtures/state-frames.mjs';

const combined = [255, -1, -1, 0xff112233, 0xff445566, 3, -1, 0xff778899];
const write = '\x1b[1;2;3;4:3;5;7;8;9;38;2;17;34;51;48;2;68;85;102;58;2;119;136;153mA';
const samples = [
    ['sgr.faint', [['\x1b[2mA', stateScreen('A', [[832, -1, -1, null, null, 0, -1, null]])]]],
    ['sgr.hidden', [['\x1b[8mA', stateScreen('A', [[896, -1, -1, null, null, 0, -1, null]])]]],
    ['sgr.strikethrough', [['\x1b[9mA', stateScreen('A', [[784, -1, -1, null, null, 0, -1, null]])]]],
    ['sgr.blink', [['\x1b[5mA', stateScreen('A', [[800, -1, -1, null, null, 0, -1, null]])]]],
    ...[['faint', 22, 190], ['hidden', 28, 127], ['strikethrough', 29, 239], ['blink', 25, 223]]
        .map(([name, reset, flags]) => ['sgr.selective-reset.' + name, [[write, stateScreen('A', [combined])],
            [`\x1b[${reset}mB`, stateScreen('AB', [combined, [flags, -1, -1, 0xff112233, 0xff445566, 3, -1, 0xff778899]])]]]),
    ['sgr.remaining.reset', [[write, stateScreen('A', [combined])], ['\x1b[0mB', stateScreen('AB', [combined, normal])]]],
    ['charsets.utf8.bmp', [['éΩ', stateScreen('éΩ')]]],
    ['charsets.utf8.combining', [['e\u0301', stateScreen('e\u0301', [[8960, -1, -1, null, null, 0, -1, null]], { col: 1 })]]],
    ['charsets.utf8.wide', [['中', stateScreen('中', [[2816, -1, -1, null, null, 0, -1, null], [4864, -1, -1, null, null, 0, -1, null]], { col: 2 })]]],
    ['charsets.utf8.supplementary', [['😀', stateScreen('😀', [[11008, -1, -1, null, null, 0, -1, null], [4864, -1, -1, null, null, 0, -1, null]], { col: 2 })]]],
    ['charsets.dec-line-drawing', [['\x1b(0lqk\x1b(Bqx', stateScreen('┌─┐qx')]]],
    ['charsets.dec-special', [['\x1b(0ABfg\x1b(Bqx', stateScreen('AB°±qx')]]],
    ['charsets.g0-g1-switching', [['\x1b(B\x1b)0A\x0eqx\x0fB\x1b)B\x0eqx\x0f', stateScreen('A─│Bqx')]]],
];
const frames = () => stateFrames('rendition', 'NTILDE_RD_', stateScreen('', [], { control: '' }), samples,
    () => stateScreen(), nonce => stateScreen(nonce, [], { control: '' }));

test('16 rendition/charset experiments derive from independent complete physical-cell samples', () => {
    const report = buildRenditionReport(frames(), runId);
    assert.equal(report.snapshots.length, 40);
    assert.deepEqual(report.observations.map(value => value.featureId), samples.map(([id]) => id));
    assert.ok(report.observations.every(value => value.result === 'passed'));
});

for (const [name, change, result] of [
    ['missing faint with correct text', f => { f[3].screen = stateScreen('A'); }, 'failed'],
    ['bad control', f => { f[2].screen = stateScreen('bad'); }, 'inconclusive'],
    ['unaffected cursor visibility changes', f => { f[3].screen.cursorVisible = false; }, 'failed'],
    ['unaffected cell style changes', f => { f[3].screen.cellDetails.at(-1).flags |= 1; }, 'failed'],
]) test(name, () => { const input = frames(); change(input); rehash(input); assert.equal(buildRenditionReport(input, runId).observations[0].result, result); });

test('wrong combining width fails qualified assertions', () => {
    const input = frames();
    const before = buildRenditionReport(input, runId);
    const combining = before.observations.find(value => value.featureId === 'charsets.utf8.combining');
    input[combining.assertions[0].snapshot].screen.cursorCol = 2;
    rehash(input);
    assert.equal(buildRenditionReport(input, runId).observations.find(value => value.featureId === combining.featureId).result, 'failed');
});

test('rendition writes match independent literal experiments and restore ASCII shifts', async () => {
    const writes = [], reads = [];
    await collectRendition({ config: { runId }, owner: async kind => { reads.push(kind); },
        fence: { write: async value => { writes.push(value); return '\x1b[?62;4;22c'; } } });
    assert.ok(writes[0].endsWith('NTILDE_RD_' + runId));
    let offset = 1;
    for (const [, steps] of samples) { assert.ok(writes[offset++].includes('KEEP!'));
        assert.deepEqual(writes.slice(offset, offset + steps.length), steps.map(([sequence]) => sequence)); offset += steps.length; }
    assert.equal(reads.filter(value => value === 'rendition-start').length, 1);
    assert.ok(writes.at(-1).endsWith('\x1b[?1049l'));
});

test('initial known refusal performs no writes; other errors remain fatal', async () => {
    const writes = [];
    const refused = await collectRendition({ config: { runId }, owner: async () => { throw Object.assign(new Error('budget'), { code: 'screenDetailsUnavailable' }); },
        fence: { write: async value => writes.push(value) } });
    assert.equal(refused.status, 'unavailable'); assert.deepEqual(writes, []);
    await assert.rejects(collectRendition({ config: { runId }, owner: async () => { throw new Error('wrong pane'); } }), /wrong pane/);
});


test('published renditionReport re-derives and matches the shared cleanup receipt', () => {
    const published = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-02-rendition-cursor.json', import.meta.url)));
    const report = published.renditionReport, receipt = published.completionReceipt;
    assert.deepEqual(buildRenditionReport(report.snapshots, report.runId, report.suiteId), report);
    assert.equal(createHash('sha256').update(JSON.stringify(report, null, 2) + '\n').digest('hex'), receipt.renditionEvidence.sha256);
    assert.equal(report.runId, receipt.runId);
    assert.equal(receipt.renditionEvidence.runId, report.runId);
    assert.equal(receipt.renditionEvidence.suiteId, report.suiteId);
    assert.deepEqual(receipt.renditionEvidence.counts, { passed: 16 });
    assert.equal(receipt.fixtureRemoved, true);
    assert.equal(published.stockCollector.rawSha256, receipt.rawSha256);
    assert.deepEqual(published.stockCollector.counts, { inconclusive: 177, supported: 78 });
    for (const [key, expected] of [['sgrEvidence', 17], ['rgbEvidence', 20]]) {
        assert.deepEqual(published.companionEvidence[key], receipt[key]);
        assert.deepEqual(receipt[key].counts, { passed: expected });
    }
    const ascii = published.companionEvidence.cellEvidence;
    assert.equal(ascii.sha256, receipt.sha256);
    assert.equal(ascii.runId, receipt.runId);
    assert.deepEqual(ascii.counts, { passed: 8 });
});


test('post-restoration probes detect an ignored G0 or G1 ASCII designation', () => {
    for (const id of ['charsets.dec-line-drawing', 'charsets.dec-special', 'charsets.g0-g1-switching']) {
        const input = frames(), report = buildRenditionReport(input, runId);
        const observation = report.observations.find(value => value.featureId === id);
        const target = input[observation.assertions[0].snapshot];
        assert.ok(target.screen.lines[0].endsWith('qx'));
        target.screen.lines[0] = target.screen.lines[0].slice(0, -2) + '─│';
        rehash(input);
        assert.equal(buildRenditionReport(input, runId).observations.find(value => value.featureId === id).result, 'failed');
    }
});
