import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildScrollbackGradingReport, replayScrollbackPinned, validateScrollbackEvidence, reconcileScrollback } from './scrollback-grading.mjs';
import { scrollbackSuiteId, scrollbackContractsVersion, pinnedScrollbackNotes, scrollbackCaseIds } from './scrollback-contracts.mjs';
import { scrollbackRecordingContext } from './scrollback-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticScrollbackEvidence, syntheticScrollbackContracts, syntheticScrollbackScreens, paletteBinding } from './fixtures/scrollback-evidence.mjs';

const runId = 'c'.repeat(32);
const contracts = syntheticScrollbackContracts();
const byId = id => contracts.find(contract => contract.caseId === id);
const evidenceWith = (id, role, change, binding) => syntheticScrollbackEvidence(runId, 'f'.repeat(64), binding, (caseId, name, state) => {
    if (caseId === id && name === role) change(state);
    return state;
});
const build = evidence => buildScrollbackGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep shared screen and binding objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const outcomes = report => Object.fromEntries(report.cases.map(item => [item.caseId, [item.pinned.observation.outcome, item.conclusion.outcome, item.divergence?.kind ?? null]]));

test('a complete run rederives all nine cases: deciding probes agree and the three null probes are recorded divergences', () => {
    const report = build(syntheticScrollbackEvidence(runId));
    assert.equal(report.suiteId, scrollbackSuiteId);
    assert.equal(report.contractsVersion, scrollbackContractsVersion);
    assert.deepEqual(report.cases.map(item => item.caseId), [...scrollbackCaseIds]);
    assert.deepEqual(outcomes(report), {
        'scrollback.accumulate': ['supported', 'supported', null], 'scrollback.total-lines': ['supported', 'supported', null],
        'scrollback.scroll-up': ['supported', 'supported', null], 'scrollback.reverse-index': ['supported', 'supported', null],
        'scrollback.scroll-down': ['supported', 'supported', null],
        'scrollback.set-region': ['inconclusive', 'supported', 'contract-measures-beyond-pinned'],
        'scrollback.alt-screen': ['inconclusive', 'supported', 'contract-measures-beyond-pinned'],
        'scrollback.decstbm': ['supported', 'supported', null],
        'scrollback.decstbm-reset': ['inconclusive', 'supported', 'contract-measures-beyond-pinned'] });
    assert.equal(report.snapshots.length, 48);
    assert.match(report.scope, /Three pinned probes are null by construction/);
    for (const item of report.cases.filter(entry => entry.divergence)) {
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'contract-measures-beyond-pinned' });
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.conclusion.evidence, 'owned-stored-cells-and-actual-scrollback');
    }
    const notes = Object.fromEntries(report.cases.map(item => [item.caseId, item.pinned.note]));
    assert.equal(notes['scrollback.set-region'], pinnedScrollbackNotes.region);
    assert.equal(notes['scrollback.alt-screen'], pinnedScrollbackNotes.alt);
    assert.equal(notes['scrollback.decstbm-reset'], pinnedScrollbackNotes.reset);
});

test('the pinned responses carry the measured scrollback totals, the stored cells and the mode flag the pinned code actually read', () => {
    const cases = Object.fromEntries(build(syntheticScrollbackEvidence(runId)).cases.map(item => [item.caseId, item.pinned]));
    const accumulate = JSON.parse(cases['scrollback.accumulate'].response);
    assert.deepEqual(accumulate.baseline, { screenLines: 16, totalLines: 16, viewportY: 0 });
    assert.deepEqual(accumulate.scroll, { screenLines: 16, totalLines: 27, viewportY: 0 });
    assert.equal(JSON.parse(cases['scrollback.total-lines'].response).lineCount, 26);
    const up = JSON.parse(cases['scrollback.scroll-up'].response);
    assert.deepEqual([up.seed.join(''), up.after.join('')], ['ABCDE', 'ACD E'.replace(' ', ' ')]);
    assert.deepEqual([up.beforeScroll.totalLines, up.afterScroll.totalLines], [16, 16]);
    assert.deepEqual(JSON.parse(cases['scrollback.set-region'].response), { cursor: { x: 0, y: 0, visible: true } });
    assert.deepEqual(JSON.parse(cases['scrollback.alt-screen'].response), { mode: true });
    const decstbm = JSON.parse(cases['scrollback.decstbm'].response);
    assert.deepEqual([decstbm.before.char, decstbm.innerBefore.char, decstbm.innerAfter.char, decstbm.topAfter.char], ['F', 'I', ' ', 'F']);
    assert.deepEqual(JSON.parse(cases['scrollback.decstbm-reset'].response).scroll, { screenLines: 16, totalLines: 27, viewportY: 0 });
});

