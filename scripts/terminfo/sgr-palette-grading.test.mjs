import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrPaletteGradingReport, replaySgrPalettePinned } from './sgr-palette-grading.mjs';
import { sgrPaletteContracts, sgrPaletteSuiteId, sgrPaletteFeeds } from './sgr-palette-contracts.mjs';
import { sgrPaletteRecordingContext } from './sgr-palette-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { canonicalPalette, paletteBinding, syntheticSgrPaletteEvidence, syntheticSgrPaletteScreens, syntheticSgrPaletteScreen,
    syntheticSgrPaletteReads, colorCell, plainCell, paletteChannel, literalChannel, defaultChannel } from './fixtures/sgr-palette-evidence.mjs';

const rebuild = e => buildSgrPaletteGradingReport(e.frames, e.run, e.runId, e.adapter);
const report = (readsOf, binding) => {
    const evidence = syntheticSgrPaletteEvidence('a'.repeat(32), 'f'.repeat(64), binding, readsOf);
    return { evidence, report: rebuild(evidence) };
};
const only = (caseId, mutate) => id => { const reads = syntheticSgrPaletteReads(id); if (id === caseId) mutate(reads); return reads; };
const rehash = (evidence, index, mutate) => {
    const screen = structuredClone(evidence.frames[index].screen);
    mutate(screen);
    evidence.frames[index] = { ...evidence.frames[index], screen, sha256: snapshotHash(screen) };
};
const withPalette = edit => { const palette = canonicalPalette(); edit(palette); return paletteBinding({ palette }); };
const find = (value, caseId) => value.cases.find(item => item.caseId === caseId);
/** Frame index of the fixture read (offset 0) or control read (offset 1) of the nth case. */
const frameOf = (position, offset = 0) => 2 + position * 2 + offset;

test('rebuilds a complete graded report with six supported conclusions beside the pinned supported verdicts', () => {
    const { report: value } = report();
    assert.equal(value.suiteId, sgrPaletteSuiteId);
    assert.equal(value.contractsVersion, 1);
    assert.equal(value.snapshots.length, 3 + 6 * 2);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        sgrPaletteContracts.map(contract => [contract.caseId, 'supported', 'supported']));
    for (const item of value.cases) {
        const contract = sgrPaletteContracts.find(entry => entry.caseId === item.caseId);
        assert.equal(item.conclusion.evidence, 'owned-stored-and-resolved-cells');
        assert.deepEqual(item.conclusion.layers, { stored: 'supported', resolved: 'supported' });
        assert.equal(item.conclusion.scope, contract.appearanceScope);
        assert.match(item.conclusion.scope, /rendered appearance is not claimed/);
        assert.deepEqual(item.feeds, sgrPaletteFeeds(contract));
        assert.deepEqual(item.pinnedFeeds, [...contract.pinnedFeeds]);
        assert.equal(item.divergence, undefined);
        assert.equal(item.colorBinding.reverseVideo, false);
        assert.deepEqual(item.colorBinding.paletteOverrideIndices, []);
        assert.match(item.colorBinding.bindingSha256, /^[a-f\d]{64}$/);
        assert.equal(item.pinned.assertions[0].expected, contract.pinnedExpected);
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
    }
    assert.match(value.scope, /Rendered glyph appearance is not claimed/);
});

test('the pinned responses embed the exact pinned state keys with stored and resolved fields together', () => {
    const { report: value } = report();
    const bright = JSON.parse(find(value, 'sgr.fg.bright').pinned.response);
    assert.deepEqual(Object.keys(bright), ['baseline', 'first', 'second']);
    assert.deepEqual([bright.first.fgSource, bright.first.fgIndex, bright.first.fg], ['palette', 9, { r: 255, g: 0, b: 0 }]);
    const indexed = JSON.parse(find(value, 'sgr.bg.256').pinned.response);
    assert.deepEqual(Object.keys(indexed), ['baseline', 'directFirst', 'directSecond', 'indexedFirst', 'indexedSecond']);
    assert.equal(indexed.directFirst.bgSource, 'literal');
    assert.equal(indexed.indexedFirst.bgSource, 'palette');
    assert.deepEqual(indexed.indexedFirst.bg, indexed.directFirst.bg);
    const reset = JSON.parse(find(value, 'sgr.fg.default').pinned.response);
    assert.deepEqual(Object.keys(reset), ['baseline', 'before', 'after']);
    assert.equal(reset.baseline.fg, null);
    assert.equal(reset.after.fg, null);
    assert.equal(reset.before.fgSource, 'palette');
});

