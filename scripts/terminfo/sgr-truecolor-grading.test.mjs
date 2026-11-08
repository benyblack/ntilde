import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrTruecolorGradingReport, replaySgrTruecolorPinned } from './sgr-truecolor-grading.mjs';
import { sgrTruecolorContracts, sgrTruecolorSuiteId, sgrTruecolorFeeds } from './sgr-truecolor-contracts.mjs';
import { sgrTruecolorRecordingContext } from './sgr-truecolor-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { canonicalPalette, paletteBinding, syntheticSgrTruecolorEvidence, syntheticSgrTruecolorScreens, syntheticSgrTruecolorScreen,
    syntheticSgrTruecolorReads, colorCell, plainCell, paletteChannel, literalChannel, defaultChannel } from './fixtures/sgr-truecolor-evidence.mjs';

const rebuild = e => buildSgrTruecolorGradingReport(e.frames, e.run, e.runId, e.adapter);
const report = (readsOf, binding) => {
    const evidence = syntheticSgrTruecolorEvidence('a'.repeat(32), 'f'.repeat(64), binding, readsOf);
    return { evidence, report: rebuild(evidence) };
};
const only = (caseId, mutate) => id => { const reads = syntheticSgrTruecolorReads(id); if (id === caseId) mutate(reads); return reads; };
const rehash = (evidence, index, mutate) => {
    const screen = structuredClone(evidence.frames[index].screen);
    mutate(screen);
    evidence.frames[index] = { ...evidence.frames[index], screen, sha256: snapshotHash(screen) };
};
const withPalette = edit => { const palette = canonicalPalette(); edit(palette); return paletteBinding({ palette }); };
const find = (value, caseId) => value.cases.find(item => item.caseId === caseId);
/** Frame index of the fixture read (offset 0) or control read (offset 1) of the nth case. */
const frameOf = (position, offset = 0) => 2 + position * 2 + offset;

const ids = ['sgr.fg.truecolor', 'sgr.bg.truecolor'];
const channelOf = caseId => (caseId.includes('.fg.') ? 'fg' : 'bg');
const firstOf = caseId => (channelOf(caseId) === 'fg' ? [255, 128, 0] : [0, 255, 128]);
const setCell = (reads, group, position, caseId, spec, attrs) => {
    const cell = reads[group][position];
    reads[group][position] = colorCell(cell.char, { ...cell, [channelOf(caseId)]: spec, attrs: attrs ?? cell.attrs });
};

test('rebuilds a complete graded report with two supported conclusions beside the pinned supported verdicts', () => {
    const { report: value } = report();
    assert.equal(value.suiteId, sgrTruecolorSuiteId);
    assert.equal(value.contractsVersion, 1);
    assert.equal(value.snapshots.length, 3 + 2 * 2);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        sgrTruecolorContracts.map(contract => [contract.caseId, 'supported', 'supported']));
    for (const item of value.cases) {
        const contract = sgrTruecolorContracts.find(entry => entry.caseId === item.caseId);
        assert.equal(item.conclusion.evidence, 'owned-stored-and-resolved-cells');
        assert.deepEqual(item.conclusion.layers, { stored: 'supported', resolved: 'supported' });
        assert.equal(item.conclusion.scope, contract.appearanceScope);
        assert.match(item.conclusion.scope, /rendered appearance is not claimed/);
        assert.deepEqual(item.feeds, sgrTruecolorFeeds(contract));
        assert.deepEqual(item.pinnedFeeds, [...contract.pinnedFeeds]);
        assert.equal(item.divergence, undefined);
        assert.equal(item.colorBinding.reverseVideo, false);
        assert.deepEqual(item.colorBinding.paletteOverrideIndices, []);
        assert.match(item.colorBinding.bindingSha256, /^[a-f\d]{64}$/);
        assert.equal(item.pinned.assertions[0].expected, contract.pinnedExpected);
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
    }
    assert.match(value.scope, /Rendered glyph appearance is not claimed/);
    assert.match(value.scope, /semicolon 38\/48;2;r;g;b form is qualified/);
});