test('a no-op scroll grades unsupported by both the pinned probe and the contract', () => {
    const noop = (id, role, lines) => outcomes(build(evidenceWith(id, role, state => { state.lines = lines(state.lines); })))[id];
    assert.deepEqual(noop('scrollback.scroll-up', 'after', lines => ['A', 'B', 'C', 'D', 'E', ...lines.slice(5)]), ['unsupported', 'unsupported', null]);
    assert.deepEqual(noop('scrollback.reverse-index', 'after', lines => ['A', 'B', 'C', ...lines.slice(3)].slice(0, 16).concat([])), ['unsupported', 'unsupported', null]);
    assert.deepEqual(noop('scrollback.scroll-down', 'after', lines => ['LINE1', 'LINE2', 'LINE3', ...Array.from({ length: 13 }, () => '')]), ['unsupported', 'unsupported', null]);
});

test('accidental history clearing and a missing overflow are unsupported by the pinned probe and the contract', () => {
    for (const id of ['scrollback.accumulate', 'scrollback.total-lines']) {
        const report = build(evidenceWith(id, 'final', state => { state.history = []; }));
        assert.deepEqual(outcomes(report)[id], ['unsupported', 'unsupported', null], id);
    }
});

test('damage the pinned probe cannot see is a recorded contract-stricter divergence from a supported pinned verdict', () => {
    const cases = [
        ['scrollback.scroll-up', 'wideAfter', state => { state.lines[8] = 'ROW-X'; }],
        ['scrollback.accumulate', 'justFull', state => { state.history = ['line 0']; }],
        ['scrollback.decstbm', 'ctlAfter', state => { state.lines[12] = ''; }],
        ['scrollback.scroll-up', 'restored', state => { state.bottom = 10; }],
        ['scrollback.reverse-index', 'moveOnly', state => { state.lines = ['', 'A', 'B', 'C', ...state.lines.slice(4)]; }],
        ['scrollback.scroll-down', 'fullAfter', state => { state.history = ['ROW-15']; }],
    ];
    for (const [id, role, change] of cases) {
        const item = build(evidenceWith(id, role, change)).cases.find(entry => entry.caseId === id);
        assert.equal(item.pinned.observation.outcome, 'supported', `${id} ${role}`);
        assert.equal(item.conclusion.outcome, 'unsupported', `${id} ${role}`);
        assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' }, `${id} ${role}`);
        assert.ok(item.conclusion.note.startsWith(`${role}: `), item.conclusion.note);
    }
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    const scrollUp = build(evidenceWith('scrollback.scroll-up', 'seed', state => { state.lines[0] = ''; })).cases[2];
    assert.equal(scrollUp.pinned.observation.outcome, 'inconclusive');
    assert.equal(scrollUp.pinned.note, 'Interior-region SU seed or history was not established');
    assert.equal(scrollUp.conclusion.outcome, 'inconclusive');
    const stale = build(evidenceWith('scrollback.scroll-up', 'initial', state => { state.history = ['stale']; })).cases[2];
    assert.equal(stale.pinned.observation.outcome, 'inconclusive', 'the pinned empty-history precondition');
    assert.equal(stale.conclusion.outcome, 'inconclusive');
    const decstbm = build(evidenceWith('scrollback.decstbm', 'innerBefore', state => { state.lines[2] = ''; })).cases[7];
    assert.equal(decstbm.pinned.note, 'Top marker or inner scrolling control was not established');
    assert.equal(decstbm.conclusion.outcome, 'inconclusive');
    const reverse = build(evidenceWith('scrollback.reverse-index', 'before', state => { state.lines[0] = ''; })).cases[3];
    assert.equal(reverse.pinned.note, 'A marker was not measured before reverse index');
    assert.equal(reverse.conclusion.outcome, 'inconclusive');
});

