import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildDevqGradingReport, validateDevqEvidence } from './devq-grading.mjs';
import { devqContracts } from './devq-contracts.mjs';
import { syntheticDevqEvidence, syntheticDevqExchanges } from './fixtures/devq-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';
import { snapshotHash } from './agent-observer.mjs';

const build = evidence => buildDevqGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const contractOf = caseId => devqContracts.find(item => item.caseId === caseId);
/** The index of the frame, and of the run-log read, of a role of a case. */
const frameOf = (caseId, role) => {
    let index = 2;
    for (const contract of devqContracts) {
        if (contract.caseId === caseId) return index + contract.roles.indexOf(role);
        index += contract.roles.length;
    }
    throw new Error(`unknown case ${caseId}`);
};
const readOf = (caseId, role) => contractOf(caseId).roles.indexOf(role);
const runCase = (evidence, caseId) => evidence.run.cases.find(item => item.caseId === caseId);
const da = '\x1b[?62;4;22c';

test('a complete synthetic run rederives the replayed pinned verdicts, the supported effect contracts and the XTREPORTCOLORS divergence', () => {
    const report = build(syntheticDevqEvidence());
    assert.equal(report.cases.length, 4);
    const [xres, colors, icon, windowTitle] = report.cases;
    for (const item of [xres, icon, windowTitle]) {
        assert.equal(item.pinned.termless.observation.outcome, 'supported');
        assert.equal(item.pinned.terminal.observation.outcome, 'supported');
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.outcome, 'supported');
        assert.equal(item.conclusion.grading, 'pinned-termless-replayed');
    }
    assert.equal(colors.pinned.termless.observation.outcome, 'inconclusive');
    assert.equal(colors.pinned.termless.observation.reason, 'invalid-reply');
    assert.equal(colors.pinned.terminal.response, '\x1b[?0;0#Q' + da, 'the terminal replay sees the raw bytes with the barrier');
    assert.equal(colors.effectContract.id, 'ntilde-device-query-report-effect-v1');
    assert.equal(colors.effectContract.outcome, 'supported');
    assert.deepEqual(colors.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-effect-supported' });
    assert.equal(colors.conclusion.outcome, 'inconclusive', 'the published conclusion is the replayed pinned verdict');
    assert.equal(colors.conclusion.reason, 'invalid-reply');
    assert.deepEqual(xres.reads[readOf('device.xtgetxres', 'list-stop')].replies, syntheticDevqExchanges['device.xtgetxres']['list-stop']);
    assert.equal(xres.reads[0].replies, undefined, 'a cells read carries no replies');
    for (const item of report.cases) assert.equal(Object.values(item.observed.groups).every(group => group.matches && group.unchanged), true, item.caseId);
    assert.equal(report.snapshots.length, 3 + devqContracts.reduce((sum, contract) => sum + contract.roles.length, 0));
    assert.equal(report.suiteId, 'ntilde-owned-device-queries-graded-v1');
});

test('the effect contract grades a substituted value as unsupported while the pinned verdict follows the pinned code', () => {
    // termName answers a different TERM: the pinned pattern still calls any hex value supported, the oracle does not.
    const wrong = syntheticDevqEvidence({ mutate: (caseId, role, read) => (caseId === 'device.xtgetxres' && role === 'pinned' ? { ...read, replies: ['\x1bP1+R7465726d4e616d65=41\x1b\\'] } : null) });
    const [item] = build(wrong).cases;
    assert.equal(item.pinned.termless.observation.outcome, 'supported');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /pinned reply differs from the reply oracle/);
    assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'pinned-supported-effect-unsupported' });
    assert.equal(item.conclusion.outcome, 'supported', 'a recorded divergence never rewrites the pinned verdict');
});

test('a wrong icon label is contradicted by the pinned code and by the oracle', () => {
    const evidence = syntheticDevqEvidence({ mutate: (caseId, role, read) => (caseId === 'device.xtwinops-20' && role === 'pinned' ? { ...read, replies: ['\x1b]Lother\x1b\\'] } : null) });
    const item = build(evidence).cases.find(entry => entry.caseId === 'device.xtwinops-20');
    assert.equal(item.pinned.termless.observation.outcome, 'unsupported');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.divergence, undefined);
    assert.equal(item.conclusion.outcome, 'unsupported');
});

test('the effect contract grades missing, partial, extra and reordered replies as unsupported', () => {
    const grade = (caseId, role, change) => build(syntheticDevqEvidence({ mutate: (id, current, read) => (id === caseId && current === role ? { ...read, replies: change(read.replies) } : null) }))
        .cases.find(item => item.caseId === caseId).effectContract;
    assert.match(grade('device.xtgetxres', 'unknown', () => []).note, /unknown request was not answered/);
    assert.match(grade('device.xtreportcolors', 'pop-1', replies => replies.slice(0, 2)).note, /pop-1 reply count differs/);
    assert.match(grade('device.xtwinops-21', 'stack', replies => [...replies, '\x1b]lstk-1\x1b\\']).note, /reply count differs/);
    assert.match(grade('device.xtwinops-20', 'distinct', replies => [...replies].reverse()).note, /reply differs from the reply oracle/);
    assert.equal(grade('device.xtreportcolors', 'set-second', () => []).outcome, 'inconclusive', 'a missing control reply is a failed setup, not a verdict');
});