test('the pinned responses embed the exact pinned state keys with literal storage and resolved RGB together', () => {
    const { report: value } = report();
    const fg = JSON.parse(find(value, 'sgr.fg.truecolor').pinned.response);
    assert.deepEqual(Object.keys(fg), ['baseline', 'controlFirst', 'controlSecond', 'targetFirst', 'targetSecond']);
    assert.deepEqual([fg.targetFirst.fgSource, fg.targetFirst.fgIndex, fg.targetFirst.fg], ['literal', -1, { r: 255, g: 128, b: 0 }]);
    assert.deepEqual([fg.targetSecond.fgSource, fg.targetSecond.fg], ['literal', { r: 17, g: 97, b: 201 }]);
    assert.deepEqual([fg.controlFirst.fgSource, fg.controlFirst.fgIndex], ['palette', 1]);
    assert.equal(fg.baseline.fg, null);
    const bg = JSON.parse(find(value, 'sgr.bg.truecolor').pinned.response);
    assert.deepEqual([bg.targetFirst.bgSource, bg.targetFirst.bg], ['literal', { r: 0, g: 255, b: 128 }]);
    assert.equal(bg.targetFirst.fg, null);
});

test('swapped RGB components are unsupported in both the pinned and the contract grading, without divergence', () => {
    for (const caseId of ids) {
        const [r, g, b] = firstOf(caseId);
        const { report: value } = report(only(caseId, reads => setCell(reads, 'fixture', 3, caseId, literalChannel(b, g, r))));
        const item = find(value, caseId);
        assert.equal(item.pinned.observation.outcome, 'unsupported', caseId);
        assert.equal(item.pinned.assertions[0].kind, 'negative');
        assert.equal(item.conclusion.outcome, 'unsupported', caseId);
        assert.equal(item.divergence, undefined, caseId);
        assert.deepEqual(item.conclusion.layers, { stored: 'unsupported', resolved: 'not-evaluated' });
    }
});

test('a palette entry with the requested RGB is the pinned supported but the contract unsupported, both recorded', () => {
    for (const caseId of ids) {
        const binding = withPalette(palette => { palette[200] = 0xff1161c9; });
        const { report: value } = report(only(caseId, reads => setCell(reads, 'fixture', 4, caseId, paletteChannel(200))), binding);
        const item = find(value, caseId);
        assert.equal(item.pinned.observation.outcome, 'supported', caseId);
        assert.equal(item.conclusion.outcome, 'unsupported', caseId);
        assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
        assert.deepEqual(item.conclusion.layers, { stored: 'unsupported', resolved: 'not-evaluated' });
    }
});

test('a target left on the default marker is a measured mismatch beside the pinned inconclusive readback', () => {
    for (const caseId of ids) {
        const { report: value } = report(only(caseId, reads => setCell(reads, 'fixture', 3, caseId, defaultChannel)));
        const item = find(value, caseId);
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.note, 'RGB target readback was absent');
        assert.equal(item.conclusion.outcome, 'unsupported');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    }
});

test('an invalid control beside a pinned unsupported is more cautious, never conclusive', () => {
    for (const caseId of ids) {
        const { report: value } = report(only(caseId, reads => {
            setCell(reads, 'fixture', 3, caseId, literalChannel(1, 2, 3));
            reads.control[1] = plainCell('Q');
        }));
        const item = find(value, caseId);
        assert.equal(item.pinned.observation.outcome, 'unsupported');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
    }
});

test('a preceding color that mimics an ignored first request stays inconclusive in both gradings', () => {
    const [r, g, b] = firstOf('sgr.fg.truecolor');
    const { report: value } = report(null, withPalette(palette => { palette[4] = 0xff000000 + r * 65536 + g * 256 + b; }));
    const fg = find(value, 'sgr.fg.truecolor');
    assert.equal(fg.pinned.observation.outcome, 'inconclusive');
    assert.equal(fg.pinned.note, 'The preceding color can mimic an ignored first RGB request');
    assert.equal(fg.conclusion.outcome, 'inconclusive');
    assert.equal(fg.divergence, undefined);
    assert.equal(find(value, 'sgr.bg.truecolor').conclusion.outcome, 'supported');
});

