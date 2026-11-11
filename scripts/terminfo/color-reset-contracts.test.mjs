import assert from 'node:assert/strict';
import { test } from 'node:test';
import { colorResetContracts, colorResetCaseIds, colorResetFixture, colorResetModel, colorResetReadPlan, gradeColorResetCase, colorResetObservation,
    colorResetFeeds, colorResetReads, queryBytes } from './color-reset-contracts.mjs';
import { parseColorSpec, oscStrings, applyColorWrites, noOverrides, effectiveColors, overrideIndices, channels16 } from './color-reset-model.mjs';
import { syntheticColorResetEvidence, syntheticColorResetGrades, syntheticBaseline } from './fixtures/color-reset-evidence.mjs';
import { canonicalPalette } from './fixtures/sgr-palette-evidence.mjs';

const outcomes = behavior => syntheticColorResetGrades(syntheticColorResetEvidence('a'.repeat(32), 'f'.repeat(64), behavior)).map(item => item.outcome);

test('the contracts cover exactly the four pinned cases, in order, with 44 reads of 47 frames', () => {
    assert.deepEqual(colorResetCaseIds, ['extensions.osc104-reset-palette', 'extensions.osc110-reset-fg', 'extensions.osc111-reset-bg', 'extensions.osc112-reset-cursor']);
    assert.deepEqual(colorResetContracts.map(item => colorResetReads(item)), [15, 10, 10, 9]);
    assert.equal(colorResetReadPlan.length, 44);
    for (const contract of colorResetContracts) {
        assert.equal(contract.roles.length, colorResetReads(contract));
        assert.equal(contract.experiments[0].id, 'pinned');
        assert.equal(contract.experiments.filter(item => item.role === 'pinned').length, 1);
        assert.ok(colorResetFeeds(contract).length > colorResetReads(contract));
        assert.deepEqual(contract.terminalFixture.writes, [], 'the pinned terminal path writes nothing');
    }
});

test('the pinned feeds are the exact BEL spellings of the pinned probe, in its order and kinds', () => {
    const bel = '\x07', color = 'rgb:aa/bb/cc';
    const expected = [['4;0', '104;0'], ['10', '110'], ['11', '111'], ['12', '112']].map(([set, reset]) => {
        const code = set.split(';')[0], where = set.includes(';') ? ';0' : '';
        return [`\x1b]${set};?${bel}`.replace(';;', ';'), `\x1b]${code}${where};${color}${bel}`, `\x1b]${set};?${bel}`.replace(';;', ';'), `\x1b]${reset}${bel}`, `\x1b]${set};?${bel}`.replace(';;', ';')];
    });
    expected[0] = ['\x1b]4;0;?\x07', '\x1b]4;0;rgb:aa/bb/cc\x07', '\x1b]4;0;?\x07', '\x1b]104;0\x07', '\x1b]4;0;?\x07'];
    expected[1] = ['\x1b]10;?\x07', '\x1b]10;rgb:aa/bb/cc\x07', '\x1b]10;?\x07', '\x1b]110\x07', '\x1b]10;?\x07'];
    expected[2] = ['\x1b]11;?\x07', '\x1b]11;rgb:aa/bb/cc\x07', '\x1b]11;?\x07', '\x1b]111\x07', '\x1b]11;?\x07'];
    expected[3] = ['\x1b]12;?\x07', '\x1b]12;rgb:aa/bb/cc\x07', '\x1b]12;?\x07', '\x1b]112\x07', '\x1b]12;?\x07'];
    assert.deepEqual(colorResetContracts.map(item => [...item.pinnedFeeds]), expected);
    for (const contract of colorResetContracts) assert.deepEqual([...contract.pinnedKinds], ['capture', 'feed', 'capture', 'feed', 'capture']);
    // The pinned experiment's plan issues those bytes, in that order, and nothing else between them.
    for (const contract of colorResetContracts) {
        const plan = contract.plan.map(step => step.feed).filter(value => value !== undefined);
        const at = plan.findIndex((bytes, index) => bytes === contract.pinnedFeeds[0] && plan[index + 1] === contract.pinnedFeeds[1]);
        assert.deepEqual(plan.slice(at, at + 5), [...contract.pinnedFeeds]);
    }
    assert.equal(queryBytes({ code: 4, index: 0 }, '\x07'), '\x1b]4;0;?\x07');
    assert.equal(queryBytes({ code: 10 }, '\x1b\\'), '\x1b]10;?\x1b\\');
});

