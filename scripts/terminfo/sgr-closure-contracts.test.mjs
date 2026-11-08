import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrClosureContracts, sgrClosureCaseIds, sgrClosureContractsVersion, sgrClosureSuiteId, sgrClosureNoncePrefix,
    gradeSgrClosureCase, sgrClosureObservation, sgrClosureFeeds, sgrClosureReads, validateSgrClosureFeeds,
    pinnedNoRenderedCellNote, pinnedOverlineUnavailableNote, pinnedUnreportedNote } from './sgr-closure-contracts.mjs';
import { sgrUnderlineColorContracts } from './sgr-underline-color-contracts.mjs';
import { replaySgrProbeCell } from './sgr-closure-grading.mjs';
import { syntheticSgrClosureReads, syntheticSgrClosureScreens, syntheticSgrClosureGrade, styled, plainCell, colorCell,
    paletteBinding } from './fixtures/sgr-closure-evidence.mjs';

const BLINK = 'sgr.blink', OVERLINE = 'sgr.overline', COLOR = 'sgr.underline.color';
const byId = id => sgrClosureContracts.find(contract => contract.caseId === id);
const grade = (caseId, edit, binding) => syntheticSgrClosureGrade(caseId, binding, id => {
    const reads = syntheticSgrClosureReads(id);
    if (id === caseId && edit) edit(reads.groups);
    return reads;
});
const outcome = (caseId, edit, binding) => grade(caseId, edit, binding).outcome;

test('the contracts pin exactly the three verified ids and fixtures; no related id is substituted', () => {
    assert.deepEqual([...sgrClosureCaseIds], [BLINK, OVERLINE, COLOR]);
    assert.deepEqual([...byId(BLINK).pinnedFeeds], ['\x1b[5mX']);
    assert.deepEqual([...byId(OVERLINE).pinnedFeeds], ['\x1b[53mX']);
    assert.deepEqual([...byId(COLOR).pinnedFeeds], ['\x1b[4m\x1b[59m\x1b[38;2;0;0;255mA\x1b[38;2;0;255;0mB\x1b[58;2;255;0;128mX']);
    assert.equal(sgrClosureContractsVersion, 1);
    assert.equal(sgrClosureSuiteId, 'ntilde-owned-sgr-closure-graded-v1');
    assert.equal(sgrClosureNoncePrefix, 'NTILDE_CL_');
    for (const contract of sgrClosureContracts) {
        assert.equal(contract.caseId, contract.probeId);
        assert.match(contract.appearanceScope, /not claimed/);
        assert.doesNotThrow(() => validateSgrClosureFeeds(contract, sgrClosureFeeds(contract)));
        assert.throws(() => validateSgrClosureFeeds(contract, [...sgrClosureFeeds(contract).slice(0, -1), 'x']), /do not match/);
        assert.equal(sgrClosureReads(contract), contract.reads);
    }
    assert.deepEqual(sgrClosureContracts.map(contract => contract.reads), [4, 5, 2]);
    assert.ok(!sgrClosureCaseIds.some(id => /faint|hidden|strikethrough|underline\.(single|double)|underline-color|fg\.|bg\./.test(id)));
});

test('sgr.underline.color shares the sibling helper fixture but keeps its own id, pinned text and reads', () => {
    const sibling = sgrUnderlineColorContracts.find(contract => contract.caseId === 'sgr.underline-color-rgb');
    const own = byId(COLOR);
    assert.notEqual(own.caseId, sibling.caseId);
    assert.deepEqual([...own.pinnedFeeds], [...sibling.pinnedFeeds], 'identical fixture bytes through the shared rgbUnderlineProbe');
    assert.deepEqual(own.plan, sibling.plan, 'identical controls');
    assert.equal(own.pinnedExpected, 'sgr.underline.color: X has explicit rgb(255,0,128) underline distinct from the reset/default B cell');
    assert.equal(sibling.pinnedExpected, 'sgr.underline-color-rgb: X has explicit rgb(255,0,128) underline distinct from the reset/default B cell');
    assert.equal(own.siblingCaseId, sibling.caseId);
    assert.equal(own.grade, sibling.grade, 'the same independent grader runs over THIS case reads');
});

test('complete synthetic runs are conclusively supported', () => {
    assert.deepEqual(syntheticSgrClosureGrade(BLINK), { outcome: 'supported' });
    assert.deepEqual(syntheticSgrClosureGrade(OVERLINE), { outcome: 'supported' });
    assert.equal(syntheticSgrClosureGrade(COLOR).outcome, 'supported');
});