test('bright selected through the standard index is the pinned supported but the contract unsupported, both recorded', () => {
    const { report: value } = report(only('sgr.fg.bright', reads => {
        reads.fixture[1] = colorCell('X', { fg: paletteChannel(1) });
        reads.control[1] = colorCell('Q', { fg: paletteChannel(1) });
    }));
    const bright = find(value, 'sgr.fg.bright');
    assert.equal(bright.pinned.observation.outcome, 'supported');
    assert.equal(bright.conclusion.outcome, 'unsupported');
    assert.deepEqual(bright.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    assert.equal(value.cases.filter(item => item.conclusion.outcome === 'supported').length, 5);
});

test('wrong 256 indices are a measured mismatch beside the pinned inconclusive', () => {
    const { report: value } = report(only('sgr.bg.256', reads => { reads.fixture[3] = colorCell('X', { bg: paletteChannel(68) }); }));
    const indexed = find(value, 'sgr.bg.256');
    assert.equal(indexed.pinned.observation.outcome, 'inconclusive');
    assert.equal(indexed.pinned.note, 'RGB reference was not calibrated, or the backend may use a custom indexed palette');
    assert.equal(indexed.conclusion.outcome, 'unsupported');
    assert.deepEqual(indexed.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
});

test('a reset that leaves the setup is unsupported in both the pinned and the contract grading, without divergence', () => {
    const { report: value } = report(only('sgr.fg.default', reads => { reads.fixture[2] = colorCell('R', { fg: paletteChannel(1) }); }));
    const reset = find(value, 'sgr.fg.default');
    assert.equal(reset.pinned.observation.outcome, 'unsupported');
    assert.equal(reset.pinned.assertions[0].kind, 'negative');
    assert.equal(reset.conclusion.outcome, 'unsupported');
    assert.equal(reset.divergence, undefined);
});

test('a literal setup is the pinned supported but only inconclusive in the contract; an invalid control beside a pinned unsupported is more cautious', () => {
    const literal = report(only('sgr.bg.default', reads => { reads.fixture[1] = colorCell('X', { bg: literalChannel(0, 205, 0) }); }));
    const item = find(literal.report, 'sgr.bg.default');
    assert.equal(item.pinned.observation.outcome, 'supported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.divergence.kind, 'contract-stricter-than-pinned');
    const cautious = report(only('sgr.fg.default', reads => {
        reads.fixture[2] = colorCell('R', { fg: paletteChannel(1) });
        reads.control[0] = plainCell('A');
    }));
    const unsupportedPinned = find(cautious.report, 'sgr.fg.default');
    assert.equal(unsupportedPinned.pinned.observation.outcome, 'unsupported');
    assert.equal(unsupportedPinned.conclusion.outcome, 'inconclusive');
    assert.deepEqual(unsupportedPinned.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('aliased and overridden palette entries keep the pinned inconclusive and the contract inconclusive', () => {
    const { report: value } = report(null, withPalette(palette => { palette[12] = palette[9]; palette[173] = palette[67]; }));
    for (const caseId of ['sgr.fg.bright', 'sgr.bg.bright', 'sgr.fg.256', 'sgr.bg.256']) {
        const item = find(value, caseId);
        assert.equal(item.pinned.observation.outcome, 'inconclusive', caseId);
        assert.equal(item.conclusion.outcome, 'inconclusive', caseId);
        assert.equal(item.divergence, undefined, caseId);
        assert.deepEqual(item.conclusion.layers, { stored: 'supported', resolved: 'inconclusive' }, caseId);
    }
    assert.equal(find(value, 'sgr.fg.default').conclusion.outcome, 'supported');
});

test('invalid controls prevent a conclusive grade even when the stored indices look right', () => {
    for (const [name, readsOf] of [
        ['initially active baseline', id => { const reads = syntheticSgrPaletteReads(id); reads.fixture[0] = colorCell('C', { attrs: ['italic'] }); return reads; }],
        ['unaffected channel drift', id => {
            const reads = syntheticSgrPaletteReads(id);
            const other = id.includes('.fg.') ? 'bg' : 'fg';
            if (!id.endsWith('.default')) reads.fixture[1] = colorCell('X', { ...reads.fixture[1], [other]: paletteChannel(3) });
            else reads.fixture[1] = colorCell('X', { ...reads.fixture[1], [other]: paletteChannel(3) });
            return reads;
        }],
        ['missing control cells', id => { const reads = syntheticSgrPaletteReads(id); reads.control = [plainCell('?'), plainCell('?'), plainCell('?')]; return reads; }],
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
    wrongBytes.run.cases[0].feeds[1] = 'C\x1b[31mX\x1b[34mY';
    assert.throws(() => rebuild(wrongBytes), /do not match the pinned contract/);

    const substituted = structuredClone(base.evidence);
    substituted.run.cases[4].feeds[1] = 'C\x1b[31mX\x1b[0mR';
    assert.throws(() => rebuild(substituted), /do not match the pinned contract/);

    const controlChanged = structuredClone(base.evidence);
    controlChanged.run.cases[2].feeds[3] = '\x1b[38;5;1mP';
    assert.throws(() => rebuild(controlChanged), /do not match the pinned contract/);

    const missingRead = structuredClone(base.evidence);
    missingRead.frames.splice(3, 1);
    assert.throws(() => rebuild(missingRead), /Incomplete SGR-palette snapshot sequence/);

    const missingCapture = structuredClone(base.evidence);
    missingCapture.run.cases[0].reads.pop();
    assert.throws(() => rebuild(missingCapture), /Invalid sgr.fg.bright graded capture record/);

    const reordered = structuredClone(base.evidence);
    [reordered.run.cases[0], reordered.run.cases[1]] = [reordered.run.cases[1], reordered.run.cases[0]];
    assert.throws(() => rebuild(reordered), /case order differs/);
});

test('rejects tampered observations, stale RGB values, hashes and barriers', () => {
    const base = report();
    const tamperedFlags = structuredClone(base.evidence);
    tamperedFlags.frames[frameOf(0)].screen.attributeRuns[1].flags |= 0x0001;
    assert.throws(() => rebuild(tamperedFlags), /Changed SGR-palette snapshot evidence|Invalid|coheren|Contradictory/i);

    const staleRgb = structuredClone(base.evidence);
    rehash(staleRgb, frameOf(0), screen => { screen.resolvedColors[1].fg.argb = 0xff123456; });
    assert.throws(() => rebuild(staleRgb), /Invalid resolved-color|coheren|disagree/i);

    const staleIndex = structuredClone(base.evidence);
    rehash(staleIndex, frameOf(0), screen => { screen.resolvedColors[1].fg = { source: 'palette', index: 1, argb: screen.colorBinding.ansiPaletteArgb[1] }; });
    assert.throws(() => rebuild(staleIndex), /disagrees with stored attributes/);

    const tamperedHash = structuredClone(base.evidence);
    tamperedHash.frames[frameOf(0)].sha256 = '0'.repeat(64);
    assert.throws(() => rebuild(tamperedHash), /Changed SGR-palette snapshot evidence/);

    const missingBarrier = structuredClone(base.evidence);
    missingBarrier.frames[frameOf(0)].barrier = null;
    assert.throws(() => rebuild(missingBarrier), /Missing SGR-palette ordering barrier evidence/);

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
    const forged = syntheticSgrPaletteScreen([plainCell('C'), colorCell('X', { fg: paletteChannel(1) }), colorCell('Y', { fg: paletteChannel(4) })]);
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
    rehash(disagreement, frameOf(2), screen => {
        screen.attributeRuns = syntheticSgrPaletteScreen([plainCell('C'), colorCell('A', { fg: literalChannel(95, 135, 175), attrs: ['bold'] })]).attributeRuns;
    });
    assert.throws(() => rebuild(disagreement), /Contradictory stored cell projections|disagrees|coheren|Noncanonical|Invalid/i);
    const underline = structuredClone(base.evidence);
    rehash(underline, frameOf(2), screen => { screen.cellDetails[0].underlineStyle = 1; });
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
    assert.throws(() => buildSgrPaletteGradingReport(base.evidence.frames, base.evidence.run, 'b'.repeat(32), base.evidence.adapter),
        /not bound to this run/);
    const foreignAdapter = structuredClone(base.evidence);
    foreignAdapter.adapter.id = 'some-other-adapter-v9';
    assert.throws(() => rebuild(foreignAdapter), /Invalid SGR-palette adapter identity/);
    const wrongLayer = structuredClone(base.evidence);
    wrongLayer.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-rgb-grading-v1';
    assert.throws(() => rebuild(wrongLayer), /Invalid SGR-palette adapter identity/);
    const wrongRunAdapter = structuredClone(base.evidence);
    wrongRunAdapter.run.adapterId = 'ntilde-owned-sgr-attributes-grading-v1';
    assert.throws(() => rebuild(wrongRunAdapter), /Invalid SGR-palette adapter identity/);
    const wrongProbeList = structuredClone(base.evidence);
    wrongProbeList.run.probeIds = ['sgr.fg.standard'];
    assert.throws(() => rebuild(wrongProbeList), /does not cover the contracted cases/);
    const wrongProbe = structuredClone(base.evidence);
    wrongProbe.run.cases[2].pinned.probeId = 'sgr.fg.standard';
    assert.throws(() => rebuild(wrongProbe), /Missing pinned sgr.fg.256 grading result/);
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
    palette[9] = 0xff102030;
    const later = paletteBinding({ palette });
    const index = frameOf(3);
    const screen = syntheticSgrPaletteScreens('sgr.fg.bright', later)[0];
    changed.frames[index] = { ...changed.frames[index], screen: { ...changed.frames[index].screen, colorBinding: later }, sha256: '' };
    changed.frames[index].sha256 = snapshotHash(changed.frames[index].screen);
    void screen;
    assert.throws(() => rebuild(changed), /palette or theme change|Invalid resolved-color|coheren|disagree/i);
    const reverseLater = structuredClone(base.evidence);
    rehash(reverseLater, frameOf(4), s => { s.colorBinding = { ...s.colorBinding, reverseVideo: true }; });
    assert.throws(() => rebuild(reverseLater), /palette or theme change/);
});

test('incomplete restoration prevents any conclusion', () => {
    const base = report();
    const unrestored = structuredClone(base.evidence);
    rehash(unrestored, unrestored.frames.length - 1, screen => { screen.cursorRow = 2; });
    assert.throws(() => rebuild(unrestored), /SGR-palette fixture was not restored/);
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
    const faithful = { ntildeRunSgrPaletteTermless: (probeId, ctx) => {
        const contract = sgrPaletteContracts.find(item => item.caseId === probeId);
        const live = base.report.cases.find(item => item.caseId === probeId).pinned;
        ctx.feed(contract.pinnedFeeds[0]);
        for (let col = 0; col < contract.fixtureCells; col++) ctx.getCell(0, col);
        return { probeId, ...live };
    } };
    const replayed = buildSgrPaletteGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: faithful });
    assert.equal(replayed.cases[0].conclusion.outcome, 'supported');
    const unfaithful = { ntildeRunSgrPaletteTermless: () => ({ pass: true, response: 'x',
        observation: { outcome: 'supported', evidence: 'parser-state' }, assertions: [{ kind: 'positive', expected: 'x', observed: 'x' }] }) };
    assert.throws(() => buildSgrPaletteGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: unfaithful }),
        /replay does not reproduce the live grading/);
});

test('the recording context serves only the pinned fixture screen and refuses stray feeds', () => {
    const contract = sgrPaletteContracts.find(item => item.caseId === 'sgr.fg.default');
    const screens = syntheticSgrPaletteScreens('sgr.fg.default');
    const ctx = sgrPaletteRecordingContext(contract, screens);
    assert.throws(() => ctx.getCell(0, 0), /read before its fixture/);
    ctx.feed('C\x1b[31mX\x1b[39mR');
    assert.equal(ctx.getCell(0, 1).fgIndex, 1);
    assert.throws(() => ctx.feed('C\x1b[31mX\x1b[39mR'), /feed 1 differs/);
    assert.throws(() => sgrPaletteRecordingContext(contract, screens).feed('C\x1b[31mX\x1b[0mR'), /feed 0 differs/);
    assert.throws(() => ctx.getText(), /must not read text/);
    const replayed = replaySgrPalettePinned(contract, screens);
    assert.equal(replayed.pass, true);
    assert.equal(replayed.observation.outcome, 'supported');
    assert.throws(() => replaySgrPalettePinned({ ...contract, pinnedKind: 'other' }, screens), /Unknown pinned kind/);
});

test('an overlined cell beside plain cells grades inconclusive through the full report builder', () => {
    const { report: value } = report(id => {
        const reads = syntheticSgrPaletteReads(id);
        reads.fixture[0] = colorCell('C', { attrs: ['overline'] });
        return reads;
    });
    assert.ok(value.cases.every(item => item.conclusion.outcome === 'inconclusive'));
    assert.ok(value.cases.every(item => /initially active/.test(item.conclusion.note)));
});

test('the default marker stays distinct from palette index 0 and from literal black end to end', () => {
    const asPalette0 = report(only('sgr.fg.default', reads => { reads.fixture[2] = colorCell('R', { fg: paletteChannel(0) }); }));
    assert.equal(find(asPalette0.report, 'sgr.fg.default').conclusion.outcome, 'unsupported');
    assert.equal(find(asPalette0.report, 'sgr.fg.default').pinned.observation.outcome, 'unsupported');
    const asLiteral = report(only('sgr.fg.default', reads => { reads.fixture[2] = colorCell('R', { fg: literalChannel(0, 0, 0) }); }));
    assert.equal(find(asLiteral.report, 'sgr.fg.default').conclusion.outcome, 'unsupported');
    void defaultChannel;
});
