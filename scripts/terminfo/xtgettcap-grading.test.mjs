import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildXtgettcapGradingReport, validateXtgettcapEvidence } from './xtgettcap-grading.mjs';
import { xtgettcapContracts, replayXtgettcapPinned } from './xtgettcap-contracts.mjs';
import { syntheticXtgettcapEvidence, syntheticXtgettcapExchanges } from './fixtures/xtgettcap-evidence.mjs';
import { snapshotHash } from './agent-observer.mjs';

const contract = xtgettcapContracts[0];
const build = evidence => buildXtgettcapGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** The index of the frame, and of the run-log read, of a role. */
const frameOf = role => 2 + contract.roles.indexOf(role);
const readOf = role => contract.roles.indexOf(role);
const da = '\x1b[?62;4;22c';

test('a complete synthetic run rederives the preserved pinned verdict and the supported effect contract', () => {
    const report = build(syntheticXtgettcapEvidence());
    assert.equal(report.cases.length, 1);
    const [item] = report.cases;
    assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
    assert.equal(item.pinned.termless.observation.reason, 'insufficient-evidence');
    assert.equal(item.pinned.termless.observation.note, 'Requested setting or name refused; other settings or names unmeasured');
    assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive', 'the terminal path is equally inconclusive on the refusal');
    assert.equal(item.pinned.terminal.response, syntheticXtgettcapExchanges.pinned.join('') + da, 'the terminal replay sees the raw bytes with the barrier');
    assert.equal(item.effectContract.id, 'ntilde-xtgettcap-capability-report-effect-v1');
    assert.equal(item.effectContract.outcome, 'supported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(item.conclusion.outcome, 'inconclusive', 'the published conclusion is the pinned verdict, preserved by design');
    assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.deepEqual(item.reads[readOf('list-order')].replies, syntheticXtgettcapExchanges['list-order']);
    assert.equal(item.reads[readOf('initial')].replies, undefined, 'a cells read carries no replies');
    assert.equal(Object.values(item.observed.requests).every(request => request.matches && request.unchanged), true);
    assert.equal(report.snapshots.length, 3 + contract.roles.length);
});

test('the effect contract grades a substituted value as unsupported and refuses an invented pinned success', () => {
    // A substituted value: Co answers 88 instead of 256.
    const wrongValue = syntheticXtgettcapEvidence('a'.repeat(32), 'f'.repeat(64), undefined,
        (role, read) => (role === 'numeric-co' ? { ...read, replies: ['\x1bP1+r436f=3838' + '\x1b\\'] } : null));
    let [item] = build(wrongValue).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /numeric-co reply differs from the reply oracle/);
    assert.equal(item.conclusion.outcome, 'inconclusive', 'the pinned verdict is still the published conclusion');
    // An invented success for the deliberately refused TN name contradicts the documented refusal policy this suite grades under:
    // contracts v1 refuses to publish it (a changed policy needs a new versioned contract, not a silent regrade).
    const invented = syntheticXtgettcapEvidence('a'.repeat(32), 'f'.repeat(64), undefined,
        (role, read) => (role === 'pinned' ? { ...read, replies: ['\x1bP1+r544e=323536' + '\x1b\\'] } : null));
    assert.throws(() => build(invented), /must be inconclusive by design/);
    assert.equal(replayXtgettcapPinned('\x1bP1+r544e=323536' + '\x1b\\').observation.outcome, 'supported',
        'the pinned criterion itself would call the value reply supported; preserving that semantics is what makes the refusal honest');
});

test('the effect contract grades missing, partial, extra and reordered replies as unsupported', () => {
    const silent = syntheticXtgettcapEvidence('a'.repeat(32), 'f'.repeat(64), undefined,
        (role, read) => (role === 'numeric-colors' ? { ...read, replies: [] } : null));
    let [item] = build(silent).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /numeric-colors request was not answered/);
    const partial = syntheticXtgettcapEvidence('a'.repeat(32), 'f'.repeat(64), undefined,
        (role, read) => (role === 'list-order' ? { ...read, replies: read.replies.slice(0, 3) } : null));
    [item] = build(partial).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /list-order reply count differs/);
    const extra = syntheticXtgettcapEvidence('a'.repeat(32), 'f'.repeat(64), undefined,
        (role, read) => (role === 'unavailable-kcuu1' ? { ...read, replies: [...read.replies, '\x1bP1+r5463' + '\x1b\\'] } : null));
    [item] = build(extra).cases;
    assert.match(item.effectContract.outcome === 'unsupported' && item.effectContract.note, /reply count differs/, 'an extra reply for a refused name is a mismatch');
    const reordered = syntheticXtgettcapEvidence('a'.repeat(32), 'f'.repeat(64), undefined,
        (role, read) => (role === 'list-witness' ? { ...read, replies: [...read.replies].reverse() } : null));
    [item] = build(reordered).cases;
    assert.match(item.effectContract.outcome === 'unsupported' && item.effectContract.note, /reply differs/, 'reversed per-name outcomes are a mismatch');
});

