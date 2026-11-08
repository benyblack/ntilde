import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrAttrsGradingReport, replaySgrAttrsPinned } from './sgr-attrs-grading.mjs';
import { sgrAttrsContracts, sgrAttrsSuiteId, sgrAttrsFeeds } from './sgr-attrs-contracts.mjs';
import { sgrAttrsRecordingContext } from './sgr-attrs-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticSgrAttrsEvidence, syntheticSgrAttrsScreens, syntheticSgrAttrsScreen, syntheticSgrAttrsReads,
    syntheticColorBinding, plainCell, styledCell } from './fixtures/sgr-attrs-evidence.mjs';

const rebuild = e => buildSgrAttrsGradingReport(e.frames, e.run, e.runId, e.adapter);
/** Fresh synthetic evidence plus its parent-derived report. */
const report = (readsOf, binding) => {
    const evidence = syntheticSgrAttrsEvidence('a'.repeat(32), 'f'.repeat(64), binding, readsOf);
    return { evidence, report: rebuild(evidence) };
};
/** Re-hash a mutated frame so only the intended defect is under test. */
const rehash = (evidence, index, mutate) => {
    const screen = structuredClone(evidence.frames[index].screen);
    mutate(screen);
    evidence.frames[index] = { ...evidence.frames[index], screen, sha256: snapshotHash(screen) };
};

test('rebuilds a complete graded report with five supported conclusions', () => {
    const { report: value } = report();
    assert.equal(value.suiteId, sgrAttrsSuiteId);
    assert.equal(value.contractsVersion, 1);
    assert.equal(value.snapshots.length, 3 + 5 * 3);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        sgrAttrsContracts.map(contract => [contract.caseId, 'supported', 'supported']));
    for (const item of value.cases) {
        const contract = sgrAttrsContracts.find(entry => entry.caseId === item.caseId);
        assert.equal(item.conclusion.evidence, 'owned-stored-cells');
        assert.equal(item.conclusion.scope, contract.appearanceScope);
        assert.match(item.conclusion.scope, /not claimed/);
        assert.deepEqual(item.feeds, sgrAttrsFeeds(contract));
        assert.deepEqual(item.pinnedFeeds, [...contract.pinnedFeeds]);
        assert.equal(item.divergence, undefined);
        assert.equal(item.colorBinding.reverseVideo, false);
        assert.match(item.colorBinding.bindingSha256, /^[a-f\d]{64}$/);
        assert.equal(item.pinned.assertions[0].expected, `${item.caseId}: attribute applied to the rendered X cell`);
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
    }
});

test('the pinned response embeds the stored projection verbatim, including the underline style', () => {
    const { report: value } = report();
    const underline = JSON.parse(value.cases[0].pinned.response);
    assert.equal(underline.char, 'X');
    assert.equal(underline.underline, 'single');
    assert.equal(underline.underlineStyle, 1);
    assert.equal(underline.dim, false);
    assert.equal(JSON.parse(value.cases[2].pinned.response).dim, true);
    assert.equal(JSON.parse(value.cases[2].pinned.response).bold, false);
});

