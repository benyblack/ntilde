import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrClosureGradingReport, replaySgrClosurePinned, validateSgrClosureEvidence } from './sgr-closure-grading.mjs';
import { sgrClosureContracts, sgrClosureSuiteId, sgrClosureContractsVersion } from './sgr-closure-contracts.mjs';
import { sgrClosureRecordingContext } from './sgr-closure-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticSgrClosureEvidence, syntheticSgrClosureReads, syntheticSgrClosureScreens, styled, plainCell } from './fixtures/sgr-closure-evidence.mjs';

const runId = 'c'.repeat(32);
const BLINK = 'sgr.blink', OVERLINE = 'sgr.overline', COLOR = 'sgr.underline.color';
const clone = value => JSON.parse(JSON.stringify(value));
const evidenceWith = (caseId, edit) => syntheticSgrClosureEvidence(runId, 'f'.repeat(64), undefined, id => {
    const reads = syntheticSgrClosureReads(id);
    if (id === caseId && edit) edit(reads.groups);
    return reads;
});
const build = evidence => buildSgrClosureGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);

test('a complete run rederives all three cases; pinned and contract agree with no divergence', () => {
    const report = build(syntheticSgrClosureEvidence(runId));
    assert.equal(report.suiteId, sgrClosureSuiteId);
    assert.equal(report.contractsVersion, sgrClosureContractsVersion);
    assert.equal(report.snapshots.length, 3 + 4 + 5 + 2);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]),
        [[BLINK, 'supported', 'supported'], [OVERLINE, 'supported', 'supported'], [COLOR, 'supported', 'supported']]);
    for (const item of report.cases) assert.equal(item.divergence, undefined);
    assert.equal(report.cases[2].conclusion.layers.stored, 'supported');
});

test('sgr.underline.color is graded from its own reads and carries its own pinned text, not the sibling verdict', () => {
    const report = build(syntheticSgrClosureEvidence(runId));
    assert.equal(report.cases[2].pinned.assertions[0].expected, 'sgr.underline.color: X has explicit rgb(255,0,128) underline distinct from the reset/default B cell');
    // Break only THIS case's literal: the case grades inconclusive from its own reads (nothing is copied from another id).
    const broken = build(evidenceWith(COLOR, groups => { groups[0][2] = styled('X', [], { style: 1 }); }));
    assert.equal(broken.cases[2].conclusion.outcome, 'inconclusive');
    assert.equal(broken.cases[0].conclusion.outcome, 'supported');
    // A pinned result carrying the sibling id's expectation text is refused as a substituted verdict.
    const evidence = syntheticSgrClosureEvidence(runId);
    const swapped = clone(evidence);
    swapped.run.cases[2].pinned.assertions[0].expected = swapped.run.cases[2].pinned.assertions[0].expected.replace('sgr.underline.color', 'sgr.underline-color-rgb');
    assert.throws(() => build(swapped), /does not match its observed evidence/);
});

test('exposed-false overline and a missing blink are graded unsupported by the contract, inconclusive by the pinned probe', () => {
    for (const [caseId, index, kind] of [[BLINK, 0, 'blink'], [OVERLINE, 1, 'overline']]) {
        const report = build(evidenceWith(caseId, groups => {
            groups[1] = [plainCell('X')]; groups[2] = [plainCell('X'), plainCell('Y')];
            if (kind === 'overline') groups[4] = [styled('V', [], { style: 1 }), plainCell('U'), plainCell('T')];
        }));
        const item = report.cases[index];
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.conclusion.outcome, 'unsupported');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    }
});

test('invalid controls and wrong flags are inconclusive in both graders', () => {
    const report = build(evidenceWith(BLINK, groups => { groups[1] = [styled('X', ['blink', 'bold'])]; groups[2][0] = styled('X', ['blink', 'bold']); }));
    assert.equal(report.cases[0].pinned.observation.outcome, 'supported', 'the pinned probe cannot see unrelated drift');
    assert.equal(report.cases[0].conclusion.outcome, 'inconclusive');
    assert.deepEqual(report.cases[0].divergence, { pinnedOutcome: 'supported', contractOutcome: 'inconclusive', kind: 'contract-stricter-than-pinned' });
});

test('the pinned replay mirrors the live shapes over the observed fixture screen', () => {
    for (const contract of sgrClosureContracts) {
        const screens = syntheticSgrClosureScreens(contract.caseId);
        const replay = replaySgrClosurePinned(contract, screens);
        assert.equal(replay.observation.outcome, 'supported');
        const context = sgrClosureRecordingContext(contract, screens);
        assert.throws(() => context.getCell(0, 0), /before its fixture/);
        assert.throws(() => context.getText(), /must not read text/);
        assert.throws(() => sgrClosureRecordingContext(contract, screens).feed('wrong bytes'), /differs from the contract/);
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(typeof context.getCell(0, contract.kind === 'underline-color' ? 2 : 0).char, 'string');
    }
});

test('tampered pinned results, run logs, bytes and identities are refused', () => {
    const evidence = syntheticSgrClosureEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[1] = '\x1b[6mX'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].feeds[1] = '\x1b[4m\x1b[58;2;255;0;129mX'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.probeId = COLOR; })), /Missing pinned sgr.overline grading result/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = ['sgr.blink', 'sgr.overline', 'sgr.underline-color-rgb']; })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid SGR closure adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-underline-color-grading-v1'; })), /Invalid SGR closure adapter identity/);
    assert.throws(() => buildSgrClosureGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('stale or edited observations, DECSCNM change and incomplete restoration are refused', () => {
    const evidence = syntheticSgrClosureEvidence(runId);
    const stale = clone(evidence);
    stale.frames[3].screen.lines[0] = 'XX';
    assert.throws(() => build(stale), /Changed SGR closure snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete SGR closure snapshot sequence/);
    const reverse = clone(evidence);
    reverse.frames[4].screen.colorBinding.reverseVideo = true;
    reverse.frames[4].sha256 = snapshotHash(reverse.frames[4].screen);
    assert.throws(() => build(reverse), /palette or theme change/);
    const restored = clone(evidence);
    const last = restored.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(restored), /was not restored/);
});

test('evidence validation entry and contract-version pinning', () => {
    const evidence = syntheticSgrClosureEvidence(runId);
    assert.doesNotThrow(() => validateSgrClosureEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildSgrClosureGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown SGR-closure contracts revision 2/);
});