test('a state-changing request and an unrelated state drift are unsupported', () => {
    const drift = syntheticDevqEvidence({ mutate: (caseId, role, read) => {
        if (caseId !== 'device.xtwinops-21' || role !== 'stable') return null;
        const screen = structuredClone(read.screen);
        screen.lines[5] = 'stale'; screen.cursorRow = 5; screen.cursorCol = 5;
        return { ...read, screen };
    } });
    const item = build(drift).cases.find(entry => entry.caseId === 'device.xtwinops-21');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /stable request changed the screen/);
    assert.equal(item.observed.groups.stable.unchanged, false);
});

const x = 'device.xtgetxres', c = 'device.xtreportcolors', i = 'device.xtwinops-20';
for (const [label, mutate, pattern] of [
    ['a journaled reply list that differs from the worker record', e => { e.frames[frameOf(x, 'pinned')].report.replies = []; }, /replies differ from their read frame/],
    ['a worker record that differs from the journal', e => { runCase(e, i).reads[readOf(i, 'stable')].report.replies = ['\x1b]Lx\x1b\\']; }, /replies differ from their read frame/],
    ['a report on a cells read', e => { e.frames[frameOf(x, 'initial')].report = { barrier: e.frames[frameOf(x, 'initial')].barrier, replies: [] }; }, /report on a cells read/],
    ['a report read without its report', e => { delete e.frames[frameOf(x, 'unknown')].report; delete runCase(e, x).reads[readOf(x, 'unknown')].report; }, /device-query/],
    ['a report on the start frame', e => { e.frames[0].report = { barrier: da, replies: [] }; }, /Unexpected device-query report frame/],
    ['a pinned termless result that differs from its replies', e => { runCase(e, x).pinned.termless.observation = { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' }; }, /does not match its observed evidence/],
    ['a pinned terminal result that differs from the journaled bytes', e => { runCase(e, c).pinned.terminal.response = ''; }, /does not match its observed evidence/],
    ['a pinned result with another evidence kind', e => { runCase(e, i).pinned.termless.observation.evidence = 'parser-state'; }, /Missing pinned/],
    ['a pinned result that claims support', e => { runCase(e, c).pinned.termless.pass = true; }, /does not match its observed evidence/],
    ['a changed adapter identity', e => { e.adapter.id = 'other'; }, /adapter identity/],
    ['a changed predecessor', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-title-stack-grading-v1'; }, /adapter identity/],
    ['a truncated journal', e => { e.frames.pop(); }, /Incomplete device-query snapshot sequence/],
    ['changed feeds', e => { runCase(e, x).feeds[1] = '\x1bP+Q5\x1b\\'; }, /feeds do not match/],
    ['an uncovered case list', e => { e.run.probeIds = []; }, /does not cover the contracted cases/],
    ['a reordered case list', e => { e.run.cases.reverse(); }, /case order differs/],
    ['a stale reply barrier', e => { e.frames[frameOf(x, 'echo-upper')].report.barrier = '\x1b[?1c'; }, /replies differ from their read frame/],
    ['a report not ordered by its own barrier', e => { e.frames[frameOf(x, 'echo-upper')].barrier = '\x1b[?1c'; }, /barrier differs from its read frame/],
]) {
    test('the report builder refuses ' + label, () => {
        const evidence = syntheticDevqEvidence();
        mutate(evidence);
        assert.throws(() => build(evidence), pattern);
    });
}

test('the evidence check alone binds the run identity and the shared theme', () => {
    const evidence = syntheticDevqEvidence();
    assert.doesNotThrow(() => validateDevqEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => validateDevqEvidence(evidence.frames, evidence.run, 'b'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('only the recoloring case may carry another colour binding, and the finish must restore the start state', () => {
    const recolored = paletteBinding({ background: 0xff010203 });
    // The colour-stack case legitimately recolors the pane: its own frames may carry another binding.
    const allowed = syntheticDevqEvidence({ recolored: { caseId: 'device.xtreportcolors', binding: recolored } });
    assert.doesNotThrow(() => build(allowed));
    // Any other case may not.
    const other = syntheticDevqEvidence({ recolored: { caseId: 'device.xtwinops-20', binding: recolored } });
    assert.throws(() => build(other), /saw the palette or theme change/);
    // And the finish frame must carry the start binding again.
    assert.throws(() => build(syntheticDevqEvidence({ finishBinding: recolored })), /fixture was not restored/);
});

test('an unrestored fixture is refused before any conclusion', () => {
    const evidence = syntheticDevqEvidence();
    const last = evidence.frames[evidence.frames.length - 1];
    last.screen = { ...last.screen, lines: ['changed', ...last.screen.lines.slice(1)] };
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(evidence), /fixture was not restored/);
});
