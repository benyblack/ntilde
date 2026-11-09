import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildCharsetsGradingReport, replayCharsetsPinned, validateCharsetsEvidence, parserStateResult } from './charsets-grading.mjs';
import { charsetsContracts, charsetsSuiteId, charsetsContractsVersion } from './charsets-contracts.mjs';
import { charsetsRecordingContext, requirePinnedReplay } from './charsets-collector.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticCharsetsEvidence, syntheticCharsetsReads, syntheticCharsetsScreens } from './fixtures/charsets-evidence.mjs';

const UTF8 = 'charsets.utf8', SPECIAL = 'charsets.dec-special', LINES = 'charsets.dec-line-drawing', G01 = 'charsets.g0-g1-switching';
const runId = 'c'.repeat(32);
const evidenceWith = (caseId, edit) => syntheticCharsetsEvidence(runId, 'f'.repeat(64), id => {
    const reads = syntheticCharsetsReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const build = evidence => buildCharsetsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** A JSON round trip: structuredClone would keep shared objects shared between frames. */
const clone = value => JSON.parse(JSON.stringify(value));
const contractOf = id => charsetsContracts.find(item => item.caseId === id);

test('a complete run rederives all four cases: pinned and contract agree and nothing diverges', () => {
    const report = build(syntheticCharsetsEvidence(runId));
    assert.equal(report.suiteId, charsetsSuiteId);
    assert.equal(report.contractsVersion, charsetsContractsVersion);
    assert.deepEqual(report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.conclusion.outcome]), [
        [UTF8, 'supported', 'supported'], [SPECIAL, 'supported', 'supported'], [LINES, 'supported', 'supported'], [G01, 'supported', 'supported']]);
    for (const item of report.cases) {
        assert.equal(item.divergence, undefined);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.equal(item.conclusion.evidence, 'owned-stored-physical-cells');
        assert.equal(item.pinned.assertions[0].expected, contractOf(item.caseId).pinnedExpected);
    }
    assert.equal(report.snapshots.length, 3 + 12 + 12 + 12 + 14);
    assert.match(report.scope, /never verdicts and are not qualified/);
    assert.match(report.scope, /an uppercase marker never proves ASCII restoration/);
});

test('the pinned responses carry exactly the cells and cursors the pinned probes read, including the empty wide continuation', () => {
    const [utf8, special, lines, g01] = build(syntheticCharsetsEvidence(runId)).cases;
    assert.equal(utf8.pinned.response, JSON.stringify({ before: ['A', 'x', 'y', '?', 'Z'], setup: [{ x: 1, y: 0 }, { x: 2, y: 0 }], after: ['A', 'é', '世', '', 'Z'] }));
    assert.equal(special.pinned.response, JSON.stringify({ before: ['q', 'q', 'q'], setup: [{ x: 1, y: 0 }], after: ['q', '─', 'q'] }));
    assert.equal(lines.pinned.response, JSON.stringify({ before: [...'jklmqxj'], setup: [{ x: 0, y: 0 }], after: [...'┘┐┌└─│j'] }));
    assert.equal(g01.pinned.response, JSON.stringify({ before: [...'AlllZ'], setup: [{ x: 1, y: 0 }], after: ['A', 'l', '┌', 'l', 'Z'] }));
});