test('an altered palette or aliased ordinary controls cannot turn into a conclusive grade', () => {
    const { report: value } = report(null, withPalette(palette => { palette[1] = palette[4]; palette[67] = 0xff010203; }));
    for (const item of value.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive', item.caseId);
        assert.deepEqual(item.conclusion.layers, { stored: 'not-evaluated', resolved: 'not-evaluated' });
    }
});

test('invalid controls prevent a conclusive grade even when the stored bytes look right', () => {
    for (const [name, readsOf] of [
        ['initially active baseline', id => { const reads = syntheticSgrTruecolorReads(id); reads.fixture[0] = colorCell('C', { attrs: ['italic'] }); return reads; }],
        ['unaffected channel drift', id => {
            const reads = syntheticSgrTruecolorReads(id);
            const other = id.includes('.fg.') ? 'bg' : 'fg';
            reads.fixture[3] = colorCell('X', { ...reads.fixture[3], [other]: paletteChannel(3) });
            return reads;
        }],
        ['missing control cells', id => { const reads = syntheticSgrTruecolorReads(id); reads.control = reads.control.map(() => plainCell('?')); return reads; }],
        ['literal stored as palette in the control', id => { const reads = syntheticSgrTruecolorReads(id); setCell(reads, 'control', 0, id, paletteChannel(67)); return reads; }],
    ]) {
        const { report: value } = report(readsOf);
        assert.ok(value.cases.every(item => item.conclusion.outcome === 'inconclusive'), name);
        assert.ok(value.cases.every(item => item.conclusion.note), name);
    }
});

test('global reverse video voids every conclusion', () => {
    const { report: value } = report(null, { ...paletteBinding(), reverseVideo: true });
    assert.ok(value.cases.every(item => item.conclusion.outcome === 'inconclusive'));
    assert.ok(value.cases.every(item => /DECSCNM/.test(item.conclusion.note)));
});

test('rejects wrong fixture bytes, missing captures and reordered cases', () => {
    const base = report();
    const wrongBytes = structuredClone(base.evidence);
    wrongBytes.run.cases[0].feeds[1] = 'C\x1b[31mA\x1b[34mB\x1b[38;2;255;128;1mX\x1b[38;2;17;97;201mY';
    assert.throws(() => rebuild(wrongBytes), /do not match the pinned contract/);

    const substituted = structuredClone(base.evidence);
    substituted.run.cases[1].feeds[1] = 'C\x1b[41mA\x1b[44mB\x1b[48:2::0:255:128mX\x1b[48:2::17:97:201mY';
    assert.throws(() => rebuild(substituted), /do not match the pinned contract/);

    const controlChanged = structuredClone(base.evidence);
    controlChanged.run.cases[1].feeds[3] = '\x1b[48;5;1mP';
    assert.throws(() => rebuild(controlChanged), /do not match the pinned contract/);

    const missingRead = structuredClone(base.evidence);
    missingRead.frames.splice(3, 1);
    assert.throws(() => rebuild(missingRead), /Incomplete SGR-truecolor snapshot sequence/);

    const missingCapture = structuredClone(base.evidence);
    missingCapture.run.cases[0].reads.pop();
    assert.throws(() => rebuild(missingCapture), /Invalid sgr.fg.truecolor graded capture record/);

    const reordered = structuredClone(base.evidence);
    [reordered.run.cases[0], reordered.run.cases[1]] = [reordered.run.cases[1], reordered.run.cases[0]];
    assert.throws(() => rebuild(reordered), /case order differs/);
});

