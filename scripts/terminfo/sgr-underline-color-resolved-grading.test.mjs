import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildUnderlineResolvedReport, validateUnderlineResolvedEvidence, underlineResolvedScreenOf } from './sgr-underline-color-resolved-grading.mjs';
import { underlineResolvedCaseIds, underlineResolvedContracts, underlineResolvedMutation } from './sgr-underline-color-resolved-contracts.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticUnderlineResolvedEvidence, underlineBinding, underlineCells } from './fixtures/underline-color-resolved-evidence.mjs';
import { paletteBinding } from './fixtures/sgr-palette-evidence.mjs';

const runId = 'a'.repeat(32);
const build = options => { const e = syntheticUnderlineResolvedEvidence(runId, options); return buildUnderlineResolvedReport(e.frames, e.run, runId); };

test('an honest run grades every case supported and retains App and parent values separately', () => {
    const report = build();
    assert.equal(report.kind, 'ntilde-extension-controls-not-terminfo-cases');
    assert.deepEqual(report.cases.map(item => item.caseId), [...underlineResolvedCaseIds]);
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'supported');
        assert.equal(item.check, 'ntilde-extension-contract');
        assert.equal(item.conclusion.evidence, 'owned-stored-and-app-resolved-underline');
        for (const group of ['before', 'mutated', 'restored']) {
            for (const cell of item.observed[group]) {
                assert.ok(cell.appObserved && cell.parentExpected, 'both fields present');
                assert.notEqual(cell.appObserved, cell.parentExpected);
                assert.equal(JSON.stringify(cell.appObserved), JSON.stringify(cell.parentExpected));
            }
        }
        assert.equal(item.bindings[1].entry5Argb, underlineResolvedMutation[5]);
        assert.deepEqual(item.bindings[1].paletteOverrideIndices, [4, 5]);
        assert.deepEqual(item.bindings[2].paletteOverrideIndices, []);
        assert.equal(item.bindings[0].underlineStage, 'underline-resolution');
    }
    assert.match(report.scope, /rendered\s+appearance is not claimed/);
});

test('the report is deterministic and contains no terminfo.dev verdict field', () => {
    assert.equal(JSON.stringify(build()), JSON.stringify(build()));
    const text = JSON.stringify(build().cases.map(item => Object.keys(item)));
    assert.doesNotMatch(text, /pinned/);
});

const refuse = (name, mutate, message = /./) => test('journal tampering is refused: ' + name, () => {
    const e = syntheticUnderlineResolvedEvidence(runId);
    mutate(e);
    assert.throws(() => buildUnderlineResolvedReport(e.frames, e.run, runId), message);
});
const rehash = frame => { frame.sha256 = snapshotHash(frame.screen); };
refuse('changed screen without rehash', e => { e.frames[3].screen.lines[0] = 'x'; }, /Changed underline-resolved snapshot evidence/);
refuse('wrong run nonce', e => { e.frames[1].screen.lines[0] = 'NTILDE_UR_' + 'b'.repeat(32); rehash(e.frames[1]); }, /not bound to this run/);
refuse('missing frame', e => { e.frames.splice(4, 1); }, /Incomplete underline-resolved/);
refuse('start and finish differ', e => { e.frames.at(-1).screen.cursorCol = 7; rehash(e.frames.at(-1)); }, /Changed|not restored/);
refuse('changed run suite', e => { e.run.suiteId = 'other'; }, /does not cover/);
refuse('changed case order', e => { e.run.cases.reverse(); }, /case order|do not match/);
refuse('changed feeds', e => { e.run.cases[0].feeds[1] += 'x'; }, /feeds do not match|Recorded/);
refuse('theme changed mid-run', e => {
    const frame = e.frames[5]; frame.screen.colorBinding = { ...frame.screen.colorBinding, foregroundArgb: 0xff010203 };
    // Keep the resolved defaults coherent so only the theme-stability rule can refuse it.
    frame.screen.resolvedColors = frame.screen.resolvedColors.map(run => ({ ...run, fg: run.fg.source === 'default' ? { ...run.fg, argb: 0xff010203 } : run.fg,
        ul: run.ul.source === 'inherit' && run.ul.inheritsFrom === 'foreground' && run.fg.source === 'default' ? { ...run.ul, argb: 0xff010203 } : run.ul }));
    rehash(frame);
}, /saw the theme change/);
refuse('underline stage removed from the binding', e => {
    const frame = e.frames[3]; const { underlineStage, underlineStageNote, ...rest } = frame.screen.colorBinding;
    frame.screen.colorBinding = rest; rehash(frame);
}, /Invalid resolved-color run|does not match the request/);
refuse('underline projection removed from the runs', e => {
    const frame = e.frames[3]; frame.screen.resolvedColors = frame.screen.resolvedColors.map(({ ul, ...run }) => run); rehash(frame);
}, /Invalid resolved-color run/);
refuse('an App-reported literal swapped for a palette cell', e => {
    const frame = e.frames[3]; const runs = frame.screen.resolvedColors;
    runs[0] = { ...runs[0], ul: { source: 'literal', index: -1, argb: 0xff445566 } }; rehash(frame);
}, /underline|Underline|run/);

test('a stale mutated read that equals the start palette makes the case inconclusive, not supported', () => {
    const report = build({ bindingOf: (contract, phase) => underlineBinding(phase === 'mutated' ? 'before' : phase) });
    for (const item of report.cases) assert.equal(item.conclusion.outcome, 'inconclusive');
});

test('wrong stored setup across the whole run is inconclusive with an explanatory note', () => {
    const report = build({ cellsOf: contract => underlineCells(contract).map(cell => ({ ...cell, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null })) });
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.match(item.conclusion.note, /not prepared|not exposed/);
    }
});

test('start-to-finish palette restoration and the screen validator are enforced', () => {
    const e = syntheticUnderlineResolvedEvidence(runId);
    assert.doesNotThrow(() => validateUnderlineResolvedEvidence(e.frames, e.run, runId));
    assert.doesNotThrow(() => underlineResolvedScreenOf(e.frames[2].screen));
    const plain = { ...e.frames[2].screen, colorBinding: paletteBinding() };
    assert.throws(() => underlineResolvedScreenOf(plain), /does not match the request|Invalid resolved-color run/);
    assert.equal(underlineResolvedContracts.length, 3);
});