test('the observed record keeps decoded code points, cell structure, switching and restoration controls explicit', () => {
    const [utf8, special, , g01] = build(syntheticCharsetsEvidence(runId)).cases.map(item => item.observed);
    assert.deepEqual(utf8.groups.target.cells, ['0,0,A', '0,1,é U+00E9', '0,2,世 U+4E16 wide', '0,3,<continuation>', '0,4,Z']);
    assert.equal(utf8.groups.target.text[0], 'Aé世>Z_______');
    assert.deepEqual(utf8.groups.target.cursor, [0, 4]);
    assert.equal(utf8.groups.decomposed.class, 'observed');
    assert.ok(utf8.groups.decomposed.cells.some(cell => cell.includes('extended')));
    assert.deepEqual(special.groups.target.cells, ['0,0,q', '0,1,─ U+2500', '0,2,q']);
    assert.equal(special.groups.witnessAscii.class, 'restoration');
    assert.equal(special.groups.witnessDec.class, 'behavior');
    assert.equal(special.groups.witnessDec.text[2], '┘┐┌└─│┌_____');
    assert.equal(g01.groups.designateOnly.text[0], 'AlllZ_______');
    assert.equal(g01.groups.g1Invoked.text[0], 'A┌┌┌Z_______');
    assert.equal(g01.groups.persistShift.text[1], '┘┐┌_________');
    assert.deepEqual(special.overlaps.map(item => item.with), ['charsets.dec-line-drawing']);
});

test('a wrong mapping is a pinned unsupported agreed by the contract, with no divergence', () => {
    const report = build(evidenceWith(SPECIAL, reads => { reads.target.rows = ['qqq']; }));
    const item = report.cases[1];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.equal(item.divergence, undefined);
    assert.equal(item.pinned.assertions[0].kind, 'negative');
    assert.equal(report.cases.filter(other => other !== item).every(other => other.conclusion.outcome === 'supported'), true, 'siblings keep their own grades');
});

test('damage only the contract can see is a recorded stricter divergence from a supported pinned verdict', () => {
    // The pinned probes never read restoration witnesses, the cursor after writing, or unlisted cells.
    const stale = build(evidenceWith(G01, reads => { reads.witnessAscii.rows = [reads.witnessAscii.rows[0], '┘┐┌└─│┌']; }));
    assert.equal(stale.cases[3].pinned.observation.outcome, 'supported');
    assert.equal(stale.cases[3].conclusion.outcome, 'inconclusive');
    assert.deepEqual(stale.cases[3].divergence, { pinnedOutcome: 'supported', contractOutcome: 'inconclusive', kind: 'contract-stricter-than-pinned' });
    const unswitched = build(evidenceWith(G01, reads => { reads.persistShift.rows = ['Al┌lZ', 'jkl']; }));
    assert.equal(unswitched.cases[3].pinned.observation.outcome, 'supported');
    assert.deepEqual(unswitched.cases[3].divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
    const cursor = build(evidenceWith(UTF8, reads => { reads.target.cursor = [0, 3]; }));
    assert.equal(cursor.cases[0].conclusion.outcome, 'unsupported');
    assert.equal(cursor.cases[0].divergence.kind, 'contract-stricter-than-pinned');
});

test('the wide continuation the pinned utf8 probe never reads is still required by the contract', () => {
    // The wide sample stored narrow leaves the old ? in column 3, but A, the code points and Z at column 4 are exactly what the pinned probe reads.
    const narrow = build(evidenceWith(UTF8, reads => { reads.target.rows = ['A\u00E9\uE000\u4E16?Z']; }));
    const item = narrow.cases[0];
    assert.equal(item.pinned.observation.outcome, 'supported', 'the pinned probe never reads column 3 or the wide flag');
    assert.equal(item.pinned.response, JSON.stringify({ before: [...'Axy?Z'], setup: [{ x: 1, y: 0 }, { x: 2, y: 0 }], after: ['A', '\u00E9', '\u4E16', '?', 'Z'] }));
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.match(item.conclusion.note, /did not claim a continuation cell/);
    assert.deepEqual(item.divergence, { pinnedOutcome: 'supported', contractOutcome: 'unsupported', kind: 'contract-stricter-than-pinned' });
});

test('an invalid setup is inconclusive in both graders with the pinned note', () => {
    for (const [id, note] of [[SPECIAL, 'ASCII seed was not measured before DEC designation'], [LINES, 'ASCII seed was not measured before line drawing'],
        [G01, 'ASCII seed was not measured before G0/G1 switching'], [UTF8, 'ASCII seed was not measured before Unicode samples']]) {
        const report = build(evidenceWith(id, reads => { reads.seed.rows = ['stale']; }));
        const item = report.cases.find(entry => entry.caseId === id);
        assert.equal(item.pinned.observation.outcome, 'inconclusive');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.pinned.note, note);
        assert.equal(item.pinned.observation.note, note);
    }
    const cup = build(evidenceWith(UTF8, reads => { reads.cup2.cursor = [0, 3]; }));
    assert.equal(cup.cases[0].pinned.note, 'Second target CUP did not reach Unicode sample cell');
    assert.equal(cup.cases[0].conclusion.outcome, 'inconclusive');
    const first = build(evidenceWith(UTF8, reads => { reads.cup1.cursor = [1, 1]; }));
    assert.equal(first.cases[0].pinned.note, 'First target CUP did not reach Unicode sample cell');
});

