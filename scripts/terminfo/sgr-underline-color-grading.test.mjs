import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrUnderlineColorGradingReport, replaySgrUnderlineColorPinned, validateSgrUnderlineColorEvidence } from './sgr-underline-color-grading.mjs';
import { sgrUnderlineColorContracts, sgrUnderlineColorSuiteId, sgrUnderlineColorFeeds, requestedUnderlineColor } from './sgr-underline-color-contracts.mjs';
import { sgrUnderlineColorRecordingContext } from './sgr-underline-color-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticSgrUnderlineColorEvidence, syntheticSgrUnderlineColorReads, syntheticSgrUnderlineColorScreens, mutatedBinding,
    ulCell, paletteBinding, paletteChannel } from './fixtures/sgr-underline-color-evidence.mjs';

const INDEXED = 'sgr.underline-color-indexed', RGB = 'sgr.underline-color-rgb', RESET = 'sgr.underline-color-reset';
const rebuild = (e, options) => buildSgrUnderlineColorGradingReport(e.frames, e.run, e.runId, e.adapter, options);
const reads = (caseId, edit) => id => { const value = syntheticSgrUnderlineColorReads(id); if (id === caseId && edit) edit(value); return value; };
const report = (readsOf, binding) => {
    const evidence = syntheticSgrUnderlineColorEvidence('a'.repeat(32), 'f'.repeat(64), binding, readsOf);
    return { evidence, report: rebuild(evidence) };
};
const rehash = (evidence, index, mutate) => {
    const screen = structuredClone(evidence.frames[index].screen);
    mutate(screen);
    evidence.frames[index] = { ...evidence.frames[index], screen, sha256: snapshotHash(screen) };
};
/** Frames: 0 start, 1 bind, indexed reads 2-5, rgb reads 6-7, reset reads 8-9, 10 finish. */
const frameOf = { [INDEXED]: 2, [RGB]: 6, [RESET]: 8 };

test('rebuilds a complete graded report with three supported conclusions and distinct layers', () => {
    const { report: value } = report();
    assert.equal(value.suiteId, sgrUnderlineColorSuiteId);
    assert.equal(value.snapshots.length, 3 + 4 + 2 + 2);
    assert.match(value.scope, /rendered underline appearance is not claimed/);
    assert.match(value.scope, /aggregate sgr\.underline\.color case is not qualified/);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        [INDEXED, RGB, RESET].map(id => [id, 'supported', 'supported']));
    assert.deepEqual(value.cases.map(item => item.conclusion.layers), [
        { stored: 'supported', resolved: 'supported' }, { stored: 'supported', resolved: 'supported' },
        { stored: 'supported', resolved: 'not-evaluated' }]);
    for (const item of value.cases) {
        const contract = sgrUnderlineColorContracts.find(entry => entry.caseId === item.caseId);
        assert.deepEqual(item.feeds, sgrUnderlineColorFeeds(contract));
        assert.deepEqual(item.pinnedFeeds, [...contract.pinnedFeeds]);
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.evidence, 'owned-stored-and-resolved-cells');
        assert.match(item.conclusion.scope, /not claimed/);
        assert.equal(item.pinned.assertions[0].expected, contract.pinnedExpected);
    }
    const indexed = value.cases[0].observed;
    assert.deepEqual(indexed.fixture.map(cell => [cell.char, cell.underlineSource, cell.underlineColorIndex]),
        [['A', 'default', -1], ['X', 'palette', 5], ['B', 'default', -1], ['Y', 'palette', 4]]);
    assert.deepEqual(indexed.mutated.slice(0, 4).map(cell => cell.underlineColorIndex), [-1, 5, -1, 4], 'stored indices survive the mutation');
    assert.notDeepEqual(indexed.fixture[1].underlineColor, indexed.mutated[1].underlineColor, 'the resolved color follows the mutated entry');
    assert.deepEqual(indexed.restored[1].underlineColor, indexed.fixture[1].underlineColor);
    assert.deepEqual(value.cases[1].observed.fixture[2].underlineColor, requestedUnderlineColor);
    assert.equal(value.cases[1].observed.fixture[2].underlineColorIndex, -1);
    assert.equal(value.cases[2].observed.fixture[2].underlineColor, null);
});