test('the model parses color specs, OSC strings and writes independently of the VT', () => {
    assert.equal(parseColorSpec('rgb:aa/bb/cc'), 0xaabbcc);
    assert.equal(parseColorSpec('#175bad'), 0x175bad);
    assert.equal(parseColorSpec('rgb:a/b/c'), 0xaabbcc, 'one digit scales to eight bits');
    assert.equal(parseColorSpec('rgb:aaaa/bbbb/cccc'), 0xaabbcc);
    assert.equal(parseColorSpec('#abc'), 0xaabbcc);
    for (const bad of ['', '?', 'rgb:aa/bb', 'rgb:gg/00/00', '#12', 'red', 'rgb:aaaaa/00/00']) assert.equal(parseColorSpec(bad), null, bad);
    assert.deepEqual(oscStrings('x\x1b]104;1;2\x07y\x1b]110\x1b\\z'), [{ code: '104', params: ['1', '2'] }, { code: '110', params: [] }]);
    assert.deepEqual(oscStrings('\x1b]4;0;?'), [], 'an unterminated string is not a string');
    let state = noOverrides();
    state = applyColorWrites(state, '\x1b]4;1;#112233;9;#445566\x07\x1b]10;#010203\x1b\\\x1b]11;#040506\x07\x1b]12;rgb:ff/00/ff\x07');
    assert.deepEqual([...state.palette], [[1, 0x112233], [9, 0x445566]]);
    assert.deepEqual([state.foreground, state.background, state.cursor], [0x010203, 0x040506, 0xff00ff]);
    state = applyColorWrites(state, '\x1b]4;1;?\x07\x1b]10;?\x07');
    assert.deepEqual([...state.palette.keys(), state.foreground], [1, 9, 0x010203], 'queries change nothing');
    state = applyColorWrites(state, '\x1b]104;1\x07\x1b]110\x07');
    assert.deepEqual([[...state.palette.keys()], state.foreground, state.background], [[9], null, 0x040506]);
    assert.equal(applyColorWrites(state, '\x1b]104\x07').palette.size, 0, 'no parameter resets every slot');
    assert.equal(applyColorWrites(state, '\x1b]104;\x07').palette.size, 1, 'an empty parameter names no slot');
    assert.equal(applyColorWrites(state, '\x1b]112\x07').cursor, null);
    assert.equal(applyColorWrites(state, '\x1b]10;;\x07').foreground, state.foreground, 'an invalid color is ignored');
});

test('effective colors lay the model over the measured baseline, never over conventional values', () => {
    const baseline = syntheticBaseline();
    let state = applyColorWrites(noOverrides(), '\x1b]4;5;#102030\x07\x1b]11;#405060\x07');
    const colors = effectiveColors(baseline, state);
    assert.equal(colors.palette[5], 0xff102030);
    assert.equal(colors.palette[6], baseline.palette[6]);
    assert.equal(colors.background, 0xff405060);
    assert.equal(colors.foreground, baseline.foreground);
    assert.deepEqual(overrideIndices(state), [5]);
    assert.deepEqual(channels16(0xff102030), [0x1010, 0x2020, 0x3030]);
});

test('each experiment writes only what its stages say: the model reproduces the intended overrides stage by stage', () => {
    const states = id => colorResetModel.overridesByStageOf(colorResetContracts[0].experiments.find(item => item.id === id));
    assert.deepEqual(states('pinned').map(state => [...state.palette]), [[], [[0, 0xaabbcc]], []]);
    const individual = states('individual-slots').map(overrideIndices);
    assert.deepEqual(individual, [[], [1, 9, 16, 200, 255], [9, 16, 200, 255], [16, 255], []]);
    assert.deepEqual(states('whole-palette').map(overrideIndices).map(list => list.length), [0, 256, 0]);
    const isolated = states('isolation');
    assert.deepEqual(isolated.map(overrideIndices), [[], [5, 6], [6], []]);
    assert.deepEqual(isolated.map(state => [state.foreground !== null, state.background !== null, state.cursor !== null]), [[false, false, false], [true, true, true], [true, true, true], [true, true, true]],
        'a palette reset of any kind leaves the three dynamic colors overridden');
    const fg = colorResetContracts[1].experiments.find(item => item.id === 'isolation');
    assert.deepEqual(colorResetModel.overridesByStageOf(fg).map(overrideIndices), [[], [5], [5]], 'a dynamic-color reset leaves the unrelated palette slot overridden');
    assert.deepEqual(colorResetModel.overridesByStageOf(fg).map(state => [state.foreground !== null, state.background !== null, state.cursor !== null]),
        [[false, false, false], [true, true, true], [false, true, true]]);
    const cursor = colorResetContracts[3].experiments.find(item => item.id === 'isolation');
    assert.deepEqual(colorResetModel.overridesByStageOf(cursor).map(state => [state.foreground !== null, state.background !== null, state.cursor !== null]),
        [[false, false, false], [true, true, true], [true, true, false]]);
});

