import assert from 'node:assert/strict';
import { test } from 'node:test';
import { selectionColorsContracts, selectionColorsCaseIds, selectionColorsFixture, selectionColorsModel, selectionColorsReadPlan,
    selectionColorsFeeds, selectionColorsReads, queryBytes, belQueryBytes, selectionColorsSelection, gradeSelectionColorsCase } from './selection-colors-contracts.mjs';
import { parseColorSpec, applyColorWrites, noOverrides, effectiveColors, overrideIndices, channels16, queriedColor } from './selection-colors-model.mjs';
import { syntheticSelectionColorsEvidence, syntheticSelectionColorsGrades } from './fixtures/selection-colors-evidence.mjs';

const grades = behavior => syntheticSelectionColorsGrades(syntheticSelectionColorsEvidence('a'.repeat(32), 'f'.repeat(64), behavior)).map(item => item.outcome);

test('the contracts cover exactly the four pinned cases, in order, with 32 reads of 35 frames', () => {
    assert.deepEqual(selectionColorsCaseIds, ['extensions.osc17-highlight-bg', 'extensions.osc19-highlight-fg',
        'extensions.osc117-reset-highlight-bg', 'extensions.osc119-reset-highlight-fg']);
    assert.deepEqual(selectionColorsContracts.map(item => selectionColorsReads(item)), [8, 8, 8, 8]);
    assert.equal(selectionColorsReadPlan.length, 32);
    for (const contract of selectionColorsContracts) {
        assert.equal(contract.roles.length, selectionColorsReads(contract));
        assert.equal(contract.experiments[0].id, 'pinned');
        assert.equal(contract.experiments.filter(item => item.role === 'pinned').length, 1);
        assert.ok(selectionColorsFeeds(contract).length > selectionColorsReads(contract));
        assert.equal(contract.pinnedPath, 'termless');
        assert.ok(JSON.stringify(contract.terminalFixture).length > 40, 'the pinned terminal path is recorded, never run as a verdict');
    }
});

test('the pinned fixtures are the exact pinned bytes: one BEL query for the set cases, the whole reset fixture for the reset cases', () => {
    const bel = '\x07';
    assert.deepEqual(selectionColorsContracts.map(item => [...item.pinnedFeeds]), [
        [`\x1b]17;?${bel}`], [`\x1b]19;?${bel}`], [`\x1b]117${bel}X`], [`\x1b]119${bel}X`]]);
    assert.deepEqual([...selectionColorsContracts[0].pinnedKinds], ['capture']);
    assert.deepEqual([...selectionColorsContracts[2].pinnedKinds], ['feed']);
    // The pinned experiment's plan issues those bytes and nothing else between the scene and the read.
    for (const contract of selectionColorsContracts) {
        const plan = contract.plan.map(step => step.feed).filter(value => value !== undefined);
        const at = plan.findIndex(bytes => bytes === contract.pinnedFeeds[0]);
        assert.ok(at >= 0, 'the pinned bytes appear in the plan');
        assert.deepEqual(plan.slice(at, at + 1), [contract.pinnedFeeds[0]]);
    }
    assert.equal(queryBytes({ code: 17 }), '\x1b]17;?\x1b\\');
    assert.equal(belQueryBytes({ code: 19 }), '\x1b]19;?\x07');
    assert.equal(selectionColorsFixture.pinnedResetBytes(117), '\x1b]117\x07X');
    // The reset cases' pinned fixture is written after the cursor is placed at (0,0), so X lands where the pinned code reads it.
    for (const contract of selectionColorsContracts.slice(2)) {
        assert.deepEqual(contract.experiments[0].cursor, [0, 0]);
    }
});

test('the reset-case observation queries are the OSC 17/19 channels, never the OSC 117/119 codes (they have no query form)', () => {
    for (const contract of selectionColorsContracts) {
        for (const experiment of contract.experiments) {
            for (const spec of experiment.queries) assert.ok([4, 10, 11, 12, 17, 19].includes(spec.code), JSON.stringify(spec));
        }
    }
    const independence = selectionColorsContracts[2].experiments.find(item => item.id === 'independence');
    assert.deepEqual(independence.queries.map(spec => spec.code), [10, 11, 12, 4, 17, 19]);
    assert.deepEqual(independence.stages[2].writes, ['\x1b]117\x1b\\'], 'the first independence reset is the ST spelling of OSC 117');
    assert.deepEqual(independence.stages[3].writes, ['\x1b]119\x07'], 'the second independence reset is the BEL spelling of OSC 119');
    const mirror = selectionColorsContracts[3].experiments.find(item => item.id === 'independence');
    assert.deepEqual(mirror.stages[2].writes, ['\x1b]119\x1b\\'], 'the mirror experiment exercises the other reset order first');
    assert.deepEqual(mirror.stages[3].writes, ['\x1b]117\x07']);
});

