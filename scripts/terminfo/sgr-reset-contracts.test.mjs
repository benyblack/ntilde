import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrResetContracts, sgrResetCaseIds, sgrResetContractsVersion, sgrResetSuiteId, sgrResetNoncePrefix,
    gradeSgrResetCase, sgrResetObservation, sgrResetFeeds, sgrResetReads, validateSgrResetFeeds,
    pinnedNotExposedNote, pinnedControlNote } from './sgr-reset-contracts.mjs';
import { syntheticSgrResetReads, syntheticSgrResetScreens, syntheticSgrResetGrade, styled, plainCell, paletteBinding,
    reverseBinding, colorCell } from './fixtures/sgr-reset-evidence.mjs';

const UNDERLINE = 'sgr.selective-reset.underline';
const INVERSE = 'sgr.selective-reset.inverse';
const byId = id => sgrResetContracts.find(contract => contract.caseId === id);
/** Grade a synthetic case after editing its reads; `edit` mutates the per-role cell specs. */
const grade = (caseId, edit, binding) => syntheticSgrResetGrade(caseId, binding, id => {
    const reads = syntheticSgrResetReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const outcome = (caseId, edit, binding) => grade(caseId, edit, binding).outcome;

test('the contracts pin exactly the two verified feature ids, fixtures and measuredReset criterion', () => {
    assert.deepEqual([...sgrResetCaseIds], [UNDERLINE, INVERSE]);
    assert.deepEqual(sgrResetContracts.map(contract => [contract.setup, contract.reset, [...contract.set], [...contract.preserve], [...contract.pinnedFeeds]]), [
        ['\x1b[1;4m', '\x1b[24m', ['underline'], ['bold'], ['C\x1b[1;4mX\x1b[24mY']],
        ['\x1b[1;7m', '\x1b[27m', ['inverse'], ['bold'], ['C\x1b[1;7mX\x1b[27mY']],
    ]);
    assert.equal(sgrResetContractsVersion, 1);
    assert.equal(sgrResetSuiteId, 'ntilde-owned-sgr-selective-reset-graded-v1');
    assert.equal(sgrResetNoncePrefix, 'NTILDE_SR_');
    for (const contract of sgrResetContracts) {
        assert.equal(contract.pinnedExpected, `${contract.caseId}: measured setup attributes clear on Y and unrelated attributes remain set`);
        assert.deepEqual(contract.terminalFixture.writes, [`${contract.setup}X${contract.reset}Y`]);
        assert.match(contract.appearanceScope, /not claimed/);
        assert.equal(sgrResetReads(contract), contract.roles.length);
        assert.doesNotThrow(() => validateSgrResetFeeds(contract, sgrResetFeeds(contract)));
        assert.throws(() => validateSgrResetFeeds(contract, [...sgrResetFeeds(contract).slice(0, -1), 'x']), /do not match/);
    }
    assert.equal(sgrResetReads(byId(UNDERLINE)), 8);
    assert.equal(sgrResetReads(byId(INVERSE)), 6);
});

test('the pinned fixture is replayed as ONE byte-exact feed and the controls never alter it', () => {
    for (const contract of sgrResetContracts) {
        const feeds = sgrResetFeeds(contract);
        assert.ok(feeds.includes(contract.pinnedFeeds[0]), 'the exact pinned bytes are fed verbatim');
        assert.equal(feeds.filter(feed => feed === contract.pinnedFeeds[0]).length, 1);
        assert.equal(contract.pinnedRead, 1);
    }
});

test('naming: no sibling variant is substituted for the two pinned ids', () => {
    for (const contract of sgrResetContracts) {
        assert.ok(!/sgr\.(underline\.single|underline-color-reset|reset|inverse)$/.test(contract.caseId));
        assert.notEqual(contract.reset, '\x1b[59m');
        assert.notEqual(contract.reset, '\x1b[0m');
    }
    const labels = byId(UNDERLINE).plan.filter(step => step.feed).map(step => step.feed).join('|');
    assert.match(labels, /\x1b\[59m/); // SGR 59 is a labeled control only
    assert.match(labels, /\x1b\[0m/);  // as is the accidental full reset
});

test('a complete synthetic run is conclusively supported for both cases', () => {
    assert.deepEqual(syntheticSgrResetGrade(UNDERLINE), { outcome: 'supported' });
    assert.deepEqual(syntheticSgrResetGrade(INVERSE), { outcome: 'supported' });
});

test('initially active targets and neutral-baseline failures are inconclusive, never graded', () => {
    assert.equal(outcome(UNDERLINE, reads => { reads.initial = [styled(' ', [], { style: 1 })]; }), 'inconclusive');
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[0] = styled('C', [], { style: 1 }); }), 'inconclusive');
    assert.equal(outcome(INVERSE, reads => { reads.pinned[0] = styled('C', ['inverse']); }), 'inconclusive');
    assert.equal(outcome(INVERSE, reads => { reads.initial = [styled(' ', ['bold'])]; }), 'inconclusive');
});

test('setup not stored (target attribute or bold absent on X) is an invalid control, not unsupported', () => {
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[1] = styled('X', ['bold']); }), 'inconclusive');
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[1] = styled('X', [], { style: 1 }); }), 'inconclusive');
    assert.equal(outcome(INVERSE, reads => { reads.pinned[1] = styled('X', ['bold']); }), 'inconclusive');
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[1] = styled('X', ['bold'], { style: 2 }); }), 'inconclusive', 'double is not the single style');
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[1] = styled('X', ['bold', 'italic'], { style: 1 }); }), 'inconclusive');
});