test('every override differs from the conventional palette, so a reset to conventional values cannot pass for the measured baseline', () => {
    const conventional = canonicalPalette();
    for (let slot = 0; slot < 256; slot++) assert.notEqual(opaque(colorResetFixture.slotRgb(slot)), conventional[slot], `slot ${slot}`);
    assert.equal(colorResetFixture.allSlots.length, 256);
    assert.equal(new Set(colorResetFixture.allSlots.map(colorResetFixture.slotRgb)).size, 256, 'every slot gets its own override');
});
const opaque = rgb => (0xff000000 | rgb) >>> 0;

test('the scene separates default markers, palette-indexed cells and literal RGB cells in both channels', () => {
    const cells = colorResetFixture.sceneCells({ fgIndex: 33, bgIndex: 34 });
    assert.deepEqual(cells.map(cell => [cell.id, cell.fg.source, cell.bg.source]), [['default', 'default', 'default'], ['palette-fg', 'palette', 'default'],
        ['literal-fg', 'literal', 'default'], ['palette-bg', 'default', 'palette'], ['literal-bg', 'default', 'literal']]);
    const bytes = colorResetFixture.sceneBytes({ fgIndex: 33, bgIndex: 34, cursor: null });
    for (const part of ['\x1b[38;5;33m', '\x1b[38;2;240;240;240m', '\x1b[48;5;34m', '\x1b[48;2;16;32;48m', '\x1b]104\x1b\\\x1b]110\x1b\\\x1b]111\x1b\\\x1b]112\x1b\\']) assert.ok(bytes.includes(part), JSON.stringify(part));
    assert.ok(bytes.startsWith('\x1b]104'), 'every scene clears the runtime colors first');
});

test('the SGR 39/49 experiment writes SGR defaults and the reset around explicit palette pens, so neither can stand in for the other', () => {
    for (const [index, set, reset, osc] of [[1, '38;5;33', '39', '110'], [2, '48;5;34', '49', '111']]) {
        const spec = colorResetContracts[index].experiments.find(item => item.id === 'sgr-default');
        const sgr = spec.stages.find(step => step.id === 'sgr'), final = spec.stages.at(-1);
        assert.ok(sgr.writes[0].includes(`\x1b[${set}mA\x1b[${reset}mB`));
        assert.ok(final.writes[0].includes(`\x1b[${set}m\x1b]${osc}\x1b\\C`), 'the reset is written between the pen and its character');
        assert.deepEqual(sgr.writes.concat(final.writes).filter(bytes => bytes.includes('\x1b]10;') || bytes.includes('\x1b]11;')).length, 0, 'no runtime color is set by these stages');
    }
});