test('rejects tampered observations, stale RGB values, hashes and barriers', () => {
    const base = report();
    const tamperedFlags = structuredClone(base.evidence);
    tamperedFlags.frames[frameOf(0)].screen.attributeRuns[3].flags |= 0x0001;
    assert.throws(() => rebuild(tamperedFlags), /Changed SGR-truecolor snapshot evidence|Invalid|coheren|Contradictory/i);

    const staleRgb = structuredClone(base.evidence);
    rehash(staleRgb, frameOf(0), screen => { screen.resolvedColors[3].fg.argb = 0xff123456; });
    assert.throws(() => rebuild(staleRgb), /Invalid resolved-color|coheren|disagree/i);

    const staleIndex = structuredClone(base.evidence);
    rehash(staleIndex, frameOf(0), screen => { screen.resolvedColors[3].fg = { source: 'palette', index: 1, argb: screen.colorBinding.ansiPaletteArgb[1] }; });
    assert.throws(() => rebuild(staleIndex), /disagrees with stored attributes/);

    const tamperedHash = structuredClone(base.evidence);
    tamperedHash.frames[frameOf(0)].sha256 = '0'.repeat(64);
    assert.throws(() => rebuild(tamperedHash), /Changed SGR-truecolor snapshot evidence/);

    const missingBarrier = structuredClone(base.evidence);
    missingBarrier.frames[frameOf(0)].barrier = null;
    assert.throws(() => rebuild(missingBarrier), /Missing SGR-truecolor ordering barrier evidence/);

    const barrierDrift = structuredClone(base.evidence);
    barrierDrift.frames[frameOf(1)].barrier = '\x1b[?62;4;23c';
    assert.throws(() => rebuild(barrierDrift), /barrier differs from its read frame/);

    const cursorDrift = structuredClone(base.evidence);
    cursorDrift.run.cases[1].reads[0].cursor = [4, 4];
    assert.throws(() => rebuild(cursorDrift), /cursor differs from its read frame/);
});

test('a consistently rewritten screen cannot pass: the retained pinned response no longer matches', () => {
    const base = report();
    const consistent = structuredClone(base.evidence);
    const forged = syntheticSgrTruecolorScreen([plainCell('C'), colorCell('A', { fg: paletteChannel(1) }), colorCell('B', { fg: paletteChannel(4) }),
        colorCell('X', { fg: paletteChannel(208) }), colorCell('Y', { fg: paletteChannel(67) })]);
    rehash(consistent, frameOf(0), screen => {
        screen.attributeRuns = forged.attributeRuns;
        screen.resolvedColors = forged.resolvedColors;
        screen.cellDetails = forged.cellDetails;
    });
    assert.throws(() => rebuild(consistent), /does not match its observed evidence/);
});

test('attribute-run flags that disagree with the stored cell details refuse instead of grading', () => {
    const base = report();
    const disagreement = structuredClone(base.evidence);
    rehash(disagreement, frameOf(1), screen => {
        screen.attributeRuns = syntheticSgrTruecolorScreen([plainCell('C'), colorCell('A', { fg: literalChannel(95, 135, 175), attrs: ['bold'] })]).attributeRuns;
    });
    assert.throws(() => rebuild(disagreement), /Contradictory stored cell projections|disagrees|coheren|Noncanonical|Invalid/i);
    const underline = structuredClone(base.evidence);
    rehash(underline, frameOf(1), screen => { screen.cellDetails[0].underlineStyle = 1; });
    assert.throws(() => rebuild(underline), /Invalid stored cell detail run|Contradictory|coheren/i);
});

test('rejects stale captures: a read frame replaced by an earlier frame cannot grade', () => {
    const base = report();
    const stale = structuredClone(base.evidence);
    stale.frames[frameOf(1)] = { ...stale.frames[frameOf(0)], index: frameOf(1), barrier: stale.frames[frameOf(1)].barrier };
    assert.throws(() => rebuild(stale), /does not match its observed evidence|disagree|differs/);
});