test('a no-op reset is unsupported once the no-reset control proves the readback shows persistence', () => {
    for (const [caseId, kept] of [[UNDERLINE, styled('Y', ['bold'], { style: 1 })], [INVERSE, styled('Y', ['bold', 'inverse'])]]) {
        const result = grade(caseId, reads => { reads.pinned[2] = kept; });
        assert.equal(result.outcome, 'unsupported');
        assert.match(result.note, /survived/);
    }
});

test('an accidental full SGR 0 (bold cleared with the target) is unsupported with the controls valid', () => {
    for (const caseId of [UNDERLINE, INVERSE]) {
        const result = grade(caseId, reads => { reads.pinned[2] = plainCell('Y'); });
        assert.equal(result.outcome, 'unsupported');
        assert.match(result.note, /accidental full reset/);
    }
});

test('SGR 24 implemented as SGR 59 (underline survives) and SGR 27 implemented as SGR 24 are unsupported', () => {
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[2] = styled('Y', ['bold'], { style: 1 }); }), 'unsupported');
    assert.equal(outcome(INVERSE, reads => { reads.pinned[2] = styled('Y', ['bold', 'inverse']); }), 'unsupported');
});

test('unrelated attribute, stored color and overline drift on Y is unsupported (stricter than the pinned probe)', () => {
    for (const caseId of [UNDERLINE, INVERSE]) {
        for (const drift of [['italic'], ['strikethrough'], ['hidden'], ['overline'], ['dim']]) {
            const result = grade(caseId, reads => { reads.pinned[2] = styled('Y', ['bold', ...drift]); });
            assert.equal(result.outcome, 'unsupported', `${caseId} ${drift}`);
            assert.match(result.note, /drifted/);
        }
        const colored = grade(caseId, reads => { reads.pinned[2] = colorCell('Y', { attrs: ['bold'], fg: { kind: 'palette', index: 1 } }); });
        assert.equal(colored.outcome, 'unsupported');
    }
    // A stored underline on Y (with its color) is drift too: the pinned fixture sets none, and a color never exists without a style.
    assert.equal(outcome(INVERSE, reads => { reads.pinned[2] = styled('Y', ['bold'], { style: 1, rgb: { r: 1, g: 2, b: 3 } }); }), 'unsupported');
    assert.throws(() => grade(INVERSE, reads => { reads.pinned[2] = styled('Y', ['bold'], { rgb: { r: 1, g: 2, b: 3 } }); }), /Invalid stored cell detail run/);
});

test('stored color or style drift on the setup cell invalidates the control', () => {
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[1] = colorCell('X', { attrs: ['bold'], underlineStyle: 1, bg: { kind: 'palette', index: 4 } }); }), 'inconclusive');
    assert.equal(outcome(INVERSE, reads => { reads.pinned[1] = colorCell('X', { attrs: ['bold', 'inverse'], fg: { kind: 'literal', rgb: { r: 1, g: 2, b: 3 } } }); }), 'inconclusive');
    assert.equal(outcome(UNDERLINE, reads => { reads.pinned[0] = colorCell('C', { fg: { kind: 'palette', index: 2 } }); }), 'inconclusive');
});

test('the no-reset, full-reset and wrong-reset controls must each hold before any verdict', () => {
    for (const caseId of [UNDERLINE, INVERSE]) {
        const target = caseId === UNDERLINE ? styled('Z', ['bold'], { style: 1 }) : styled('Z', ['bold', 'inverse']);
        void target;
        assert.equal(outcome(caseId, reads => { reads.noop[2] = styled('Z', ['bold']); }), 'inconclusive', 'persistence not shown');
        assert.equal(outcome(caseId, reads => { reads.noop[2] = styled('Q', ['bold']); }), 'inconclusive', 'wrong cell');
        assert.equal(outcome(caseId, reads => { reads.full[2] = styled('Y', ['bold']); }), 'inconclusive', 'full reset did not neutralize');
        assert.equal(outcome(caseId, reads => { reads.wrong[2] = plainCell('Y'); }), 'inconclusive', 'wrong reset cleared the target');
    }
    // SGR 22 control: bold must be cleared but underline kept.
    assert.equal(outcome(UNDERLINE, reads => { reads.wrong[2] = styled('Y', ['bold'], { style: 1 }); }), 'inconclusive');
    // SGR 24 control for inverse: both inverse and bold must remain.
    assert.equal(outcome(INVERSE, reads => { reads.wrong[2] = styled('Y', ['bold']); }), 'inconclusive');
});