test('for the null pinned probes the contract may grade unsupported, and every difference is recorded', () => {
    const cases = [['scrollback.set-region', 'ctlScrolled', state => { state.history = ['ROW-0']; }],
        ['scrollback.alt-screen', 'restored', state => { state.history = []; }],
        ['scrollback.decstbm-reset', 'final', state => { state.history = []; }]];
    for (const [id, role, change] of cases) {
        const item = build(evidenceWith(id, role, change)).cases.find(entry => entry.caseId === id);
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measures-beyond-pinned' }, id);
    }
    const invalid = build(evidenceWith('scrollback.alt-screen', 'seeded', state => { state.history = []; })).cases[6];
    assert.deepEqual(invalid.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'inconclusive', kind: 'contract-measures-beyond-pinned' });
    const contract = byId('scrollback.alt-screen');
    assert.throws(() => reconcileScrollback(contract, 'supported', { outcome: 'supported' }), /expected to be inconclusive by construction/);
    assert.throws(() => reconcileScrollback(byId('scrollback.scroll-up'), 'inconclusive', { outcome: 'supported' }), /more permissive than the pinned probe/);
    assert.equal(reconcileScrollback(byId('scrollback.scroll-up'), 'supported', { outcome: 'supported' }), null);
});

test('a combined reset-plus-overflow fixture cannot be replaced by separate margin-reset and accumulation passes', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    const separate = clone(evidence);
    const feeds = separate.run.cases[8].feeds;
    // Separate passes: the margin pair and the overflow no longer follow one another with the home move between them.
    feeds.splice(feeds.indexOf('\x1b[H'), 1);
    assert.throws(() => build(separate), /do not match the pinned contract/);
    const split = clone(evidence);
    split.run.cases[8].feeds.splice(3, 0, '\x1b[2J\x1b[3J');
    assert.throws(() => build(split), /do not match the pinned contract/);
    const shorter = clone(evidence);
    shorter.run.cases[8].feeds.splice(10, 10);
    assert.throws(() => build(shorter), /do not match the pinned contract/);
    const insufficient = clone(evidence);
    insufficient.run.cases[0].feeds.pop();
    assert.throws(() => build(insufficient), /do not match the pinned contract/);
});

test('the recording context serves a read only at a journaled feed count and refuses unknown bytes', () => {
    for (const contract of contracts) {
        const screens = syntheticScrollbackScreens(contract);
        const replay = replayScrollbackPinned(contract, screens);
        assert.equal(replay.observation.evidence, 'parser-state');
        const context = scrollbackRecordingContext(contract, screens);
        assert.equal(context.cols, 60);
        assert.throws(() => context.feed('x'), /differs from the contract/);
        assert.throws(() => context.getText(), /must not read text/);
        assert.throws(() => context.getMode('mouse'), /unmodeled mode/);
    }
    const contract = byId('scrollback.reverse-index');
    const context = scrollbackRecordingContext(contract, syntheticScrollbackScreens(contract));
    assert.throws(() => context.getCell(0, 0), /without an observed state/);
    context.feed(contract.pinnedFeeds[0]);
    assert.equal(context.getCell(0, 0).char, 'A');
    assert.equal(context.complete, false);
    context.feed(contract.pinnedFeeds[1]);
    assert.equal(context.complete, true);
    assert.throws(() => context.feed(contract.pinnedFeeds[1]), /differs from the contract/);
});

test('a pinned probe that returns early may feed only its declared cleanup reset', () => {
    const contract = byId('scrollback.scroll-up');
    const context = scrollbackRecordingContext(contract, syntheticScrollbackScreens(contract));
    for (const feed of contract.pinnedFeeds.slice(0, 7)) context.feed(feed);
    context.feed('\x1b[r');
    assert.equal(context.complete, true);
    assert.throws(() => context.feed('\x1b[r'), /differs from the contract/);
    const other = byId('scrollback.reverse-index');
    const strict = scrollbackRecordingContext(other, syntheticScrollbackScreens(other));
    strict.feed(other.pinnedFeeds[0]);
    assert.throws(() => strict.feed('\x1b[r'), /differs from the contract/, 'only probes with a finally block have a cleanup');
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[5].pinned.observation.outcome = 'supported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[6].pinned.response = '{"mode":false}'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Scrollback adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-closure-grading-v1'; })), /Invalid Scrollback adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Scrollback adapter identity/);
    assert.throws(() => buildScrollbackGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[0] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame|ordering/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'cells';
    assert.throws(() => build(kind), /Wrong scrollback\.total-lines read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    const stale = clone(evidence);
    stale.frames[4].screen.lines[0] = 'line X';
    assert.throws(() => build(stale), /Changed Scrollback snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Scrollback snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Scrollback snapshot sequence/);
    const history = clone(evidence);
    history.frames[4].screen.history.lines.push('late');
    assert.throws(() => build(history), /Changed Scrollback snapshot evidence|Incomplete owned editing history/);
});