test('a measured mismatch is unsupported only beside the pinned inconclusive, and is recorded', () => {
    const { report: value } = report(caseId => {
        const reads = syntheticSgrAttrsReads(caseId);
        if (caseId === 'sgr.strikethrough') { reads.target = [plainCell('X')]; reads.trailing = [plainCell('X'), plainCell('Y')]; }
        return reads;
    });
    const strike = value.cases.find(item => item.caseId === 'sgr.strikethrough');
    assert.equal(strike.pinned.observation.outcome, 'inconclusive');
    assert.equal(strike.pinned.note, 'Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review');
    assert.equal(strike.conclusion.outcome, 'unsupported');
    assert.deepEqual(strike.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    assert.equal(value.cases.filter(item => item.conclusion.outcome === 'supported').length, 4);
});

test('a wrong underline style is the pinned supported but the contract unsupported, with both recorded', () => {
    const { report: value } = report(caseId => {
        const reads = syntheticSgrAttrsReads(caseId);
        if (caseId === 'sgr.underline.single') {
            reads.target = [styledCell('X', ['underline'], 3)];
            reads.trailing = [reads.target[0], plainCell('Y')];
        }
        return reads;
    });
    const underline = value.cases[0];
    assert.equal(underline.pinned.observation.outcome, 'supported');
    assert.equal(underline.conclusion.outcome, 'unsupported');
    assert.deepEqual(underline.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
});

test('invalid controls prevent a conclusive grade even when the flag is set', () => {
    for (const [name, readsOf] of [
        ['initially active baseline', caseId => { const reads = syntheticSgrAttrsReads(caseId); reads.baseline = [styledCell('C', ['italic'])]; return reads; }],
        ['unrelated drift', caseId => { const reads = syntheticSgrAttrsReads(caseId); const attrs = [...new Set([...reads.target[0].attrs, 'blink'])];
            reads.target = [styledCell('X', attrs)]; reads.trailing = [reads.target[0], ...reads.trailing.slice(1)]; return reads; }],
        ['missing restoration cell', caseId => { const reads = syntheticSgrAttrsReads(caseId); reads.trailing = [reads.trailing[0], plainCell('?'), plainCell('?')]; return reads; }],
    ]) {
        const { report: value } = report(readsOf);
        assert.ok(value.cases.every(item => item.conclusion.outcome === 'inconclusive'), name);
        assert.ok(value.cases.every(item => item.conclusion.note), name);
    }
});

test('global reverse video voids the conclusions without touching the per-cell inverse flag', () => {
    const { report: value } = report(null, { ...syntheticColorBinding(), reverseVideo: true });
    assert.ok(value.cases.every(item => item.conclusion.outcome === 'inconclusive'));
    assert.match(value.cases[1].conclusion.note, /DECSCNM/);
});

test('rejects wrong input variants, missing captures and reordered cases', () => {
    const base = report();
    const wrongBytes = structuredClone(base.evidence);
    wrongBytes.run.cases[0].feeds[4] = '\x1b[4:3mX';
    assert.throws(() => rebuild(wrongBytes), /do not match the pinned contract/);

    const substituted = structuredClone(base.evidence);
    substituted.run.cases[3].feeds[4] = '\x1b[5mX';
    assert.throws(() => rebuild(substituted), /do not match the pinned contract/);

    const missingRead = structuredClone(base.evidence);
    missingRead.frames.splice(3, 1);
    assert.throws(() => rebuild(missingRead), /Incomplete SGR-attributes snapshot sequence/);

    const missingCapture = structuredClone(base.evidence);
    missingCapture.run.cases[0].reads.pop();
    assert.throws(() => rebuild(missingCapture), /Invalid sgr.underline.single graded capture record/);

    const reordered = structuredClone(base.evidence);
    [reordered.run.cases[0], reordered.run.cases[1]] = [reordered.run.cases[1], reordered.run.cases[0]];
    assert.throws(() => rebuild(reordered), /case order differs/);
});

test('rejects tampered observations, hashes and barriers', () => {
    const base = report();
    const tamperedFlags = structuredClone(base.evidence);
    tamperedFlags.frames[3].screen.attributeRuns[0].flags |= 0x0010;
    assert.throws(() => rebuild(tamperedFlags), /Changed SGR-attributes snapshot evidence|Invalid|coheren/i);

    const tamperedHash = structuredClone(base.evidence);
    tamperedHash.frames[3].sha256 = '0'.repeat(64);
    assert.throws(() => rebuild(tamperedHash), /Changed SGR-attributes snapshot evidence/);

    // Flipping the flag consistently in all three projections still cannot pass: the pinned
    // response retained in the run log no longer matches the observed screen.
    const consistent = structuredClone(base.evidence);
    rehash(consistent, 3, screen => {
        screen.attributeRuns = syntheticSgrAttrsScreen([styledCell('X', ['strikethrough'])]).attributeRuns;
        screen.cellDetails = syntheticSgrAttrsScreen([styledCell('X', ['strikethrough'])]).cellDetails;
    });
    assert.throws(() => rebuild(consistent), /does not match its observed evidence|disagree|differs/);

    const missingBarrier = structuredClone(base.evidence);
    missingBarrier.frames[3].barrier = null;
    assert.throws(() => rebuild(missingBarrier), /Missing SGR-attributes ordering barrier evidence/);

    const barrierDrift = structuredClone(base.evidence);
    barrierDrift.frames[4].barrier = '\x1b[?62;4;23c';
    assert.throws(() => rebuild(barrierDrift), /barrier differs from its read frame/);

    const cursorDrift = structuredClone(base.evidence);
    cursorDrift.run.cases[1].reads[0].cursor = [4, 4];
    assert.throws(() => rebuild(cursorDrift), /cursor differs from its read frame/);
});

test('rejects stale captures: a read frame replaced by an earlier frame cannot grade', () => {
    const base = report();
    const stale = structuredClone(base.evidence);
    // The target read replays the baseline capture (attribute not yet applied).
    stale.frames[3] = { ...stale.frames[2], index: 3, barrier: stale.frames[3].barrier };
    assert.throws(() => rebuild(stale), /does not match its observed evidence|disagree|differs/);
});

test('rejects mismatched run identities and adapter bindings', () => {
    const base = report();
    assert.throws(() => buildSgrAttrsGradingReport(base.evidence.frames, base.evidence.run, 'b'.repeat(32), base.evidence.adapter),
        /not bound to this run/);

    const foreignAdapter = structuredClone(base.evidence);
    foreignAdapter.adapter.id = 'some-other-adapter-v9';
    assert.throws(() => rebuild(foreignAdapter), /Invalid SGR-attributes adapter identity/);

    const wrongLayer = structuredClone(base.evidence);
    wrongLayer.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-colors-grading-v1';
    assert.throws(() => rebuild(wrongLayer), /Invalid SGR-attributes adapter identity/);

    const wrongProbeList = structuredClone(base.evidence);
    wrongProbeList.run.probeIds = ['sgr.bold'];
    assert.throws(() => rebuild(wrongProbeList), /does not cover the contracted cases/);

    const wrongProbe = structuredClone(base.evidence);
    wrongProbe.run.cases[2].pinned.probeId = 'sgr.bold';
    assert.throws(() => rebuild(wrongProbe), /Missing pinned sgr.faint grading result/);
});

test('missing stored evidence refuses instead of grading', () => {
    const base = report();
    for (const field of ['resolvedColors', 'cellDetails', 'attributeRuns']) {
        const missing = structuredClone(base.evidence);
        delete missing.frames[3].screen[field];
        missing.frames[3].sha256 = snapshotHash(missing.frames[3].screen);
        assert.throws(() => rebuild(missing), /./, field);
    }
});

test('a palette or theme change during observation voids the whole run', () => {
    const base = report();
    const changed = structuredClone(base.evidence);
    const later = syntheticColorBinding({ foreground: 0xff102030 });
    changed.frames[4] = { ...changed.frames[4], screen: { ...changed.frames[4].screen, colorBinding: later },
        sha256: snapshotHash({ ...changed.frames[4].screen, colorBinding: later }) };
    assert.throws(() => rebuild(changed), /palette or theme change|Invalid resolved-color|does not cohere/i);
});

test('incomplete restoration prevents any conclusion', () => {
    const base = report();
    const unrestored = structuredClone(base.evidence);
    const last = unrestored.frames.length - 1;
    rehash(unrestored, last, screen => { screen.cursorRow = 2; });
    assert.throws(() => rebuild(unrestored), /SGR-attributes fixture was not restored/);
});

test('fabricated pinned results are refused', () => {
    const base = report();
    for (const [name, mutate] of [
        ['fabricated assertion detail', pinned => { pinned.assertions[0].expected = 'made up criterion'; }],
        ['dropped assertions', pinned => { delete pinned.assertions; }],
        ['wrong pass flag', pinned => { pinned.pass = false; }],
        ['wrong response', pinned => { pinned.response = '{"fabricated":true}'; }],
        ['invented inconclusive verdict', pinned => {
            pinned.pass = false;
            pinned.note = 'made up';
            pinned.observation = { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'made up' };
        }],
        ['invented unsupported verdict', pinned => {
            pinned.pass = false;
            pinned.observation = { outcome: 'unsupported', evidence: 'parser-state' };
        }],
    ]) {
        const tampered = structuredClone(base.evidence);
        mutate(tampered.run.cases[0].pinned);
        assert.throws(() => rebuild(tampered), /does not match its observed evidence/, name);
    }
});

test('the replay module must reproduce the live pinned grading exactly', () => {
    const base = report();
    const faithful = { ntildeRunSgrAttrsTermless: (probeId, ctx) => {
        const contract = sgrAttrsContracts.find(item => item.caseId === probeId);
        const live = base.report.cases.find(item => item.caseId === probeId).pinned;
        ctx.feed(contract.pinnedFeeds[0]);
        ctx.getCell(0, 0);
        return { probeId, ...live };
    } };
    const replayed = buildSgrAttrsGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: faithful });
    assert.equal(replayed.cases[0].conclusion.outcome, 'supported');

    const unfaithful = { ntildeRunSgrAttrsTermless: () => ({ pass: true, response: 'x',
        observation: { outcome: 'supported', evidence: 'parser-state' }, assertions: [{ kind: 'positive', expected: 'x', observed: 'x' }] }) };
    assert.throws(() => buildSgrAttrsGradingReport(base.evidence.frames, base.evidence.run, base.evidence.runId, base.evidence.adapter, { replayModule: unfaithful }),
        /replay does not reproduce the live grading/);
});

test('the recording context serves only the pinned fixture screen and refuses stray feeds', () => {
    const contract = sgrAttrsContracts.find(item => item.caseId === 'sgr.faint');
    const screens = syntheticSgrAttrsScreens('sgr.faint');
    const ctx = sgrAttrsRecordingContext(contract, screens);
    assert.throws(() => ctx.getCell(0, 0), /read before its fixture/);
    ctx.feed('\x1b[2mX');
    assert.equal(ctx.getCell(0, 0).dim, true);
    assert.throws(() => ctx.feed('\x1b[2mX'), /feed 1 differs/);
    assert.throws(() => sgrAttrsRecordingContext(contract, screens).feed('\x1b[1mX'), /feed 0 differs/);
    assert.throws(() => ctx.getText(), /must not read text/);
    const replayed = replaySgrAttrsPinned(contract, screens);
    assert.equal(replayed.pass, true);
    assert.equal(replayed.observation.outcome, 'supported');
});
