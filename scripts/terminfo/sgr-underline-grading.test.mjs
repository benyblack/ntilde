import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildSgrUnderlineGradingReport, replaySgrUnderlinePinned, validateSgrUnderlineEvidence } from './sgr-underline-grading.mjs';
import { sgrUnderlineContracts, sgrUnderlineSuiteId, sgrUnderlineFeeds, sgrUnderlineLadder } from './sgr-underline-contracts.mjs';
import { sgrUnderlineRecordingContext } from './sgr-underline-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticSgrUnderlineEvidence, syntheticSgrUnderlineReads, syntheticSgrUnderlineScreens, styled, colorCell, plainCell,
    paletteBinding } from './fixtures/sgr-underline-evidence.mjs';
import { paletteChannel } from './fixtures/sgr-palette-evidence.mjs';

const rebuild = (e, options) => buildSgrUnderlineGradingReport(e.frames, e.run, e.runId, e.adapter, options);
const report = (readsOf, binding) => {
    const evidence = syntheticSgrUnderlineEvidence('a'.repeat(32), 'f'.repeat(64), binding, readsOf);
    return { evidence, report: rebuild(evidence) };
};
const rehash = (evidence, index, mutate) => {
    const screen = structuredClone(evidence.frames[index].screen);
    mutate(screen);
    evidence.frames[index] = { ...evidence.frames[index], screen, sha256: snapshotHash(screen) };
};
/** Read frame `read` (0 baseline, 1 target, 2 trailing, 3 ladder) of case `position`. */
const frameOf = (position, read) => 2 + position * 4 + read;
const reads = (caseId, edit) => id => { const value = syntheticSgrUnderlineReads(id); if (id === caseId && edit) edit(value); return value; };