test('a pinned unsupported over invalid contract evidence is recorded as the contract being more cautious', () => {
    const report = build(evidenceWith(SPECIAL, reads => { reads.target.rows = ['qqq']; reads.initial.rows = ['x']; }));
    const item = report.cases[1];
    assert.equal(item.pinned.observation.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'unsupported', contractOutcome: 'inconclusive', kind: 'contract-more-cautious-than-pinned' });
});

test('the pinned control guards produce the pinned inconclusive shapes (flank, trailing j, A/Z)', () => {
    const flank = build(evidenceWith(SPECIAL, reads => { reads.target.rows = ['──q']; }));
    assert.equal(flank.cases[1].pinned.note, 'ASCII flank control changed after DEC designation');
    assert.equal(flank.cases[1].pinned.observation.outcome, 'inconclusive');
    assert.equal(flank.cases[1].conclusion.outcome, 'unsupported');
    assert.equal(flank.cases[1].divergence.kind, 'contract-measured-mismatch');
    const trailing = build(evidenceWith(LINES, reads => { reads.target.rows = ['┘┐┌└─│┘']; }));
    assert.equal(trailing.cases[2].pinned.note, 'Trailing ASCII j control changed after DEC line drawing');
    const control = build(evidenceWith(G01, reads => { reads.target.rows = ['┌l┌lZ']; }));
    assert.equal(control.cases[3].pinned.note, 'ASCII A/Z control changed after G0/G1 switching');
    const utf8 = build(evidenceWith(UTF8, reads => { reads.target.rows = ['Bé世Z']; }));
    assert.equal(utf8.cases[0].pinned.note, 'ASCII control changed after Unicode samples');
});

test('the pinned replay equals the live pinned shapes over the observed fixture screens', () => {
    for (const contract of charsetsContracts) {
        const screens = syntheticCharsetsScreens(contract.caseId);
        const replay = replayCharsetsPinned(contract, screens);
        assert.equal(replay.pass, true);
        assert.equal(replay.observation.outcome, 'supported');
        assert.equal(replay.assertions[0].expected, contract.pinnedExpected);
        const context = charsetsRecordingContext(contract, screens);
        assert.equal(context.cols, 60);
        assert.equal(context.getScrollback().screenLines, 6);
        assert.throws(() => context.getCell(0, 0), /before its fixture/);
        assert.throws(() => context.getCursor(), /before its fixture/);
        assert.throws(() => context.getText(), /must not read text/);
        context.feed(contract.pinnedFeeds[0]);
        assert.equal(context.getCell(0, 0).char, contract.caseId === UTF8 ? 'A' : contract.caseId === G01 ? 'A' : contract.caseId === SPECIAL ? 'q' : 'j');
        assert.throws(() => context.feed('x'), /differs from the contract/);
        assert.throws(() => charsetsRecordingContext(contract, screens).feed(contract.pinnedFeeds[1]), /differs from the contract/);
        for (const text of contract.pinnedFeeds.slice(1)) context.feed(text);
        assert.equal(context.fedCount, contract.pinnedFeeds.length);
        if (contract.pinnedFinally !== null) {
            assert.equal(context.finalized, false);
            context.feed(contract.pinnedFinally);
            assert.equal(context.finalized, true);
            assert.throws(() => context.feed(contract.pinnedFinally), /differs from the contract/, 'restoration bytes run once');
        } else assert.throws(() => context.feed('\x0F\x1B(B\x1B)B'), /differs from the contract/);
    }
    // The restoration bytes cannot come before any pinned feed.
    assert.throws(() => charsetsRecordingContext(contractOf(SPECIAL), syntheticCharsetsScreens(SPECIAL)).feed('\x0F\x1B(B\x1B)B'), /differs from the contract/);
});

