import assert from 'node:assert/strict';
import test from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticOsc78Evidence } from './fixtures/osc78-evidence.mjs';
import { metadataProjection } from './metadata.mjs';
import { osc78AdapterId } from './osc78-grading-collector.mjs';
import { buildOsc78GradingReport, evaluateRead, matchesOsc78Baseline, observeFrame, osc78Counts, osc78ObserverPlan } from './osc78-grading.mjs';
import { makeOsc78Experiments, osc78CaseIds, osc78Fixture, osc7EffectContractId, osc8EffectContractId, osc8Forms, osc8PinnedUri, simulateOsc78 } from './osc78-contracts.mjs';

const runId = 'a'.repeat(32), short = 'aaaaaaaa';
const derive = evidence => buildOsc78GradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
const build = options => derive(syntheticOsc78Evidence(options));
const outcomes = report => report.cases.map(item => [item.conclusion.outcome, item.effectContract.outcome]);
const OSC7 = 'extensions.osc7-cwd', OSC8 = 'extensions.osc8';
/** A model App that answers one read differently: the oracle still predicts the exact bytes' effect. */
const adjusting = (experiment, label, change) => ({ adjust: (id, name, state) => { if (id === experiment && name === label) change(state); } });
const uri = osc8PinnedUri;

test('the untouched synthetic evidence supports both effect contracts and preserves both pinned verdicts and their divergences', () => {
    const report = build();
    assert.deepEqual(outcomes(report), [['inconclusive', 'supported'], ['supported', 'supported']]);
    assert.deepEqual(osc78Counts(report), { 'inconclusive/supported': 1, 'supported/supported': 1 });
    assert.deepEqual(report.cases.map(item => item.effectContract.id), [osc7EffectContractId, osc8EffectContractId]);
    assert.deepEqual(report.cases.map(item => item.caseId), [...osc78CaseIds]);
    for (const item of report.cases) assert.match(item.effectContract.attribution, /not the pinned probe/);
    assert.deepEqual(report.cases[0].divergences, [{ pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' }]);
    assert.deepEqual(report.cases[1].divergences, [{ termlessOutcome: 'supported', terminalOutcome: 'inconclusive', kind: 'terminal-path-inconclusive-by-design' }]);
    assert.equal(report.cases[0].conclusion.grading, 'pinned-declaration-only-preserved-by-design');
    assert.equal(report.cases[1].conclusion.grading, 'pinned-termless-replay-over-stored-cells');
    assert.deepEqual(report.cases[0].pinnedFixture, { fixtureBytes: false, termlessFeeds: [], terminalWrites: [] });
    assert.equal(report.snapshots.length, 26); assert.equal(report.restoredSnapshot, 25);
});

test('the report states what was restored: the prepared baseline, never the as-found or original null metadata', () => {
    const { restoration } = build();
    assert.equal(restoration.preparedBaselineRestored, true);
    assert.equal(restoration.originalMetadataRestored, false);
    assert.deepEqual(restoration.initial, { oscTitle: null, workingDirectory: null });
    assert.deepEqual(restoration.final, { oscTitle: `LK-base-${short}`, workingDirectory: '/ntilde-qa/osc78-base' });
    assert.match(restoration.note, /neither the as-found metadata nor the original unprepared metadata is claimed restored/);
});

test('the OSC 7 facts keep the reported URI, the decoded cwd metadata, the missing host component and what was not observed apart', () => {
    const facts = build().cases[0].observed;
    assert.equal(facts.transmitted.space.uri, `file://localhost/ntilde-qa/osc7%20space/${short}`);
    assert.equal(facts.observedCwd.encodedSpace, `/ntilde-qa/osc7 space/${short}`);
    assert.equal(facts.observedCwd.foreignAuthority, `/ntilde-qa/osc7-authority/${short}`);
    assert.equal(facts.hostComponent.exposed, false);
    assert.equal(facts.freshness.baselineFirstSecondDistinct, true);
    assert.deepEqual(facts.notObserved, ['child process working directory', 'existence of the named path', 'remote-host validity']);
});

test('the OSC 8 facts keep the cell range, the witness, the id, the snapshot-local identities and what was not observed apart', () => {
    const facts = build().cases[1].observed, links = osc8Forms(runId);
    assert.deepEqual(facts.pinnedFixture.runs, [{ row: 0, col: 1, length: 4, uri, id: null }]);
    assert.deepEqual([facts.pinnedFixture.closedLinkWitness, facts.pinnedFixture.textBeforeLink], [{ char: 'Z', linked: false }, { char: 'A', linked: false }]);
    assert.deepEqual([facts.idParameter.observed, facts.idParameter.sent], [[links.idValue], links.idValue]);
    assert.equal(facts.twoLinks.distinctIdentities, 2);
    assert.equal(facts.snapshotLocalIdentity.equalsOscId, false);
    assert.match(facts.snapshotLocalIdentity.lifetime, /snapshot-local/);
    assert.deepEqual(facts.notObserved, ['click handling', 'browser launch', 'URI safety policy', 'rendered underline appearance']);
});

// ---------- OSC 8 effect refusals: each control must reject its named defect ----------

const osc8Refusals = [
    ['link-open ignored (correct text, no hyperlink metadata)', adjusting('extensions.osc8/effect', 'pinned', state => { state.runs = []; }), 'pinned-fixture-effect'],
    ['a wrong URI', adjusting('extensions.osc8/effect', 'pinned', state => { state.runs[0].uri = 'https://example.com/other'; }), 'pinned-fixture-effect'],
    ['a wrong id', adjusting('extensions.osc8/id', 'linked', state => { state.runs[0].id = 'osc8-wrong'; }), 'id-parameter'],
    ['a dropped id', adjusting('extensions.osc8/id', 'linked', state => { state.runs[0].id = null; }), 'id-parameter'],
    ['a range extending into the closed-link witness', adjusting('extensions.osc8/effect', 'pinned', state => { state.runs[0].length = 5; }), 'pinned-fixture-effect'],
    ['a range that stops short of the last linked cell', adjusting('extensions.osc8/effect', 'pinned', state => { state.runs[0].length = 3; }), 'pinned-fixture-effect'],
    ['a range shifted one column left', adjusting('extensions.osc8/effect', 'pinned', state => { state.runs[0].col = 0; }), 'pinned-fixture-effect'],
    ['stale hyperlink metadata surviving a plain overwrite', adjusting('extensions.osc8/stale', 'cleared', state => { state.runs = [{ row: 0, col: 1, length: 4, identity: 0, uri, id: null }]; }), 'no-stale-links'],
    ['stale hyperlink metadata surviving a second fixture', adjusting('extensions.osc8/stale', 'second', state => { state.runs[0].uri = uri; }), 'no-stale-links'],
    ['a second distinct link merged into the first', adjusting('extensions.osc8/two-links', 'linked', state => { state.runs = [{ ...state.runs[0], length: 6 }]; }), 'two-links-not-merged'],
    ['the second link replaced by a copy of the first', adjusting('extensions.osc8/two-links', 'linked', state => { state.runs[1].uri = state.runs[0].uri; }), 'two-links-not-merged'],
    ['a closed link affecting subsequent text', adjusting('extensions.osc8/effect', 'later', state => { state.runs.push({ row: 4, col: 0, length: 5, identity: 1, uri, id: null }); }), 'closed-link-later-text'],
    ['a closed link continuing across the witness and the later text', adjusting('extensions.osc8/effect', 'later', state => { state.runs[0].length = 5; }), 'closed-link-later-text'],
];
for (const [label, options, failed] of osc8Refusals) {
    test(`OSC 8: ${label} is unsupported when every validity control held`, () => {
        const item = build(options).cases[1];
        assert.equal(item.effectContract.outcome, 'unsupported', label);
        assert.ok(item.effectContract.failed.includes(failed), `${failed} in ${item.effectContract.failed}`);
        assert.ok(item.effectContract.validity.every(entry => entry.held));
    });
}

test('OSC 8: a link-open ignored at the prepared stock read leaves the pinned termless verdict and the effect contract inconclusive, never supported', () => {
    const report = build(adjusting('extensions.osc8/stock', 'pinned', state => { state.runs = []; }));
    assert.deepEqual(outcomes(report)[1], ['inconclusive', 'inconclusive']);
    assert.equal(report.cases[1].conclusion.outcome, 'inconclusive');
    assert.equal(report.cases[1].effectContract.outcome, 'inconclusive', 'the prepared stock read did not match the oracle, so no conclusive grade');
});

test('OSC 8: a leaked pinned link is unsupported for both the pinned termless probe and the effect contract', () => {
    const report = build({ adjust: (id, name, state) => { if ((id === 'extensions.osc8/stock' || id === 'extensions.osc8/effect') && name === 'pinned') state.runs[0].length = 5; } });
    assert.deepEqual(outcomes(report)[1], ['unsupported', 'inconclusive'], 'the stock read no longer matches the oracle, so the effect contract cannot decide');
    const effectOnly = build(adjusting('extensions.osc8/effect', 'pinned', state => { state.runs[0].length = 5; }));
    assert.deepEqual(outcomes(effectOnly)[1], ['supported', 'unsupported'], 'the pinned stock replay and the effect contract are separately identified and can differ');
    assert.deepEqual(effectOnly.cases[1].divergences.map(item => item.kind), ['pinned-and-effect-differ', 'terminal-path-inconclusive-by-design']);
});

test('OSC 8: an invalid validity control makes the case inconclusive: the prior state, the unmarked text and the close-only gate must show no link', () => {
    for (const [experiment, label, expectedInvalid] of [['extensions.osc8/effect', 'prior', 'prior-state'], ['extensions.osc8/effect', 'ordinary', 'unmarked-control'], ['extensions.osc8/gate-close', 'post', 'gate-close']]) {
        const item = build(adjusting(experiment, label, state => { state.runs = [{ row: 0, col: 1, length: 4, identity: 0, uri, id: null }]; })).cases[1];
        assert.equal(item.effectContract.outcome, 'inconclusive', experiment + label);
        assert.ok(item.effectContract.invalid.includes(expectedInvalid), `${expectedInvalid} in ${item.effectContract.invalid}`);
    }
});

// ---------- OSC 7 effect refusals ----------

const space = `/ntilde-qa/osc7 space/${short}`, first = `/ntilde-qa/osc7-${short}/first`;
const osc7Refusals = [
    ['no update after the first OSC 7', adjusting('extensions.osc7-cwd/effect', 'first', state => { state.cwd = '/ntilde-qa/osc78-base'; }), 'first-update'],
    ['a stale second update that still reports the first path', adjusting('extensions.osc7-cwd/effect', 'second', state => { state.cwd = first; }), 'fresh-second-update'],
    ['a second update that reports the baseline again', adjusting('extensions.osc7-cwd/effect', 'second', state => { state.cwd = '/ntilde-qa/osc78-base'; }), 'fresh-second-update'],
    ['an encoded space that is not decoded', adjusting('extensions.osc7-cwd/space', 'encoded', state => { state.cwd = space.replaceAll(' ', '%20'); }), 'encoded-space'],
    ['an encoded space that is dropped', adjusting('extensions.osc7-cwd/space', 'encoded', state => { state.cwd = space.replaceAll(' ', ''); }), 'encoded-space'],
    ['a host component left in the path', adjusting('extensions.osc7-cwd/effect', 'first', state => { state.cwd = 'localhost' + first; }), 'first-update'],
    ['a baseline that is not restored', adjusting('extensions.osc7-cwd/effect', 'restored', state => { state.cwd = `/ntilde-qa/osc7-${short}/second`; }), 'restoration-to-baseline'],
];
for (const [label, options, failed] of osc7Refusals) {
    test(`OSC 7: ${label} is unsupported when every validity control held`, () => {
        const item = build(options).cases[0];
        assert.equal(item.effectContract.outcome, 'unsupported', label);
        assert.ok(item.effectContract.failed.includes(failed), `${failed} in ${item.effectContract.failed}`);
        assert.ok(item.effectContract.validity.every(entry => entry.held));
        assert.equal(item.conclusion.outcome, 'inconclusive', 'the pinned verdict is preserved unchanged');
    });
}

test('OSC 7: an OSC 7 that disturbs the title, the cells, the cursor or the hyperlinks is unsupported', () => {
    const changes = [state => { state.title = 'changed'; }, state => { state.rows[3] = 'stray'; }, state => { state.cursor = [5, 5]; },
        state => { state.runs = [{ row: 0, col: 0, length: 2, identity: 0, uri, id: null }]; }];
    for (const change of changes) {
        const item = build(adjusting('extensions.osc7-cwd/effect', 'first', change)).cases[0];
        assert.equal(item.effectContract.outcome, 'unsupported');
        assert.ok(item.effectContract.failed.includes('first-update'));
    }
});

test('OSC 7: invalid controls are inconclusive: a cwd change by plain text, by an OSC 2 title, or a prior state that is not the baseline', () => {
    for (const [label, options, invalid] of [
        ['plain text moved the cwd', adjusting('extensions.osc7-cwd/effect', 'ordinary', state => { state.cwd = first; }), 'unmarked-control'],
        ['an OSC 2 title moved the cwd', adjusting('extensions.osc7-cwd/gate-title', 'post', state => { state.cwd = first; }), 'gate-title'],
        ['the prior cwd was not the baseline', adjusting('extensions.osc7-cwd/effect', 'prior', state => { state.cwd = first; }), 'prior-state']]) {
        const item = build(options).cases[0];
        assert.equal(item.effectContract.outcome, 'inconclusive', label);
        assert.ok(item.effectContract.invalid.includes(invalid), `${invalid} in ${item.effectContract.invalid}`);
    }
    const stock = build(adjusting('extensions.osc7-cwd/stock', 'baseline', state => { state.cwd = first; })).cases[0];
    assert.equal(stock.effectContract.outcome, 'inconclusive', 'a prepared baseline that is not the declared baseline');
});

test('recorded-only variants never decide: the foreign authority, the ST terminator and the same-anchor links do not change a verdict', () => {
    const report = build({ adjust: (id, name, state) => {
        if (id === 'extensions.osc7-cwd/authority') state.cwd = 'something-else';
        if (id === 'extensions.osc7-cwd/st-terminator') state.cwd = 'something-else';
        if (id === 'extensions.osc8/same-anchor') state.runs = [];
        if (id === 'extensions.osc8/st-terminator') state.runs = [];
    } });
    assert.deepEqual(outcomes(report), [['inconclusive', 'supported'], ['supported', 'supported']]);
    const authority = report.cases[0].experiments.find(item => item.name === 'authority').reads[0].evaluation;
    assert.equal(authority.passed, false); assert.equal(authority.required.cwd.match, false);
});

// ---------- structural refusals ----------

test('the observer plan is one entry per read and refuses a grid below the fixture minimum', () => {
    const { frames } = syntheticOsc78Evidence();
    assert.equal(osc78ObserverPlan(frames[0].before, runId).length, 23);
    const small = structuredClone(frames[0].before); small.screen.rows = 11;
    assert.throws(() => osc78ObserverPlan(small, runId), /needs a larger grid/);
});

test('a truncated journal, a missing frame and an extra frame are refused', () => {
    const evidence = syntheticOsc78Evidence();
    assert.throws(() => buildOsc78GradingReport(evidence.frames.slice(0, -1), evidence.run, runId, evidence.adapter), /Incomplete OSC 7\/8 snapshot sequence/);
    assert.throws(() => buildOsc78GradingReport([...evidence.frames, evidence.frames.at(-1)], evidence.run, runId, evidence.adapter), /Incomplete OSC 7\/8 snapshot sequence/);
    assert.throws(() => buildOsc78GradingReport(evidence.frames, evidence.run, 'b'.repeat(32), evidence.adapter));
});

test('edited frames are refused: a changed digest, a changed hyperlink run and a changed cwd that no longer matches its hash', () => {
    for (const change of [frame => { frame.sha256 = '0'.repeat(64); }, frame => { frame.before.metadata.buffer.links.push({ row: 5, col: 0, length: 1, identity: 0, uri, id: null }); },
        frame => { frame.before.metadata.workingDirectory = '/edited'; }]) {
        const evidence = syntheticOsc78Evidence(); change(evidence.frames[7]);
        assert.throws(() => derive(evidence));
    }
});

test('a wrong fixture is refused: changed recorded feeds, a changed barrier or a changed read kind', () => {
    const cases = [run => { run.experiments[1].feeds[2] = run.experiments[1].feeds[2].replaceAll('first', 'forged'); },
        run => { run.experiments[0].barriers[0] = '\x1b[?9;9c'; }, run => { run.experiments[7].reads[0].kind = 'other'; },
        run => { run.experiments[7].reads[1].cursor = [3, 3]; }, run => { run.experiments.pop(); }, run => { run.experiments.reverse(); }];
    for (const change of cases) {
        const evidence = syntheticOsc78Evidence(); change(evidence.run);
        assert.throws(() => derive(evidence), /feeds do not match|cursor differs|barrier differs|Wrong |Invalid|order differs|does not cover/);
    }
});

test('cross-case evidence substitution is refused: the pinned run record or the stock read of the other case', () => {
    const swapped = syntheticOsc78Evidence();
    swapped.run.pinned[0].termless = swapped.run.pinned[1].termless;
    assert.throws(() => derive(swapped), /Missing pinned/);
    const other = syntheticOsc78Evidence();
    other.run.pinned[1].termless.result = structuredClone(other.run.pinned[0].termless.result);
    assert.throws(() => derive(other), /does not match its observed evidence/);
    const frames = syntheticOsc78Evidence(), borrowed = syntheticOsc78Evidence();
    frames.frames[2] = { ...borrowed.frames[12], index: 2, kind: frames.frames[2].kind };
    assert.throws(() => derive(frames), /cursor differs from its read frame/, 'the stock read of the other case has another cursor');
    const lookalike = syntheticOsc78Evidence();
    lookalike.frames[2] = { ...borrowed.frames[9], index: 2, kind: lookalike.frames[2].kind };
    assert.notEqual(derive(lookalike).cases[0].effectContract.outcome, 'supported', 'a read from another experiment never grades as the baseline');
});

test('a pinned result that no longer equals the replay over the stock read is refused, and a terminal path that decides is refused', () => {
    const tampered = syntheticOsc78Evidence();
    tampered.run.pinned[1].termless.result.observation.outcome = 'unsupported';
    assert.throws(() => derive(tampered), /does not match its observed evidence/);
    const decided = syntheticOsc78Evidence();
    decided.run.pinned[1].terminal.result.observation.outcome = 'supported';
    assert.throws(() => derive(decided), /does not match its observed evidence/);
    const declared = syntheticOsc78Evidence();
    declared.run.pinned[0].termless.result.response = '{"declared":false}';
    assert.throws(() => derive(declared), /does not match its observed evidence/);
});

test('an identity mismatch is refused: a different adapter id, a missing digest or a different parent adapter', () => {
    for (const change of [adapter => { adapter.id = 'ntilde-owned-osc133-grading-v1'; }, adapter => { adapter.bundleSha256 = 'xyz'; }, adapter => { adapter.derivedFromAdapterId = 'other'; },
        adapter => { delete adapter.upstreamBundleSha256; }]) {
        const evidence = syntheticOsc78Evidence(); change(evidence.adapter);
        assert.throws(() => derive(evidence), /Invalid OSC 7\/8 adapter identity/);
    }
    const run = syntheticOsc78Evidence(); run.run.adapterId = 'other';
    assert.throws(() => derive(run), /Invalid OSC 7\/8 adapter identity/);
    assert.equal(syntheticOsc78Evidence().adapter.id, osc78AdapterId);
});

test('incomplete restoration is refused: a cwd, a link, the title or the cells not back at the baseline', () => {
    for (const change of [frame => { frame.before.metadata.workingDirectory = `/ntilde-qa/osc7-${short}/second`; }, frame => { frame.before.metadata.oscTitle = 'LK-unrestored'; frame.before.metadata.displayTitle = 'LK-unrestored'; },
        frame => { frame.before.metadata.buffer.links = [{ row: 0, col: 1, length: 4, identity: 0, uri, id: null }]; }, frame => { frame.before.screen.lines[0] = 'leftover'; }]) {
        const evidence = syntheticOsc78Evidence(), last = evidence.frames.at(-1); last.before = structuredClone(last.before); change(last);
        last.sha256 = last.afterSha256 = snapshotHash(metadataProjection(last.before)); last.after = structuredClone(last.before);
        assert.throws(() => derive(evidence), /baseline binding or restoration failed|Invalid or incomplete|Changed/);
    }
});

test('a missing observation or stale identity: frames from another run id fail the nonce binding', () => {
    const evidence = syntheticOsc78Evidence({ runId: 'c'.repeat(32) });
    assert.throws(() => buildOsc78GradingReport(evidence.frames, evidence.run, runId, evidence.adapter), /baseline binding or restoration failed/);
});

test('the parent module replay must reproduce the live pinned result', () => {
    const evidence = syntheticOsc78Evidence();
    const faithful = { ntildeRunOsc78Termless: (caseId, ctx) => {
        if (caseId === OSC7) return { probeId: caseId, ...build().cases[0].pinned.termless };
        ctx.feed(ctx.getCell ? '\x1b[1;1H\x1b[2KA\x1b]8;;' + uri + '\x07LINK\x1b]8;;\x07Z' : '');
        return { probeId: caseId, ...build().cases[1].pinned.termless };
    } };
    assert.doesNotThrow(() => buildOsc78GradingReport(evidence.frames, evidence.run, runId, evidence.adapter, { replayModule: faithful }));
    const lying = { ntildeRunOsc78Termless: (caseId, ctx) => { if (caseId === OSC8) ctx.feed('\x1b[1;1H\x1b[2KA\x1b]8;;' + uri + '\x07LINK\x1b]8;;\x07Z'); return { probeId: caseId, pass: true, response: 'x', observation: { outcome: 'supported' } }; } };
    assert.throws(() => buildOsc78GradingReport(evidence.frames, evidence.run, runId, evidence.adapter, { replayModule: lying }), /replay does not reproduce the live grading/);
});

test('evaluateRead and observeFrame expose the stored facts the oracle compares, and the baseline predicate separates start from finish', () => {
    const evidence = syntheticOsc78Evidence(), fixture = osc78Fixture(runId);
    const state = simulateOsc78([fixture.stage], fixture, runId);
    const observed = observeFrame(evidence.frames[2]);
    assert.deepEqual([observed.cwd, observed.title, observed.links, observed.status], ['/ntilde-qa/osc78-base', `LK-base-${short}`, [], 'prompt']);
    assert.equal(evaluateRead(evidence.frames[2], state, evidence.frames[1]).passed, true);
    assert.equal(evaluateRead(evidence.frames[2], { ...state, cwd: 'elsewhere' }, evidence.frames[1]).required.cwd.match, false);
    const first = evidence.frames[0].before;
    assert.equal(matchesOsc78Baseline(evidence.frames[1].before, first, runId, false), true);
    assert.equal(matchesOsc78Baseline(evidence.frames[1].before, first, runId, true), false, 'the bound screen is not the restored one');
    assert.equal(matchesOsc78Baseline(evidence.frames.at(-1).before, first, runId, true), true);
    assert.equal(makeOsc78Experiments(runId).length, 14);
});