test('grading: a correct App is supported in every case; each injected defect is graded as the criterion demands', () => {
    assert.deepEqual(outcomes({}), ['supported', 'supported', 'supported', 'supported']);
    // Setup that never took effect cannot judge a reset: inconclusive, never unsupported.
    assert.deepEqual(outcomes({ neverApplied: true }), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive']);
    assert.deepEqual(outcomes({ noReplies: true }), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive']);
    // A reset ignored by one target fails only that case.
    assert.deepEqual(outcomes({ ignoreReset: 'osc110' }), ['supported', 'unsupported', 'supported', 'supported']);
    assert.deepEqual(outcomes({ ignoreReset: 'osc104' }), ['unsupported', 'supported', 'supported', 'supported']);
    assert.deepEqual(outcomes({ ignoreReset: 'osc111' }), ['supported', 'supported', 'unsupported', 'supported']);
    assert.deepEqual(outcomes({ ignoreReset: 'osc112' }), ['supported', 'supported', 'supported', 'unsupported']);
    assert.deepEqual(outcomes({ individualResetClearsAll: true }), ['unsupported', 'supported', 'supported', 'supported']);
    assert.deepEqual(outcomes({ wholeResetPartial: true }), ['unsupported', 'supported', 'supported', 'supported']);
    assert.deepEqual(outcomes({ wrongTarget: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    assert.deepEqual(outcomes({ sgr39ClearsOverride: true }), ['supported', 'unsupported', 'unsupported', 'supported']);
    assert.deepEqual(outcomes({ wrongTheme: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    assert.deepEqual(outcomes({ staleReplies: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    assert.deepEqual(outcomes({ renderStale: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported'], 'replies and metadata restored but the render was not');
    // A reset the renderer never applied repeats the previous render, even where other overrides remain (the isolation experiments).
    assert.deepEqual(outcomes({ stalePng: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    // Where other overrides remain drawn, a reset render equal to the baseline render means the renderer dropped them too.
    assert.deepEqual(outcomes({ baselinePng: true }), ['unsupported', 'unsupported', 'unsupported', 'unsupported']);
    // A reply that never arrived at the reset readback cannot establish unsupported: the pinned probe is inconclusive too.
    assert.deepEqual(outcomes({ noResetReplies: true }), ['inconclusive', 'inconclusive', 'inconclusive', 'inconclusive']);
});

test('grading names the failed check of the first failing experiment', () => {
    const [palette] = syntheticColorResetGrades(syntheticColorResetEvidence('a'.repeat(32), 'f'.repeat(64), { individualResetClearsAll: true }));
    assert.equal(palette.outcome, 'unsupported');
    assert.match(palette.note, /individual-slots\/reset-one: the exact query replies/);
    const individual = palette.experiments.find(item => item.id === 'individual-slots');
    assert.equal(individual.outcome, 'unsupported');
    assert.ok(individual.controls.every(item => item.passed), 'the controls held, so the mismatch is a verdict');
    assert.ok(palette.experiments.filter(item => item.id !== 'individual-slots').every(item => item.outcome === 'supported'));
});

test('grading: a dirty baseline, a missing control or an override equal to the baseline is inconclusive, not unsupported', () => {
    const evidence = syntheticColorResetEvidence();
    const dirty = structuredClone(evidence.frames);
    dirty[2].pixel.renderDetails.theme.foregroundOverride = true;
    assert.equal(grade(evidence, dirty)[0].outcome, 'inconclusive');
    const same = structuredClone(evidence.frames);
    // The override stage shows the baseline color everywhere: the intended override is indistinguishable from no override.
    for (const index of [3]) { same[index] = structuredClone(same[index - 1]); same[index].queries = structuredClone(evidence.frames[index].queries); }
    assert.notEqual(grade(evidence, same)[0].outcome, 'supported');
});
function grade(evidence, frames) {
    let at = 2;
    return colorResetContracts.map(contract => {
        const reads = evidence.run.cases.find(item => item.caseId === contract.caseId).reads.map(() => frames[at++]);
        return gradeColorResetCase(contract, colorResetObservation(contract, reads.map(item => ({ screen: item.screen, queries: item.queries, details: item.pixel.renderDetails, pixelSha256: item.pixel.sha256 }))));
    });
}

test('grading refuses missing captures and an unknown contracts revision', () => {
    const evidence = syntheticColorResetEvidence();
    const contract = colorResetContracts[0];
    assert.throws(() => colorResetObservation(contract, []), /Missing runtime-color read captures/);
    assert.throws(() => gradeColorResetCase(contract, {}), /Missing extensions.osc104-reset-palette read captures/);
    const reads = evidence.run.cases[0].reads.map((_, index) => evidence.frames[2 + index]);
    const observation = colorResetObservation(contract, reads.map(item => ({ screen: item.screen, queries: item.queries, details: item.pixel.renderDetails, pixelSha256: item.pixel.sha256 })));
    assert.throws(() => gradeColorResetCase(contract, observation, 2), /Unknown runtime-color reset contracts revision 2/);
    assert.equal(gradeColorResetCase(contract, observation).outcome, 'supported');
});