test('rejects mismatched run identities and adapter bindings', () => {
    const base = report();
    assert.throws(() => buildSgrTruecolorGradingReport(base.evidence.frames, base.evidence.run, 'b'.repeat(32), base.evidence.adapter),
        /not bound to this run/);
    const foreignAdapter = structuredClone(base.evidence);
    foreignAdapter.adapter.id = 'some-other-adapter-v9';
    assert.throws(() => rebuild(foreignAdapter), /Invalid SGR-truecolor adapter identity/);
    const wrongLayer = structuredClone(base.evidence);
    wrongLayer.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-attributes-grading-v1';
    assert.throws(() => rebuild(wrongLayer), /Invalid SGR-truecolor adapter identity/);
    const wrongRunAdapter = structuredClone(base.evidence);
    wrongRunAdapter.run.adapterId = 'ntilde-owned-sgr-palette-grading-v1';
    assert.throws(() => rebuild(wrongRunAdapter), /Invalid SGR-truecolor adapter identity/);
    const wrongProbeList = structuredClone(base.evidence);
    wrongProbeList.run.probeIds = ['sgr.fg.standard'];
    assert.throws(() => rebuild(wrongProbeList), /does not cover the contracted cases/);
    const wrongProbe = structuredClone(base.evidence);
    wrongProbe.run.cases[1].pinned.probeId = 'sgr.fg.standard';
    assert.throws(() => rebuild(wrongProbe), /Missing pinned sgr.bg.truecolor grading result/);
});

test('missing stored evidence refuses instead of grading', () => {
    const base = report();
    for (const field of ['resolvedColors', 'cellDetails', 'attributeRuns']) {
        const missing = structuredClone(base.evidence);
        delete missing.frames[frameOf(0)].screen[field];
        missing.frames[frameOf(0)].sha256 = snapshotHash(missing.frames[frameOf(0)].screen);
        assert.throws(() => rebuild(missing), /./, field);
    }
});

test('a palette or theme change during observation voids the whole run', () => {
    const base = report();
    const changed = structuredClone(base.evidence);
    const palette = canonicalPalette();
    palette[1] = 0xff102030;
    const later = paletteBinding({ palette });
    const index = frameOf(1, 1);
    const screen = syntheticSgrTruecolorScreens('sgr.fg.truecolor', later)[0];
    changed.frames[index] = { ...changed.frames[index], screen: { ...changed.frames[index].screen, colorBinding: later }, sha256: '' };
    changed.frames[index].sha256 = snapshotHash(changed.frames[index].screen);
    void screen;
    assert.throws(() => rebuild(changed), /palette or theme change|Invalid resolved-color|coheren|disagree/i);
    const reverseLater = structuredClone(base.evidence);
    rehash(reverseLater, frameOf(1, 0), s => { s.colorBinding = { ...s.colorBinding, reverseVideo: true }; });
    assert.throws(() => rebuild(reverseLater), /palette or theme change/);
});

test('incomplete restoration prevents any conclusion', () => {
    const base = report();
    const unrestored = structuredClone(base.evidence);
    rehash(unrestored, unrestored.frames.length - 1, screen => { screen.cursorRow = 2; });
    assert.throws(() => rebuild(unrestored), /SGR-truecolor fixture was not restored/);
});

test('fabricated pinned results are refused', () => {
    const base = report();
    for (const [name, mutate] of [
        ['fabricated assertion detail', pinned => { pinned.assertions[0].expected = 'made up criterion'; }],
        ['dropped assertions', pinned => { delete pinned.assertions; }],
        ['wrong pass flag', pinned => { pinned.pass = false; }],
        ['wrong response', pinned => { pinned.response = '{"fabricated":true}'; }],
        ['invented inconclusive verdict', pinned => {
            pinned.pass = false;
            pinned.note = 'made up';
            pinned.observation = { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'made up' };
        }],
        ['invented unsupported verdict', pinned => {
            pinned.pass = false;
            pinned.observation = { outcome: 'unsupported', evidence: 'parser-state' };
        }],
    ]) {
        const tampered = structuredClone(base.evidence);
        mutate(tampered.run.cases[0].pinned);
        assert.throws(() => rebuild(tampered), /does not match its observed evidence/, name);
    }
});