test('the effect contract grades a state-changing request and an unrelated state drift as unsupported', () => {
    const drift = syntheticXtgettcapEvidence('a'.repeat(32), 'f'.repeat(64), undefined, (role, read) => {
        if (role !== 'recovery-co') return null;
        const screen = structuredClone(read.screen);
        screen.lines[5] = 'stale';
        screen.cursorRow = 5;
        screen.cursorCol = 5;
        return { ...read, screen };
    });
    const [item] = build(drift).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.match(item.effectContract.note, /recovery-co request changed the screen/);
    assert.equal(item.observed.requests['recovery-co'].unchanged, false);
});

for (const [label, mutate, pattern] of [
    ['a journaled reply list that differs from the worker record', e => { e.frames[frameOf('pinned')].report.replies = []; }, /replies differ from their read frame/],
    ['a worker record that differs from the journal', e => { e.run.cases[0].reads[readOf('flag-tc')].report.replies = ['\x1bP0+r\x1b\\']; }, /replies differ from their read frame/],
    ['a report on a cells read', e => { e.frames[frameOf('initial')].report = { barrier: e.frames[frameOf('initial')].barrier, replies: [] }; }, /report on a cells read/],
    ['a report read without its report', e => { delete e.frames[frameOf('echo-upper')].report; delete e.run.cases[0].reads[readOf('echo-upper')].report; }, /XTGETTCAP/],
    ['a report on the start frame', e => { e.frames[0].report = { barrier: da, replies: [] }; }, /Unexpected XTGETTCAP report frame/],
    ['a pinned termless result that differs from its replies', e => { e.run.cases[0].pinned.termless.observation = { outcome: 'supported', evidence: 'query' }; }, /does not match its observed evidence/],
    ['a pinned terminal result that differs from the journaled bytes', e => { e.run.cases[0].pinned.terminal.response = ''; }, /does not match its observed evidence/],
    ['a pinned result with another evidence kind', e => { e.run.cases[0].pinned.termless.observation.evidence = 'parser-state'; }, /Missing pinned/],
    ['a pinned result that claims support', e => { e.run.cases[0].pinned.termless.pass = true; }, /does not match its observed evidence/],
    ['a changed adapter identity', e => { e.adapter.id = 'other'; }, /adapter identity/],
    ['a changed predecessor', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-lr-margins-grading-v1'; }, /adapter identity/],
    ['a truncated journal', e => { e.frames.pop(); }, /Incomplete XTGETTCAP snapshot sequence/],
    ['changed feeds', e => { e.run.cases[0].feeds[1] = '\x1bP+q544e;436f\x1b\\'; }, /feeds do not match/],
    ['a reordered case list', e => { e.run.probeIds = []; }, /does not cover the contracted cases/],
    ['a stale reply barrier', e => { e.frames[frameOf('flag-rgb')].report.barrier = '\x1b[?1c'; }, /replies differ from their read frame/],
    ['a report not ordered by its own barrier', e => { e.frames[frameOf('flag-rgb')].barrier = '\x1b[?1c'; }, /barrier differs from its read frame/],
]) {
    test('the report builder refuses ' + label, () => {
        const evidence = syntheticXtgettcapEvidence();
        mutate(evidence);
        assert.throws(() => build(evidence), pattern);
    });
}

test('the evidence check alone binds the run identity and the shared theme', () => {
    const evidence = syntheticXtgettcapEvidence();
    assert.doesNotThrow(() => validateXtgettcapEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => validateXtgettcapEvidence(evidence.frames, evidence.run, 'b'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('an unrestored fixture is refused before any conclusion', () => {
    const evidence = syntheticXtgettcapEvidence();
    const last = evidence.frames[evidence.frames.length - 1];
    last.screen = { ...last.screen, lines: ['changed', ...last.screen.lines.slice(1)] };
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(evidence), /fixture was not restored/);
});