test('a measured mismatch is unsupported beside the pinned inconclusive or negative, and is recorded', () => {
    const { report: value } = report(reads(RGB, edit => { edit.fixture[2] = ulCell('X', { fg: { kind: 'literal', rgb: { r: 0, g: 255, b: 0 } }, rgb: { r: 255, g: 0, b: 129 } }); }));
    const rgb = value.cases[1];
    assert.equal(rgb.conclusion.outcome, 'unsupported');
    assert.equal(rgb.pinned.observation.outcome, 'inconclusive');
    assert.deepEqual(rgb.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    const left = report(reads(RESET, edit => { edit.fixture[2] = ulCell('Y', { rgb: requestedUnderlineColor }); })).report.cases[2];
    assert.equal(left.pinned.observation.outcome, 'unsupported', 'the pinned reset probe itself reports the left-over color');
    assert.equal(left.conclusion.outcome, 'unsupported');
    assert.equal(left.divergence, undefined);
});

test('invalid controls make the contract more cautious than a pinned supported, recorded as a divergence', () => {
    const { report: value } = report(reads(INDEXED, edit => { edit.control[0] = ulCell('P', { index: 3 }); }));
    const indexed = value.cases[0];
    assert.equal(indexed.pinned.observation.outcome, 'supported');
    assert.equal(indexed.conclusion.outcome, 'inconclusive');
    assert.deepEqual(indexed.divergence, { pinnedOutcome: 'supported', contractOutcome: 'inconclusive', kind: 'contract-stricter-than-pinned' });
});

test('a persistent unrelated palette change cannot pass, and a changed theme voids the run', () => {
    const noRestore = syntheticSgrUnderlineColorEvidence('a'.repeat(32), 'f'.repeat(64), undefined, reads(INDEXED, edit => {
        const binding = paletteBinding(); binding.ansiPaletteArgb[200] = 0xff010203; edit.restoredBinding = binding;
    }));
    // The restored read no longer equals the start palette although the closing frame does: the journal itself is refused.
    assert.throws(() => rebuild(noRestore), /unexpected palette change/);
    const theme = syntheticSgrUnderlineColorEvidence();
    rehash(theme, frameOf[RGB], screen => { screen.colorBinding = { ...screen.colorBinding, themeName: 'Changed' }; });
    assert.throws(() => rebuild(theme), /saw the theme change/);
    const unrestored = syntheticSgrUnderlineColorEvidence();
    rehash(unrestored, unrestored.frames.length - 1, screen => { screen.colorBinding = mutatedBinding(screen.colorBinding); });
    assert.throws(() => rebuild(unrestored), /not restored|palette was not restored/);
});

test('an unrelated palette change in a non-mutating read is refused even when it is restored by the end', () => {
    for (const [name, index] of [['rgb fixture read', frameOf[RGB]], ['rgb control read', frameOf[RGB] + 1], ['reset fixture read', frameOf[RESET]],
        ['reset control read', frameOf[RESET] + 1], ['indexed fixture read', frameOf[INDEXED]], ['indexed control read', frameOf[INDEXED] + 1],
        ['indexed restored read', frameOf[INDEXED] + 3]]) {
        const evidence = syntheticSgrUnderlineColorEvidence();
        rehash(evidence, index, screen => {
            screen.colorBinding = { ...screen.colorBinding, ansiPaletteArgb: screen.colorBinding.ansiPaletteArgb.map((value, entry) => (entry === 200 ? 0xff010203 : value)),
                paletteOverrideIndices: [200] };
        });
        assert.throws(() => rebuild(evidence), /unexpected palette change/, name);
    }
    // The contracted mutation read may differ, but only the mutating case's third read may be the mutated one.
    const misplaced = syntheticSgrUnderlineColorEvidence();
    rehash(misplaced, frameOf[RGB], screen => { screen.colorBinding = mutatedBinding(screen.colorBinding); });
    assert.throws(() => rebuild(misplaced), /unexpected palette change/);
});

test('the pinned replay reproduces the pinned verdict shapes from the observed cells', () => {
    for (const contract of sgrUnderlineColorContracts) {
        assert.equal(replaySgrUnderlineColorPinned(contract, syntheticSgrUnderlineColorScreens(contract.caseId)).observation.outcome, 'supported');
    }
    const wrong = syntheticSgrUnderlineColorScreens(INDEXED, undefined, reads(INDEXED, edit => { edit.fixture[1] = ulCell('X', { fg: paletteChannel(4), index: 6 }); }));
    assert.equal(replaySgrUnderlineColorPinned(sgrUnderlineColorContracts[0], wrong).observation.outcome, 'inconclusive');
    assert.throws(() => replaySgrUnderlineColorPinned({ caseId: 'sgr.underline.color', pinnedRead: 0, fixtureCells: 3 }, wrong), /Unknown pinned case/);
});

test('every report refuses tampering with a frame, even a re-hashed one that changes a stored underline color', () => {
    const evidence = syntheticSgrUnderlineColorEvidence();
    const tampered = structuredClone(evidence);
    tampered.frames[frameOf[RGB]] = { ...tampered.frames[frameOf[RGB]], screen: { ...tampered.frames[frameOf[RGB]].screen, cursorCol: 4 } };
    assert.throws(() => rebuild(tampered), /Changed SGR-underline-color snapshot evidence/);
    for (const [caseId, edit] of [
        [INDEXED, screen => { screen.cellDetails.find(run => run.underlineColorIndex === 5).underlineColorIndex = 6; }],
        [RGB, screen => { screen.cellDetails.find(run => run.underlineArgb !== null).underlineArgb += 1; }],
        [RESET, screen => { screen.cellDetails.find(run => run.underlineArgb !== null).underlineArgb = null; screen.cellDetails.find(run => run.underlineStyle === 1 && run.underlineArgb === null).underlineColorIndex = -1; }],
    ]) {
        const forged = structuredClone(evidence);
        rehash(forged, frameOf[caseId], edit);
        assert.throws(() => rebuild(forged), /Pinned sgr\.underline-color-(indexed|rgb|reset) grading does not match|Invalid|Contradictory|Noncanonical/, caseId);
    }
});

test('a forged pinned verdict, response, note or assertion is refused', () => {
    for (const forge of [
        pinned => { pinned.observation = { ...pinned.observation, outcome: 'unsupported' }; },
        pinned => { pinned.pass = false; },
        pinned => { pinned.response = pinned.response.replace('"X"', '"Y"'); },
        pinned => { pinned.note = 'forged'; },
        pinned => { pinned.assertions = [{ kind: 'negative', expected: 'x', observed: 'y' }]; },
    ]) {
        const evidence = syntheticSgrUnderlineColorEvidence();
        forge(evidence.run.cases[1].pinned);
        assert.throws(() => rebuild(evidence), /Pinned sgr\.underline-color-rgb grading does not match/);
    }
});

test('the recorded plan, barriers, cursors, case coverage and adapter identity are enforced', () => {
    const mutate = (edit, pattern) => { const evidence = syntheticSgrUnderlineColorEvidence(); edit(evidence); assert.throws(() => rebuild(evidence), pattern); };
    mutate(e => { e.run.cases[0].feeds[1] = 'D'; }, /do not match the pinned contract/);
    mutate(e => { e.run.cases[2].feeds[1] = e.run.cases[2].feeds[1].replace('\x1b[59mY', '\x1b[0mY'); }, /do not match the pinned contract/); // SGR 59 swapped for SGR 0
    mutate(e => { e.run.cases[0].feeds[1] = e.run.cases[0].feeds[1].replace('58;5;5m', '58;5;6m'); }, /do not match the pinned contract/);
    mutate(e => { e.run.cases[0].feeds.splice(4, 1); }, /do not match the pinned contract/);                            // the OSC 4 mutation removed
    mutate(e => { e.run.cases[1].barriers.pop(); }, /Invalid sgr\.underline-color-rgb graded capture record/);
    mutate(e => { e.run.cases[0].reads[0].cursor = [3, 3]; }, /cursor differs from its read frame/);
    mutate(e => { e.run.cases.pop(); }, /does not cover the contracted cases/);
    mutate(e => { e.run.probeIds.pop(); }, /does not cover the contracted cases/);
    mutate(e => { e.adapter.id = 'other'; }, /Invalid SGR-underline-color adapter identity/);
    mutate(e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-truecolor-grading-v1'; }, /Invalid SGR-underline-color adapter identity/);
    mutate(e => rehash(e, 1, screen => { screen.lines[0] = 'NTILDE_UC_' + 'c'.repeat(32); }), /not bound to this run/);
    mutate(e => e.frames.pop(), /Incomplete SGR-underline-color snapshot sequence/);
    assert.throws(() => validateSgrUnderlineColorEvidence([], {}, 'a'.repeat(32), {}), /Incomplete/);
});

test('the pinned feed is the only thing the recording context accepts, and reads follow it', () => {
    const contract = sgrUnderlineColorContracts[2];
    const context = sgrUnderlineColorRecordingContext(contract, syntheticSgrUnderlineColorScreens(contract.caseId));
    assert.throws(() => context.getCell(0, 0), /read before its fixture/);
    assert.throws(() => context.feed('\x1b[4m\x1b[59mC\x1b[58;2;255;0;128mX\x1b[0mY'), /differs from the contract/);
    context.feed(contract.pinnedFeeds[0]);
    assert.equal(context.getCell(0, 1).underlineColor.r, 255);
    assert.throws(() => context.getText(), /must not read text/);
});