for (const [caseId, attr] of [[BLINK, 'blink'], [OVERLINE, 'overline']]) {
    test(`${caseId}: neutral setup, requested transition and reset restoration are required before any verdict`, () => {
        assert.equal(outcome(caseId, groups => { groups[0] = [styled('C', [attr])]; }), 'inconclusive', 'initially active');
        assert.equal(outcome(caseId, groups => { groups[1] = [styled('Q', [attr])]; }), 'inconclusive', 'target not rendered');
        assert.deepEqual(grade(caseId, groups => { groups[1] = [styled('Q', [attr])]; }), { outcome: 'inconclusive', note: pinnedNoRenderedCellNote });
        assert.equal(outcome(caseId, groups => { groups[2] = [styled('X', [attr]), styled('Y', [attr])]; }), 'inconclusive', 'reset did not neutralize Y');
        assert.equal(outcome(caseId, groups => { groups[2] = [plainCell('X'), plainCell('Y')]; }), 'inconclusive', 'X changed by the reset');
    });
    test(`${caseId}: an exposed false value with valid controls is unsupported`, () => {
        const result = grade(caseId, groups => { groups[1] = [plainCell('X')]; groups[2] = [plainCell('X'), plainCell('Y')]; });
        assert.equal(result.outcome, 'unsupported');
        assert.match(result.note, /exposed as false/);
    });
    test(`${caseId}: unrelated attribute and color drift invalidates the setup control`, () => {
        for (const extra of ['bold', 'italic', 'dim', 'inverse', 'hidden', 'strikethrough', attr === 'blink' ? 'overline' : 'blink']) {
            assert.equal(outcome(caseId, groups => { groups[1] = [styled('X', [attr, extra])]; groups[2][0] = styled('X', [attr, extra]); }), 'inconclusive', extra);
        }
        assert.equal(outcome(caseId, groups => { groups[1] = [colorCell('X', { attrs: [attr], fg: { kind: 'palette', index: 1 } })]; }), 'inconclusive');
        // A generic underline is never accepted in place of the requested attribute.
        assert.equal(outcome(caseId, groups => { groups[1] = [styled('X', [], { style: 1 })]; groups[2][0] = styled('X', [], { style: 1 }); }), 'inconclusive');
    });
    test(`${caseId}: the decoy control proves the flag is independent of every other attribute`, () => {
        assert.equal(outcome(caseId, groups => { groups[3] = [styled('W', ['bold'], { style: 1 })]; }), 'inconclusive', 'decoy shape wrong');
        assert.equal(outcome(caseId, groups => { groups[3][0] = styled('W', [...groups[3][0] ? ['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', attr === 'blink' ? 'overline' : 'blink', attr] : []], { style: 1 }); }), 'inconclusive', 'decoy reads the target');
    });
}

test('overline: the overline field is an explicit boolean and never inferred from underline', () => {
    // Underline-only cell is not an overline.
    assert.equal(outcome(OVERLINE, groups => { groups[1] = [styled('X', [], { style: 1 })]; }), 'inconclusive');
    // Independence group: overline survives the underline reset only when overline works; a broken one is still gradeable.
    assert.equal(outcome(OVERLINE, groups => { groups[4] = [styled('V', ['overline'], { style: 1 }), styled('U', []), plainCell('T')]; }), 'inconclusive', 'overline lost with the underline');
    assert.equal(outcome(OVERLINE, groups => { groups[4] = [styled('V', ['overline'], { style: 1 }), styled('U', ['overline']), styled('T', ['overline'])]; }), 'inconclusive', 'overline survived SGR 55');
    const broken = grade(OVERLINE, groups => { groups[1] = [plainCell('X')]; groups[2] = [plainCell('X'), plainCell('Y')];
        groups[4] = [styled('V', [], { style: 1 }), plainCell('U'), plainCell('T')]; });
    assert.equal(broken.outcome, 'unsupported', 'no overline anywhere: valid underline-independence shape, observed mismatch');
});

