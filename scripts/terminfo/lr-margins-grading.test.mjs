import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildLrMarginsGradingReport } from './lr-margins-grading.mjs';
import { lrMarginsContracts } from './lr-margins-contracts.mjs';
import { syntheticLrMarginsEvidence } from './fixtures/lr-margins-evidence.mjs';

const contract = lrMarginsContracts[0];
const build = evidence => buildLrMarginsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);

test('the pinned verdict is preserved inconclusive and the separately identified effect contract carries the graded verdict', () => {
    const [item] = build(syntheticLrMarginsEvidence()).cases;
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.equal(item.effectContract.outcome, 'supported');
    assert.equal(item.effectContract.id, 'ntilde-left-right-margins-effect-v1');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(item.pinned.response, '{"mode":true}');
    assert.ok(Object.values(item.observed.controls).every(control => control.matchesModel));
});

test('a control that differs from the model is an unsupported effect, still beside an inconclusive pinned verdict', () => {
    const evidence = syntheticLrMarginsEvidence('a'.repeat(32), 'f'.repeat(64), undefined, (role, model) => { if (role === 'ichDch') model.grid[0][9] = '?'; });
    const [item] = build(evidence).cases;
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /ichDch control differs/);
});

test('a mode the App did not report set leaves the effect inconclusive', () => {
    const evidence = syntheticLrMarginsEvidence('a'.repeat(32), 'f'.repeat(64), undefined, (role, model) => { if (role === 'set') model.mode = false; });
    const [item] = build(evidence).cases;
    assert.equal(item.effectContract.outcome, 'inconclusive');
    assert.equal(item.pinned.response, '{"mode":false}');
});

for (const [label, mutate, pattern] of [
    ['a pinned result that differs from its replay', e => { e.run.cases[0].pinned.response = '{"mode":false}'; }, /does not match its observed evidence/],
    ['a pinned verdict that is not inconclusive', e => { e.run.cases[0].pinned.observation = { outcome: 'supported', evidence: 'parser-state' }; }, /does not match|inconclusive by design/],
    ['a changed adapter identity', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-column-editing-grading-v1'; }, /adapter identity/],
    ['a frame without its stored margins', e => { delete e.frames[3].screen.horizontalMargins; e.frames[3].sha256 = 'x'; }, /horizontal margins|Changed/],
    ['changed feeds', e => { e.run.cases[0].feeds[1] = '\x1b[?69l'; }, /feeds do not match/],
]) {
    test('the report builder refuses ' + label, () => {
        const evidence = syntheticLrMarginsEvidence();
        mutate(evidence);
        assert.throws(() => build(evidence), pattern);
    });
}