test('the replay module must reproduce the live pinned grading exactly', () => {
    const base = report();
    const faithful = { ntildeRunSgrTruecolorTermless: (probeId, ctx) => {
        const contract = sgrTruecolorContracts.find(item => item.caseId === probeId);
        const live = base.report.cases.find(item => item.caseId === probeId).pinned;
        ctx.feed(contract.pinnedFeeds[0]);
        for (let col = 0; col < contract.fixtureCells; col++) ctx.getCell(0, col);
        return { probeId, ...live };
    } };
    const replayed = buildSgrTruecolorGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: faithful });
    assert.equal(replayed.cases[0].conclusion.outcome, 'supported');
    const unfaithful = { ntildeRunSgrTruecolorTermless: () => ({ pass: true, response: 'x',
        observation: { outcome: 'supported', evidence: 'parser-state' }, assertions: [{ kind: 'positive', expected: 'x', observed: 'x' }] }) };
    assert.throws(() => buildSgrTruecolorGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: unfaithful }),
        /replay does not reproduce the live grading/);
});

test('the recording context serves only the pinned fixture screen and refuses stray feeds', () => {
    const contract = sgrTruecolorContracts.find(item => item.caseId === 'sgr.fg.truecolor');
    const screens = syntheticSgrTruecolorScreens('sgr.fg.truecolor');
    const ctx = sgrTruecolorRecordingContext(contract, screens);
    assert.throws(() => ctx.getCell(0, 0), /read before its fixture/);
    ctx.feed(contract.pinnedFeeds[0]);
    assert.deepEqual([ctx.getCell(0, 3).fgSource, ctx.getCell(0, 3).fg], ['literal', { r: 255, g: 128, b: 0 }]);
    assert.throws(() => ctx.feed(contract.pinnedFeeds[0]), /feed 1 differs/);
    assert.throws(() => sgrTruecolorRecordingContext(contract, screens).feed('C\x1b[31mA\x1b[34mB\x1b[38:2::255:128:0mX\x1b[38;2;17;97;201mY'), /feed 0 differs/);
    assert.throws(() => ctx.getText(), /must not read text/);
    const replayed = replaySgrTruecolorPinned(contract, screens);
    assert.equal(replayed.pass, true);
    assert.equal(replayed.observation.outcome, 'supported');
    assert.throws(() => replaySgrTruecolorPinned({ ...contract, pinnedKind: 'other' }, screens), /Unknown pinned kind/);
});

test('an overlined cell beside plain cells grades inconclusive through the full report builder', () => {
    const { report: value } = report(id => {
        const reads = syntheticSgrTruecolorReads(id);
        reads.fixture[0] = colorCell('C', { attrs: ['overline'] });
        return reads;
    });
    assert.ok(value.cases.every(item => item.conclusion.outcome === 'inconclusive'));
    assert.ok(value.cases.every(item => /initially active/.test(item.conclusion.note)));
});

test('the default marker and palette index 0 stay distinct from literal black end to end', () => {
    for (const [name, spec] of [['palette index 0', paletteChannel(0)], ['literal black', literalChannel(0, 0, 0)], ['default marker', defaultChannel]]) {
        const { report: value } = report(only('sgr.fg.truecolor', reads => setCell(reads, 'fixture', 3, 'sgr.fg.truecolor', spec)));
        assert.equal(find(value, 'sgr.fg.truecolor').conclusion.outcome, 'unsupported', name);
        assert.equal(find(value, 'sgr.bg.truecolor').conclusion.outcome, 'supported', name);
    }
});

test('every report conclusion carries both layer states even when inconclusive', () => {
    const { report: value } = report(null, { ...paletteBinding(), reverseVideo: true });
    for (const item of value.cases) {
        assert.deepEqual(item.conclusion.layers, { stored: 'not-evaluated', resolved: 'not-evaluated' });
        assert.deepEqual(item.contractResult.layers, item.conclusion.layers);
    }
});

test('a detail-only index rewrite with a recalibrated palette cannot turn a wrong ordinary-control index into a supported truecolor case', () => {
    const base = report();
    const forged = structuredClone(base.evidence);
    rehash(forged, frameOf(0), screen => { screen.cellDetails.find(run => run.fg === 1).fg = 2; });
    assert.throws(() => rebuild(forged), /Contradictory|does not match its observed evidence|coheren/i);
});