test('the pinned run must replay its complete fixture when decided, and its restoration bytes whenever it fed anything', () => {
    const contract = contractOf(G01);
    const screens = syntheticCharsetsScreens(G01);
    const context = charsetsRecordingContext(contract, screens);
    context.feed(contract.pinnedFeeds[0]);
    assert.throws(() => requirePinnedReplay(contract, context, { observation: { outcome: 'inconclusive' } }), /did not run its restoration bytes/);
    context.feed(contract.pinnedFinally);
    assert.doesNotThrow(() => requirePinnedReplay(contract, context, { observation: { outcome: 'inconclusive' } }));
    assert.throws(() => requirePinnedReplay(contract, context, { observation: { outcome: 'supported' } }), /did not replay its complete fixture/);
    // Geometry exit: nothing fed, nothing to restore.
    assert.doesNotThrow(() => requirePinnedReplay(contract, charsetsRecordingContext(contract, screens), { observation: { outcome: 'inconclusive' } }));
});

test('the pinned geometry gate is the pinned inconclusive shape', () => {
    for (const [contract, cols] of [[contractOf(SPECIAL), 2], [contractOf(LINES), 6], [contractOf(G01), 4], [contractOf(UTF8), 4]]) {
        const narrow = syntheticCharsetsScreens(contract.caseId).map(screen => ({ ...screen, cols }));
        const replay = replayCharsetsPinned(contract, narrow);
        assert.equal(replay.note, contract.pinnedGeometryNote);
        assert.equal(replay.response, JSON.stringify({ cols }));
        assert.equal(replay.observation.outcome, 'inconclusive');
    }
});

test('parserStateResult mirrors the pinned result shapes', () => {
    assert.deepEqual(parserStateResult(true, 'e', { a: 1 }), { pass: true, response: '{"a":1}', observation: { outcome: 'supported', evidence: 'parser-state' },
        assertions: [{ kind: 'positive', expected: 'e', observed: '{"a":1}' }] });
    assert.deepEqual(parserStateResult(null, 'e', { a: 1 }, 'n'), { pass: false, response: '{"a":1}', note: 'n',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'n' } });
    assert.equal(parserStateResult(false, 'e', {}).assertions[0].kind, 'negative');
});

test('tampered pinned results, run logs and identities are refused', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    const mutate = edit => { const copy = clone(evidence); edit(copy); return copy; };
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].pinned.pass = false; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].pinned.observation.outcome = 'unsupported'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[2].pinned.response = copy.run.cases[2].pinned.response.replace('"j"', '"k"'); })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[3].pinned.note = 'edited'; })), /does not match its observed evidence/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[0].feeds[2] = 'x'; })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases[1].feeds[3] = copy.run.cases[1].feeds[3].replace('(0', '(B'); })), /do not match/);
    assert.throws(() => build(mutate(copy => { copy.run.cases.reverse(); })), /case order differs/);
    assert.throws(() => build(mutate(copy => { copy.run.probeIds = [...copy.run.probeIds].reverse(); })), /does not cover/);
    assert.throws(() => build(mutate(copy => { copy.adapter.id = 'other'; })), /Invalid Charsets adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.adapter.derivedFromAdapterId = 'ntilde-owned-sgr-closure-grading-v1'; })), /Invalid Charsets adapter identity/);
    assert.throws(() => build(mutate(copy => { copy.run.adapterId = 'other'; })), /Invalid Charsets adapter identity/);
    assert.throws(() => buildCharsetsGradingReport(evidence.frames, evidence.run, 'd'.repeat(32), evidence.adapter), /not bound to this run/);
});