test('rebuilds a complete graded report with four supported conclusions on exact stored style', () => {
    const { report: value } = report();
    assert.equal(value.suiteId, sgrUnderlineSuiteId);
    assert.equal(value.contractsVersion, 1);
    assert.equal(value.snapshots.length, 3 + 4 * 4);
    assert.match(value.scope, /Stored state only: stroke appearance is not claimed; no stock grades/);
    assert.deepEqual(value.cases.map(item => [item.caseId, item.conclusion.outcome, item.pinned.observation.outcome]),
        sgrUnderlineContracts.map(contract => [contract.caseId, 'supported', 'supported']));
    for (const item of value.cases) {
        const contract = sgrUnderlineContracts.find(entry => entry.caseId === item.caseId);
        assert.equal(item.conclusion.scope, contract.appearanceScope);
        assert.match(item.conclusion.scope, /not claimed/);
        assert.deepEqual(item.feeds, sgrUnderlineFeeds(contract));
        assert.deepEqual(item.pinnedFeeds, [...contract.pinnedFeeds]);
        assert.equal(item.divergence, undefined);
        assert.equal(item.colorBinding.reverseVideo, false);
        assert.equal(item.pinned.assertions[0].expected, `${item.caseId}: attribute applied to the rendered X cell`);
        assert.ok(item.barriers.every(barrier => /^\x1b\[\?[\d;]+c$/.test(barrier)));
        assert.equal(item.observed.target.underline, contract.style);
        assert.equal(item.observed.target.underlineStyle, contract.styleCode);
        // The labeled ladder is retained with every spelling, graded or not.
        assert.deepEqual(item.observed.controlLadder.map(cell => cell.spelling),
            [...sgrUnderlineLadder.map(entry => entry.spelling), 'neutral (after SGR 0)']);
    }
});

test('the pinned response embeds the stored projection, exact style name and code included', () => {
    const { report: value } = report();
    for (const [position, contract] of sgrUnderlineContracts.entries()) {
        const response = JSON.parse(value.cases[position].pinned.response);
        assert.equal(response.char, 'X');
        assert.equal(response.underline, contract.style);
        assert.equal(response.underlineStyle, contract.styleCode);
        assert.equal(response.bold, false);
    }
});

test('a measured wrong-style substitution is unsupported beside the pinned inconclusive, and is recorded', () => {
    const { report: value } = report(reads('sgr.underline.curly', edit => {
        edit.target = [styled('X', 'single')]; edit.trailing = [styled('X', 'single'), plainCell('Y')];
    }));
    const curly = value.cases[1];
    assert.equal(curly.pinned.observation.outcome, 'inconclusive');
    assert.equal(curly.conclusion.outcome, 'unsupported');
    assert.equal(curly.conclusion.note, 'SGR 4:3 stored the single underline style instead of curly');
    assert.deepEqual(curly.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
    assert.deepEqual(value.cases.filter(item => item.divergence).map(item => item.caseId), ['sgr.underline.curly']);
});

test('invalid controls make the contract more cautious than a pinned supported, recorded as a divergence', () => {
    const { report: value } = report(reads('sgr.underline.dotted', edit => { edit.target = [styled('X', 'dotted', { attrs: ['bold'] })]; edit.trailing = [edit.target[0], plainCell('Y')]; }));
    const dotted = value.cases[2];
    assert.equal(dotted.pinned.observation.outcome, 'supported');
    assert.equal(dotted.conclusion.outcome, 'inconclusive');
    assert.deepEqual(dotted.divergence, { pinnedOutcome: 'supported', contractOutcome: 'inconclusive', kind: 'contract-stricter-than-pinned' });
    // Missing restoration is the same: never graded unsupported or supported.
    const restored = report(reads('sgr.underline.dashed', edit => { edit.trailing = [edit.target[0], styled('Y', 'dashed')]; })).report.cases[3];
    assert.equal(restored.conclusion.outcome, 'inconclusive');
    assert.equal(restored.divergence.kind, 'contract-stricter-than-pinned');
});

test('the contract is never more permissive than the pinned probe, even when its own grader is forced to agree', () => {
    // A pinned inconclusive can never be overridden into supported by the contract grader.
    const evidence = syntheticSgrUnderlineEvidence('a'.repeat(32), 'f'.repeat(64), undefined, reads('sgr.underline.double', edit => {
        edit.target = [styled('X', 'dashed')]; edit.trailing = [styled('X', 'dashed'), plainCell('Y')];
    }));
    assert.equal(rebuild(evidence).cases[0].conclusion.outcome, 'unsupported');
    const screens = syntheticSgrUnderlineScreens('sgr.underline.double');
    assert.equal(replaySgrUnderlinePinned(sgrUnderlineContracts[0], screens).observation.outcome, 'supported');
    const wrong = syntheticSgrUnderlineScreens('sgr.underline.double', undefined, reads('sgr.underline.double', edit => { edit.target = [styled('X', 'single')]; }));
    assert.equal(replaySgrUnderlinePinned(sgrUnderlineContracts[0], wrong).observation.outcome, 'inconclusive');
});

test('a generic-flag-only response is never graded supported for any of the four styles', () => {
    for (const [position, contract] of sgrUnderlineContracts.entries()) {
        const { report: value } = report(reads(contract.caseId, edit => { edit.target = [styled('X', 'single')]; edit.trailing = [styled('X', 'single'), plainCell('Y')]; }));
        assert.equal(value.cases[position].conclusion.outcome, 'unsupported', contract.caseId);
        assert.equal(value.cases[position].pinned.observation.outcome, 'inconclusive', contract.caseId);
    }
});

test('DECSCNM, a dirty baseline or a collapsed ladder keeps the verdict inconclusive and never unsupported', () => {
    const reversed = report(null, { ...paletteBinding(), reverseVideo: true }).report;
    assert.ok(reversed.cases.every(item => item.conclusion.outcome === 'inconclusive'));
    assert.ok(reversed.cases.every(item => item.colorBinding.reverseVideo === true));
    const dirty = report(reads('sgr.underline.double', edit => { edit.baseline = [colorCell('C', { fg: paletteChannel(1) })]; })).report;
    assert.equal(dirty.cases[0].conclusion.outcome, 'inconclusive');
    const collapsed = report(reads('sgr.underline.double', edit => {
        edit.target = [styled('X', 'single')]; edit.trailing = [styled('X', 'single'), plainCell('Y')];
        edit.ladder = [...sgrUnderlineLadder.map(entry => styled(entry.char, 'single')), plainCell('N')];
    })).report;
    assert.equal(collapsed.cases[0].conclusion.outcome, 'inconclusive');
    assert.match(collapsed.cases[0].conclusion.note, /collapsed/);
});

test('every report refuses tampering with a frame, even a re-hashed one that changes the stored style', () => {
    const { evidence } = report();
    const tampered = structuredClone(evidence);
    tampered.frames[frameOf(0, 1)] = { ...tampered.frames[frameOf(0, 1)], screen: { ...tampered.frames[frameOf(0, 1)].screen, cursorCol: 3 } };
    assert.throws(() => rebuild(tampered), /Changed SGR-underline snapshot evidence/);
    // Re-hashed: the journal is self-consistent but the recorded pinned result no longer matches the observed cells.
    const forged = structuredClone(evidence);
    rehash(forged, frameOf(0, 1), screen => {
        screen.cellDetails[0].underlineStyle = 3; // double -> curly in the stored detail only
    });
    assert.throws(() => rebuild(forged), /Pinned sgr\.underline\.double grading does not match|Contradictory|Invalid/);
    const downgraded = structuredClone(evidence);
    rehash(downgraded, frameOf(1, 1), screen => { screen.cellDetails[0].underlineStyle = 1; });
    assert.throws(() => rebuild(downgraded), /Pinned sgr\.underline\.curly grading does not match|Contradictory|Invalid/);
});

test('a forged pinned verdict, response, note or assertion is refused', () => {
    for (const forge of [
        pinned => { pinned.observation = { ...pinned.observation, outcome: 'unsupported' }; },
        pinned => { pinned.pass = false; },
        pinned => { pinned.response = pinned.response.replace('"X"', '"Y"'); },
        pinned => { pinned.note = 'forged'; },
        pinned => { pinned.assertions = [{ kind: 'negative', expected: 'x', observed: 'y' }]; },
    ]) {
        const { evidence } = report();
        forge(evidence.run.cases[1].pinned);
        assert.throws(() => rebuild(evidence), /Pinned sgr\.underline\.curly grading does not match/);
    }
});

test('the recorded plan, barriers, cursors and case coverage must match the contract exactly', () => {
    const mutate = (edit, pattern) => { const { evidence } = report(); edit(evidence); assert.throws(() => rebuild(evidence), pattern); };
    mutate(e => { e.run.cases[0].feeds[1] = 'D'; }, /do not match the pinned contract/);
    mutate(e => { e.run.cases[2].feeds = e.run.cases[2].feeds.slice(1); }, /do not match the pinned contract/);
    mutate(e => { e.run.cases[0].feeds[4] = '\x1b[4:2mX'; }, /do not match the pinned contract/); // SGR 4:2 swapped in for SGR 21
    mutate(e => { e.run.cases[0].feeds[4] = '\x1b[4mX'; }, /do not match the pinned contract/);   // generic SGR 4 swapped in
    mutate(e => { e.run.cases[1].barriers.pop(); }, /Invalid sgr\.underline\.curly graded capture record/);
    mutate(e => { e.run.cases[1].barriers[0] = 'x'; }, /Invalid sgr\.underline\.curly graded capture record/);
    mutate(e => { e.run.cases[0].reads[0].cursor = [3, 3]; }, /cursor differs from its read frame/);
    mutate(e => { e.run.cases[0].reads[2].kind = 'text'; }, /Wrong sgr\.underline\.double read kind/);
    mutate(e => { e.run.cases.pop(); }, /does not cover the contracted cases/);
    mutate(e => { e.run.cases.reverse(); }, /case order differs|do not match/);
    mutate(e => { e.run.probeIds.pop(); }, /does not cover the contracted cases/);
});

test('adapter identity, run binding, restoration and theme stability are enforced', () => {
    const mutate = (edit, pattern) => { const { evidence } = report(); edit(evidence); assert.throws(() => rebuild(evidence), pattern); };
    mutate(e => { e.adapter.id = 'other'; }, /Invalid SGR-underline adapter identity/);
    mutate(e => { e.run.adapterId = 'other'; }, /Invalid SGR-underline adapter identity/);
    mutate(e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-palette-grading-v1'; }, /Invalid SGR-underline adapter identity/);
    mutate(e => { e.adapter.bundleSha256 = 'xyz'; }, /Invalid SGR-underline adapter identity/);
    assert.throws(() => buildSgrUnderlineGradingReport(report().evidence.frames, report().evidence.run, 'b'.repeat(32), report().evidence.adapter), /not bound to this run/);
    mutate(e => rehash(e, 1, screen => { screen.lines[0] = 'NTILDE_SU_' + 'c'.repeat(32); }), /not bound to this run/);
    mutate(e => rehash(e, e.frames.length - 1, screen => { screen.lines[0] = 'left over'; }), /not restored/);
    mutate(e => rehash(e, frameOf(3, 3), screen => { screen.colorBinding = { ...screen.colorBinding, themeName: 'Changed' }; }), /palette or theme change/);
    mutate(e => e.frames.pop(), /Incomplete SGR-underline snapshot sequence/);
    assert.throws(() => validateSgrUnderlineEvidence([], {}, 'a'.repeat(32), {}), /Incomplete/);
});

test('the parent-side module replay must reproduce the live pinned result', () => {
    const { evidence } = report();
    // Direct recording-context use: the pinned feed must be exact and the read must follow it.
    const contract = sgrUnderlineContracts[0];
    const screens = syntheticSgrUnderlineScreens(contract.caseId);
    const context = sgrUnderlineRecordingContext(contract, screens);
    assert.throws(() => context.getCell(0, 0), /read before its fixture/);
    assert.throws(() => context.feed('\x1b[4:2mX'), /differs from the contract/);
    context.feed(contract.pinnedFeeds[0]);
    assert.equal(context.getCell(0, 0).underline, 'double');
    assert.throws(() => context.getText(), /must not read text/);
    assert.throws(() => context.feed(contract.pinnedFeeds[0]), /differs from the contract/);
    const lying = { ntildeRunSgrUnderlineTermless: (probeId, ctx) => {
        ctx.feed(sgrUnderlineContracts.find(item => item.caseId === probeId).pinnedFeeds[0]);
        return { probeId, pass: false, response: 'x', observation: { outcome: 'inconclusive', evidence: 'parser-state' }, assertions: [] };
    } };
    assert.throws(() => rebuild(evidence, { replayModule: lying }), /replay does not reproduce the live grading/);
    const honest = { ntildeRunSgrUnderlineTermless: (probeId, ctx) => {
        const item = sgrUnderlineContracts.find(entry => entry.caseId === probeId);
        ctx.feed(item.pinnedFeeds[0]);
        const cell = ctx.getCell(0, 0);
        const response = JSON.stringify(cell);
        const measured = cell.char === 'X' && item.pinnedCheck(cell) === true;
        return measured
            ? { probeId, pass: true, response, observation: { outcome: 'supported', evidence: 'parser-state' }, assertions: [{ kind: 'positive', expected: item.pinnedExpected, observed: response }] }
            : { probeId, pass: false, response, note: 'x', observation: { outcome: 'inconclusive', evidence: 'parser-state' }, assertions: [] };
    } };
    assert.doesNotThrow(() => rebuild(evidence, { replayModule: honest }));
});