test('an edited screen with a recomputed digest is caught by the pinned agreement or the contract, not the digest', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    const copy = clone(evidence);
    const frame = copy.frames[4];
    frame.screen.cursorCol = 4;
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /cursor differs from its read frame/);
    const history = clone(evidence);
    const final = history.frames[4];
    final.screen.history.lines = ['x'];
    final.screen.history.totalLines = 1;
    final.screen.bufferState.scrollbackLines = 1;
    final.sha256 = snapshotHash(final.screen);
    assert.throws(() => build(history), /does not match its observed evidence/);
});

test('inconsistent buffer state, flags and details are refused', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    const attrs = clone(evidence);
    attrs.frames[4].screen.attributeRuns[0].flags = 0x0301;
    attrs.frames[4].sha256 = snapshotHash(attrs.frames[4].screen);
    assert.throws(() => build(attrs));
    const count = clone(evidence);
    count.frames[4].screen.bufferState.scrollbackLines = 3;
    count.frames[4].sha256 = snapshotHash(count.frames[4].screen);
    assert.throws(() => build(count));
    const missing = clone(evidence);
    delete missing.frames[4].screen.bufferState;
    missing.frames[4].sha256 = snapshotHash(missing.frames[4].screen);
    assert.throws(() => build(missing));
});

test('incomplete restoration, an unbound start and a wrong geometry are refused', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    const copy = clone(evidence);
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
    const history = clone(evidence);
    const finish = history.frames.at(-1);
    finish.screen.history = { startLine: 0, totalLines: 1, lines: ['x'] };
    finish.screen.bufferState.scrollbackLines = 1;
    finish.sha256 = snapshotHash(finish.screen);
    assert.throws(() => build(history), /was not restored/);
    const huge = clone(evidence);
    huge.frames[0].screen.cols = 1e9;
    assert.throws(() => buildScrollbackGradingReport(huge.frames, huge.run, huge.runId, huge.adapter), /at most 128 rows and 256 columns/);
    const nonce = clone(evidence);
    nonce.frames[1].screen.lines[0] = 'NTILDE_SB_other';
    nonce.frames[1].sha256 = snapshotHash(nonce.frames[1].screen);
    assert.throws(() => build(nonce), /not bound to this run/);
    assert.throws(() => buildScrollbackGradingReport([{ ...evidence.frames[0], screen: { ...evidence.frames[0].screen, rows: 5 } }, ...evidence.frames.slice(1)],
        evidence.run, evidence.runId, evidence.adapter), /at least 12 rows/);
});

test('DECSCNM on throughout is a stable but unusable run: every case is inconclusive, never graded', () => {
    const everywhere = syntheticScrollbackEvidence(runId, 'f'.repeat(64), { ...paletteBinding(), reverseVideo: true });
    assert.ok(build(everywhere).cases.every(item => item.conclusion.outcome === 'inconclusive'));
    const copy = clone(syntheticScrollbackEvidence(runId));
    copy.frames[4].screen.colorBinding.reverseVideo = true;
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('a theme or palette change during the suite voids the run', () => {
    const copy = clone(syntheticScrollbackEvidence(runId));
    copy.frames[4].screen.colorBinding.themeName = 'other';
    copy.frames[4].sha256 = snapshotHash(copy.frames[4].screen);
    assert.throws(() => build(copy), /palette or theme change/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticScrollbackEvidence(runId);
    assert.doesNotThrow(() => validateScrollbackEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildScrollbackGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown scrollback contracts revision 2/);
});