test('the model parses color specs and OSC writes independently of the VT, with nullable selection sources', () => {
    assert.equal(parseColorSpec('rgb:aa/bb/cc'), 0xaabbcc);
    assert.equal(parseColorSpec('#20e060'), 0x20e060);
    assert.equal(parseColorSpec('rgb:1/2/3'), 0x112233, 'short channels scale to eight bits');
    for (const bad of ['', '?', 'rgb:aa/bb', 'red']) assert.equal(parseColorSpec(bad), null, bad);
    let state = noOverrides();
    state = applyColorWrites(state, '\x1b]17;rgb:aa/bb/cc\x07');
    assert.equal(state.selectionBackground, 0xaabbcc);
    assert.equal(state.selectionForeground, null);
    state = applyColorWrites(state, '\x1b]19;#20e060\x1b\\');
    assert.equal(state.selectionForeground, 0x20e060);
    state = applyColorWrites(state, '\x1b]117\x07');
    assert.equal(state.selectionBackground, null, 'OSC 117 clears the selection background');
    assert.equal(state.selectionForeground, 0x20e060, 'OSC 117 leaves the selection foreground alone');
    state = applyColorWrites(state, '\x1b]119\x07');
    assert.equal(state.selectionForeground, null);
    state = applyColorWrites(state, '\x1b]17;?\x07\x1b]19;?\x07');
    assert.equal(state.selectionBackground, null, 'queries change nothing');
    state = applyColorWrites(state, '\x1b]17;;\x07');
    assert.equal(state.selectionBackground, null, 'an invalid color is ignored, never stored');
    assert.equal(applyColorWrites(state, '\x1b]4;5;#102030\x07').palette.get(5), 0x102030);
});

test('effective colors lay the model over the measured baseline; an unset selection source stays null and queries expect absence', () => {
    const baseline = { foreground: 0xffd3d3d3, background: 0xff000000, cursor: 0xffffffff, selectionBackground: null, selectionForeground: null,
        palette: Array.from({ length: 256 }, (_, i) => 0xff000000 + i) };
    const state = applyColorWrites(noOverrides(), '\x1b]17;rgb:aa/bb/cc\x07');
    const effective = effectiveColors(baseline, state);
    assert.equal(effective.selectionBackground, 0xffaabbcc);
    assert.equal(effective.selectionForeground, null, 'the unset source stays null, never an invented RGB');
    assert.deepEqual(channels16(0xffaabbcc), [0xaaaa, 0xbbbb, 0xcccc]);
    assert.equal(queriedColor(effective, { code: 17 }), 0xffaabbcc);
    assert.equal(queriedColor(effective, { code: 19 }), null);
    const cleared = effectiveColors(baseline, applyColorWrites(state, '\x1b]117\x07'));
    assert.equal(queriedColor(cleared, { code: 17 }), null);
    assert.deepEqual(overrideIndices(applyColorWrites(noOverrides(), '\x1b]4;5;#102030\x07')), [5]);
});

test('the scene fixtures: the established selection bounds, the scene rows, the cursor homes and the pinned X line', () => {
    assert.deepEqual(selectionColorsSelection, { startRow: 2, startCol: 4, endRow: 4, endCol: 10 });
    const bytes = selectionColorsFixture.sceneBytes({ cursor: [8, 8] });
    for (const row of [1, 3, 4, 5]) assert.ok(bytes.includes(`\x1b[${row + 1};5H`), `row ${row} is written at column 4`);
    assert.ok(bytes.endsWith('\x1b[9;9H'), 'the ordinary cursor home is (8,8)');
    const pinned = selectionColorsFixture.sceneBytes({ cursor: [0, 0] });
    assert.ok(pinned.endsWith('\x1b[1;1H'), 'the pinned reset experiments home the cursor at (0,0)');
    const lines = selectionColorsFixture.expectedLines(13, [{ writes: [] }, { writes: [selectionColorsFixture.pinnedResetBytes(117)] }], 1);
    assert.equal(lines[0], 'X', 'the pinned X lands in row 0');
    assert.equal(lines[3], '    HMWqg_|');
    const withoutX = selectionColorsFixture.expectedLines(13, [{ writes: [] }], 0);
    assert.equal(withoutX[0], '');
});

test('the grader refuses every injected defect with the right verdict class', () => {
    assert.deepEqual(grades({}), ['supported', 'supported', 'supported', 'supported']);
    assert.deepEqual(grades({ neverApplied: true }), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive'],
        'an override that never applied is a control failure, inconclusive');
    assert.deepEqual(grades({ nullToLiteral: true }), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive'], 'a substituted default cannot pass');
    assert.deepEqual(grades({ falseSilence: true }), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive'], 'silence under an active override cannot pass');
    assert.deepEqual(grades({ staleSecondValue: true }), ['unsupported', 'unsupported', 'supported', 'supported'], 'a stale second value fails the set cases only');
    assert.deepEqual(grades({ ignoreReset: '117' }), ['supported', 'supported', 'unsupported', 'supported']);
    assert.deepEqual(grades({ ignoreReset: '119' }), ['supported', 'supported', 'supported', 'unsupported']);
    assert.deepEqual(grades({ wrongTarget: true }), ['supported', 'supported', 'unsupported', 'unsupported'], 'a crossed reset fails its own case');
    assert.deepEqual(grades({ staleInkAfterReset: true }), ['supported', 'supported', 'supported', 'unsupported'],
        'a foreground reset that leaves the stale ink drawn under a remaining background override fails the OSC 119 case only');
    assert.deepEqual(grades({ inkIgnoresForegroundUnderBackground: true }), ['supported', 'supported', 'inconclusive', 'inconclusive'],
        'a foreground override that is not drawn while a background override is active cannot pass the coupled experiments');
    assert.deepEqual(grades({ invisibleInkUnderBackground: true }), ['inconclusive', 'supported', 'inconclusive', 'unsupported'],
        'selected text drawn in the fill color under a background-only override is not accepted as the derived contrast ink');
    assert.deepEqual(grades({ lostOther: true }), ['supported', 'supported', 'unsupported', 'unsupported'], 'losing the unaffected override fails');
    assert.deepEqual(grades({ staleReplies: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    assert.deepEqual(grades({ renderStale: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    assert.deepEqual(grades({ wrongRender: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    assert.deepEqual(grades({ baselinePng: true }), ['supported', 'supported', 'unsupported', 'unsupported'],
        'a reset that merely repeats the baseline render is only wrong where an override remains drawn');
});