test('overline: the pinned false-versus-unavailable distinction is preserved by the replay', () => {
    const contract = byId(OVERLINE);
    const cell = overline => ({ char: 'X', overline, blink: false });
    const unavailable = replaySgrProbeCell(contract, cell(undefined));
    assert.equal(unavailable.pass, false);
    assert.equal(unavailable.note, pinnedOverlineUnavailableNote);
    assert.deepEqual(unavailable.notTested, { reason: 'no-semantic-observable', noObservable: 'cell.overline field not exposed' });
    assert.equal(unavailable.observation, undefined);
    const exposedFalse = replaySgrProbeCell(contract, cell(false));
    assert.equal(exposedFalse.observation.outcome, 'inconclusive');
    assert.equal(exposedFalse.note, pinnedUnreportedNote);
    assert.equal(replaySgrProbeCell(contract, cell(true)).observation.outcome, 'supported');
    assert.equal(replaySgrProbeCell(contract, { ...cell(undefined), char: 'Q' }).note, pinnedNoRenderedCellNote);
    // Blink has no such guard: an undefined blink field is the generic inconclusive.
    assert.equal(replaySgrProbeCell(byId(BLINK), { char: 'X', blink: undefined }).observation.outcome, 'inconclusive');
});

test('a missing overline field in the stored details is refused, never read as false', () => {
    const screens = JSON.parse(JSON.stringify(syntheticSgrClosureScreens(OVERLINE)));
    for (const screen of screens) for (const run of screen.cellDetails) delete run.overline;
    assert.throws(() => sgrClosureObservation(byId(OVERLINE), screens, paletteBinding()), /overline|Invalid stored cell detail/i);
});

test('blink: a wrong stored flag (bold or italic standing in for blink) is not blink', () => {
    assert.equal(outcome(BLINK, groups => { groups[1] = [styled('X', ['bold'])]; groups[2][0] = styled('X', ['bold']); }), 'inconclusive');
    const noFlag = grade(BLINK, groups => { groups[1] = [plainCell('X')]; groups[2] = [plainCell('X'), plainCell('Y')]; });
    assert.equal(noFlag.outcome, 'unsupported');
});

test('sgr.underline.color: independent grading, default null color, sibling literal/index controls', () => {
    assert.equal(outcome(COLOR, groups => { groups[0][2] = styled('X', [], { style: 1 }); }), 'inconclusive', 'no explicit literal');
    const reads = syntheticSgrClosureReads(COLOR);
    const [a, b, x] = syntheticSgrClosureScreens(COLOR).slice(0, 1).map(screen => screen);
    void a; void b; void x; void reads;
    const contract = byId(COLOR);
    const observation = sgrClosureObservation(contract, syntheticSgrClosureScreens(COLOR), paletteBinding());
    assert.equal(observation.fixture[0].underlineColor, null, 'the default underline color keeps the pinned null representation');
    assert.equal(observation.fixture[0].underlineSource, 'default');
    assert.deepEqual(observation.fixture[2].underlineColor, { r: 255, g: 0, b: 128 });
    assert.equal(observation.reverseVideo, false);
    assert.throws(() => gradeSgrClosureCase(contract, { ...observation, fixture: observation.fixture.slice(1) }), /Missing/);
});

test('DECSCNM confusion makes every case inconclusive', () => {
    const base = paletteBinding();
    const on = { ...base, reverseVideo: true, foregroundArgb: base.backgroundArgb, backgroundArgb: base.foregroundArgb };
    for (const caseId of [BLINK, OVERLINE]) assert.equal(outcome(caseId, undefined, on), 'inconclusive');
    assert.equal(outcome(COLOR, undefined, on), 'inconclusive');
});

test('grader entry refuses unknown revisions and missing read groups', () => {
    const contract = byId(BLINK);
    const observation = sgrClosureObservation(contract, syntheticSgrClosureScreens(BLINK), paletteBinding());
    assert.deepEqual(gradeSgrClosureCase(contract, observation), { outcome: 'supported' });
    assert.throws(() => gradeSgrClosureCase(contract, observation, 2), /Unknown SGR-closure contracts revision/);
    assert.throws(() => gradeSgrClosureCase(contract, { ...observation, decoy: undefined }), /Missing/);
    assert.throws(() => gradeSgrClosureCase(contract, { ...observation, trailing: observation.trailing.slice(1) }), /Missing/);
    assert.throws(() => sgrClosureObservation(contract, syntheticSgrClosureScreens(BLINK).slice(1), paletteBinding()), /Missing sgr.blink read captures/);
    assert.equal(contract.observed(observation).controls.decoy.char, 'W');
});