test('identity mismatch: a pinned result recorded for another case is refused', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    const swapped = clone(evidence);
    [swapped.run.cases[1].pinned, swapped.run.cases[2].pinned] = [swapped.run.cases[2].pinned, swapped.run.cases[1].pinned];
    assert.throws(() => build(swapped), /Missing pinned charsets\.dec-special grading result/);
});

test('mismatched identities: a wrong recorded barrier, cursor or read kind is refused', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    const barrier = clone(evidence);
    barrier.run.cases[0].barriers[3] = '\x1b[?1;2c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    const cursor = clone(evidence);
    cursor.run.cases[0].reads[2].cursor = [0, 3];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    const kind = clone(evidence);
    kind.run.cases[1].reads[0].kind = 'text';
    assert.throws(() => build(kind), /Wrong charsets\.dec-special read kind/);
});

test('stale or edited observations (changed screen with an old digest) are refused', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    const stale = clone(evidence);
    stale.frames[4].screen.lines[0] = 'Axy?X';
    assert.throws(() => build(stale), /Changed Charsets snapshot evidence/);
    const reordered = clone(evidence);
    [reordered.frames[3], reordered.frames[4]] = [reordered.frames[4], reordered.frames[3]];
    assert.throws(() => build(reordered), /Changed Charsets snapshot evidence/);
    const missing = clone(evidence);
    missing.frames.splice(5, 1);
    assert.throws(() => build(missing), /Incomplete Charsets snapshot sequence/);
});

test('an edited cell with a recomputed digest is caught by the pinned agreement, not the digest', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    const copy = clone(evidence);
    // The target read of charsets.utf8 (frame 2 + 5): a pinned result recorded before the edit no longer agrees.
    const frame = copy.frames[2 + 5];
    frame.screen.lines[0] = 'Aé世Y';
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(copy), /does not match its observed evidence/);
});

test('inconsistent observations: lines that contradict the stored cell structure are refused', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    // The wide continuation is flagged but the text lists the wide character twice: the pinned cells no longer agree with the recorded result.
    const doubled = clone(evidence);
    const frame = doubled.frames[2 + 5];
    frame.screen.lines[0] = 'Aé世世Z';
    frame.sha256 = snapshotHash(frame.screen);
    assert.throws(() => build(doubled), /does not match its observed evidence/);
    // Text longer than the physical cells can hold is incoherent on its own.
    const overflow = clone(evidence);
    overflow.frames[2 + 5].screen.lines[0] = 'x'.repeat(61);
    overflow.frames[2 + 5].sha256 = snapshotHash(overflow.frames[2 + 5].screen);
    assert.throws(() => build(overflow), /does not match its physical cells/);
    // Attribute strings were never requested: a frame that carries them cannot be a charset observation.
    const attributes = clone(evidence);
    attributes.frames[4].screen.attributeRuns = [{ count: 360, flags: 0x300, fg: -1, bg: -1 }];
    attributes.frames[4].sha256 = snapshotHash(attributes.frames[4].screen);
    assert.throws(() => build(attributes), /must not carry attribute strings/);
});

test('incomplete restoration of the main screen is refused', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    const copy = clone(evidence);
    const last = copy.frames.at(-1);
    last.screen.lines[0] = 'left over';
    last.sha256 = snapshotHash(last.screen);
    assert.throws(() => build(copy), /was not restored/);
});

test('contract-version pinning and evidence validation entry', () => {
    const evidence = syntheticCharsetsEvidence(runId);
    assert.doesNotThrow(() => validateCharsetsEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => buildCharsetsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { contractsVersion: 2 }), /Unknown charsets contracts revision 2/);
});