test('DECSCNM confusion: global reverse video must be off in graded reads and distinct from per-cell inverse', () => {
    const on = reverseBinding(paletteBinding());
    for (const caseId of [UNDERLINE, INVERSE]) {
        assert.equal(outcome(caseId, undefined, on), 'inconclusive', 'DECSCNM on in every read');
    }
    // The DECSCNM-on control did not engage.
    assert.equal(outcome(INVERSE, reads => { reads.reverseBinding = paletteBinding(); }), 'inconclusive');
    // Per-cell storage must not change under DECSCNM.
    assert.equal(outcome(INVERSE, reads => { reads.decscnm = [plainCell('C'), styled('X', ['bold']), styled('Y', ['bold'])]; }), 'inconclusive');
    assert.equal(outcome(INVERSE, reads => { reads.decscnm = [styled('C', ['inverse']), styled('X', ['bold', 'inverse']), styled('Y', ['bold'])]; }), 'inconclusive');
    // The underline case never needs a DECSCNM-on control.
    assert.equal(byId(UNDERLINE).reverseControlRead, undefined);
    assert.equal(byId(INVERSE).reverseControlRead, 5);
});

test('SGR 24 vs SGR 59: underline color behavior is observed, never required', () => {
    // A color is never stored without a style, so after SGR 24 the observable variable is whether re-enabling brings it back.
    const colorReads = (_unused, zColor) => reads => {
        reads.color = [plainCell('C'), styled('X', ['bold'], { style: 1, rgb: { r: 255, g: 0, b: 128 } }),
            styled('Y', ['bold']),
            styled('Z', ['bold'], { style: 1, ...(zColor ? { rgb: { r: 255, g: 0, b: 128 } } : {}) })];
    };
    for (const zColor of [false, true]) assert.equal(outcome(UNDERLINE, colorReads(false, zColor)), 'supported', `${zColor}`);
    const contract = byId(UNDERLINE);
    const behavior = flag => contract.observed(sgrResetObservation(contract, syntheticSgrResetScreens(UNDERLINE, paletteBinding(),
        id => { const reads = syntheticSgrResetReads(id); colorReads(flag, flag)(reads); return reads; }))).underlineColorBehavior;
    assert.equal(behavior(false).sgr24ClearsStoredColor, true);
    assert.equal(behavior(false).sgr24ColorSurvivesReEnable, false);
    assert.equal(behavior(true).sgr24ColorSurvivesReEnable, true);
    assert.match(behavior(false).policy, /not required/);
    // SGR 59 leaves the underline in place while clearing the color (the contrast with SGR 24).
    assert.equal(behavior(false).sgr59KeepsUnderline, true);
    assert.equal(behavior(false).sgr59ClearsStoredColor, true);
});

test('malformed flag/style combinations are refused as contradictory evidence, not graded', () => {
    const screens = syntheticSgrResetScreens(UNDERLINE);
    const broken = structuredClone(screens);
    // Underline flag cleared in the attribute runs while the stored style stays single.
    broken[1].attributeRuns = broken[1].attributeRuns.map(run => ({ ...run, flags: run.flags & ~0x0008 }));
    assert.throws(() => sgrResetObservation(byId(UNDERLINE), broken), /Contradictory stored cell projections|disagree/);
    assert.throws(() => sgrResetObservation(byId(UNDERLINE), screens.slice(0, -1)), /Missing SGR selective-reset read captures/);
});

test('the contract grader refuses missing read groups and unknown revisions', () => {
    const contract = byId(UNDERLINE);
    const observation = sgrResetObservation(contract, syntheticSgrResetScreens(UNDERLINE));
    assert.deepEqual(gradeSgrResetCase(contract, observation), { outcome: 'supported' });
    assert.throws(() => gradeSgrResetCase(contract, observation, 2), /Unknown SGR selective-reset contracts revision/);
    for (const group of ['pinned', 'noop', 'full', 'wrong', 'double', 'sgr59', 'color']) {
        assert.throws(() => gradeSgrResetCase(contract, { ...observation, [group]: observation[group].slice(1) }), /Missing/, group);
    }
    assert.throws(() => gradeSgrResetCase(contract, { ...observation, initial: undefined }), /Missing/);
});

test('observed records carry every group, and pinned notes are the pinned strings', () => {
    for (const contract of sgrResetContracts) {
        const record = contract.observed(sgrResetObservation(contract, syntheticSgrResetScreens(contract.caseId)));
        assert.equal(record.pinned.length, 3);
        assert.ok(record.controls.fullReset && record.controls.noReset);
        assert.equal(record.reverseVideo, false);
    }
    assert.equal(pinnedNotExposedNote, 'Baseline, styled, or reset cell was not exposed');
    assert.equal(pinnedControlNote, 'Neutral baseline, styled setup, or reset readback was not measured');
});

test('wrong characters on the pinned fixture are not exposed (pinned note)', () => {
    const result = grade(UNDERLINE, reads => { reads.pinned[1] = styled('Q', ['bold'], { style: 1 }); });
    assert.deepEqual(result, { outcome: 'inconclusive', note: pinnedNotExposedNote });
});
