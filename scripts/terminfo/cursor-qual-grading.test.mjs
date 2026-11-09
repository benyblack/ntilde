import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { buildCursorQualGradingReport, cursorShapeOf, pinnedCallbackDigests, replayCursorQualPinned, validateCursorQualEvidence } from './cursor-qual-grading.mjs';
import { correctBehavior, syntheticCursorEvidence, writeCursorPngs, cellWidth, cellHeight, cloneEvidence } from './fixtures/cursor-qual-evidence.mjs';

const build = (evidence, options = {}) => buildCursorQualGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { imageOf: evidence.imageOf, ...options });
const outcomes = report => Object.fromEntries(report.cases.map(item => [item.caseId, item.conclusion.outcome]));
const withBehavior = behavior => build(syntheticCursorEvidence('a'.repeat(32), 'f'.repeat(64), { ...correctBehavior, ...behavior }));
const only = (report, id) => report.cases.find(item => item.caseId === id);
/** Rehash a frame after a test changed its screen, so the journal stays internally consistent and only the semantics are wrong. */
const rehash = frame => { frame.sha256 = snapshotHash(frame.screen); };

test('a correct terminal qualifies all four cases, and every pinned path is replayed and reconciled', () => {
    const report = build(syntheticCursorEvidence());
    assert.deepEqual(outcomes(report), { 'cursor.cup-scroll-region': 'supported', 'cursor.hide': 'supported', 'cursor.shape': 'supported', 'cursor.reverse-wrap': 'supported' });
    for (const item of report.cases) {
        assert.equal(item.pinned.observation.outcome, 'supported', `${item.caseId} termless`);
        assert.equal(item.conclusion.grading, 'pinned-termless-plus-contract-v1');
        assert.ok(item.contractResult.layers.includes('physical cursor'));
    }
    // The three pinned app callbacks that cannot decide stay inconclusive; the contract's measurement beyond them is an explicit divergence.
    for (const id of ['cursor.cup-scroll-region', 'cursor.hide', 'cursor.shape']) {
        assert.equal(only(report, id).pinnedApp.observation.outcome, 'inconclusive');
        assert.equal(only(report, id).divergence, undefined, 'the termless probe the inventory reads as the pinned verdict agrees with the contract');
        assert.equal(only(report, id).appDivergence.kind, 'contract-measures-beyond-pinned');
        assert.equal(only(report, id).appDivergence.against, 'pinned-app');
    }
    assert.equal(only(report, 'cursor.reverse-wrap').pinnedApp.observation.outcome, 'supported');
    assert.equal(only(report, 'cursor.reverse-wrap').divergence, undefined);
    assert.match(only(report, 'cursor.cup-scroll-region').pinnedApp.observation.note, /measured 1;1, 2;1, 11;1 — the physical row 5 and the margins still need an independent observable/);
    assert.match(only(report, 'cursor.hide').pinnedApp.observation.note, /control must visibly show the cursor/);
    assert.deepEqual(only(report, 'cursor.shape').pinnedApp.observation.frames.map(item => item.label), ['Default cursor shape', 'Bar cursor shape']);
    assert.deepEqual(report.cases.map(item => item.conclusion.evidence), ['owned-stored-physical-cursor-and-cpr', 'owned-stored-cursor-and-controlled-pixels', 'owned-stored-cursor-and-controlled-pixels', 'owned-stored-physical-cursor-and-cpr']);
});

test('the raw CPR replies are retained beside the physical cursor and the reply is region-relative under DECOM', () => {
    const report = build(syntheticCursorEvidence());
    const cup = only(report, 'cursor.cup-scroll-region');
    assert.deepEqual(cup.reads.filter(read => read.kind === 'cpr').slice(0, 3).map(read => [read.cursor, read.rawCpr]),
        [[[4, 0], '\x1b[1;1R'], [[5, 0], '\x1b[2;1R'], [[14, 0], '\x1b[11;1R']]);
    assert.deepEqual(cup.pinnedApp.observation.evidence, 'query');
    assert.equal(cup.observed.groups.find(group => group.id === 'app').reads[0].cprConsistentWithPhysicalCursor, true);
});

test('relative/absolute coordinate confusion: an absolute CPR under DECOM voids the measurement instead of being read as a physical row', () => {
    const report = withBehavior({ cprRelativeUnderOrigin: false });
    const cup = only(report, 'cursor.cup-scroll-region');
    assert.equal(cup.conclusion.outcome, 'inconclusive');
    assert.match(cup.contractResult.note, /differs from the region-relative report 1;1 the observed physical cursor implies/);
    // The physical rows are unchanged evidence: only the independent report disagrees.
    assert.equal(cup.pinned.observation.outcome, 'supported');
});

test('a terminal that ignores DECOM for CUP lands at the wrong physical row and is unsupported, not inconclusive', () => {
    const cup = only(withBehavior({ cupFollowsOrigin: false }), 'cursor.cup-scroll-region');
    assert.equal(cup.conclusion.outcome, 'unsupported');
    assert.equal(cup.pinned.observation.outcome, 'unsupported');
    assert.equal(cup.divergence, undefined, 'both the termless probe and the contract say unsupported');
    assert.equal(cup.appDivergence.against, 'pinned-app');
    assert.equal(cup.appDivergence.kind, 'contract-measures-beyond-pinned');
});

test('incorrect margins are a measured mismatch even though the DECOM controls reuse DECSTBM', () => {
    const cup = only(withBehavior({ marginShift: 1 }), 'cursor.cup-scroll-region');
    assert.equal(cup.conclusion.outcome, 'unsupported');
    assert.match(cup.contractResult.note, /Margins 5;16 differ from the contracted 5;15/);
});

test('missing visibility: a terminal whose DECTCEM reset is not stored is unsupported; a stored flag without the paint change cannot qualify rendering', () => {
    assert.equal(only(withBehavior({ hideStored: false }), 'cursor.hide').conclusion.outcome, 'unsupported');
    const storedOnly = only(withBehavior({ hidePaints: false }), 'cursor.hide');
    assert.equal(storedOnly.pinned.observation.outcome, 'supported', 'the pinned termless flag alone says supported');
    assert.equal(storedOnly.conclusion.outcome, 'unsupported', 'but the controlled pixels still show the cursor');
    assert.match(storedOnly.contractResult.note, /pixels\/hide.target-cell-blank/);
    assert.equal(storedOnly.divergence.kind, 'contract-stricter-than-pinned');
    assert.deepEqual(storedOnly.divergences.map(item => item.against), ['pinned-termless', 'pinned-app']);
});

test('wrong shapes: CSI 5 SP q stored as a block is unsupported; a bar rendered as a block voids the reference controls', () => {
    const wrongStored = only(withBehavior({ shapeOfFive: 'block' }), 'cursor.shape');
    assert.equal(wrongStored.conclusion.outcome, 'unsupported');
    assert.equal(wrongStored.pinned.observation.outcome, 'supported', 'CSI 6 SP q is still a bar');
    assert.equal(wrongStored.divergence.kind, 'contract-stricter-than-pinned');
    const wrongPaint = only(withBehavior({ paintsBar: false }), 'cursor.shape');
    assert.equal(wrongPaint.conclusion.outcome, 'inconclusive');
    assert.match(wrongPaint.contractResult.note, /shape.reference-shapes/);
});

test('blink stays separate from shape and visibility: a bar that never turns off in the hidden half-period is unsupported', () => {
    const shape = only(withBehavior({ blinkAware: false }), 'cursor.shape');
    assert.equal(shape.conclusion.outcome, 'unsupported');
    assert.match(shape.contractResult.note, /shape.target-blink-hidden-phase/);
});

test('default versus explicit intent: CSI 0 q leaving an explicit copy of the default is a restoration failure the journal refuses', () => {
    assert.throws(() => withBehavior({ zeroQIsExplicit: true }), /fixture was not restored/);
});

test('a terminal without ?45 is unsupported with valid controls, in both pinned paths and the contract', () => {
    const wrap = only(withBehavior({ reverseWrap: false }), 'cursor.reverse-wrap');
    assert.equal(wrap.conclusion.outcome, 'unsupported');
    assert.equal(wrap.pinned.observation.outcome, 'unsupported');
    assert.equal(wrap.pinnedApp.observation.outcome, 'unsupported');
    assert.equal(wrap.divergence, undefined);
    assert.match(wrap.pinnedApp.observation.note, /Measured wrap was not reversed to row 1 column 60/);
    assert.deepEqual(wrap.observed.groups.find(group => group.id === 'mode-off').reads.map(read => read.matchesExpectation), [true, true, true]);
});

test('a false reverse-wrap positive is refused: wrapping with the mode off makes the control fail, so the movement is not attributable to ?45', () => {
    const wrap = only(withBehavior({ reverseWrapIgnoresMode: true }), 'cursor.reverse-wrap');
    assert.equal(wrap.pinned.observation.outcome, 'supported', 'the pinned fixture alone cannot tell');
    assert.equal(wrap.conclusion.outcome, 'inconclusive');
    assert.match(wrap.contractResult.note, /mode-off.*stops at column 1 of row 2/);
});

test('incomplete restoration prevents a conclusive grade: terminal-wide drift is refused and a single unrestored read blocks the verdict', () => {
    assert.throws(() => withBehavior({ ignoreOriginReset: true }), /fixture was not restored/);
    const evidence = syntheticCursorEvidence();
    const contracts = validateCursorQualEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    // The first read of the cup termless group that follows the pinned cleanup is its restoration read.
    const restoration = 2 + 1;
    const frame = evidence.frames[restoration];
    frame.screen.bufferState.originMode = true; rehash(frame);
    const cup = only(build(evidence), contracts[0].caseId);
    assert.equal(cup.conclusion.outcome, 'inconclusive');
    assert.match(cup.contractResult.note, /origin mode and margins restored|Origin mode is set/);
    assert.equal(cup.contractResult.outcome, 'inconclusive');
});

test('a missing CPR reply is an explicit absence that voids the report cross-check', () => {
    const report = withBehavior({ noCpr: true });
    assert.equal(only(report, 'cursor.cup-scroll-region').conclusion.outcome, 'inconclusive');
    assert.match(only(report, 'cursor.cup-scroll-region').contractResult.note, /No cursor position report was received/);
    assert.equal(only(report, 'cursor.cup-scroll-region').pinnedApp.observation.reason, 'no-response');
});

test('the original UndeclaredTerminalGeometry failures and the repair record are retained for reverse-wrap and shape only', () => {
    const report = build(syntheticCursorEvidence());
    for (const id of ['cursor.reverse-wrap', 'cursor.shape']) {
        const item = only(report, id);
        assert.equal(item.pinnedApp.repaired, true);
        assert.equal(item.geometryRepair.original.threw.name, 'UndeclaredTerminalGeometry');
        assert.deepEqual(item.geometryRepair.geometry, { rows: 24, cols: 60 });
        assert.deepEqual(item.geometryRepair.geometryBefore, item.geometryRepair.geometryAfter);
        assert.equal(item.geometryRepair.repair.repairedTermNeedsGeometry, true);
        assert.equal(item.geometryRepair.repair.termSha256, pinnedCallbackDigests[id].termSha256);
    }
    assert.deepEqual(only(report, 'cursor.reverse-wrap').geometryRepair.original.writes, [], 'the original callback fails before its first byte');
    assert.deepEqual(only(report, 'cursor.shape').geometryRepair.original.writes, ['\x1b[5 q', '\x1b[0 q'], 'the original shape callback fails after its captures and cleanup');
    assert.equal(only(report, 'cursor.hide').geometryRepair, undefined);
    assert.equal(only(report, 'cursor.hide').pinnedApp.repaired, false);
});

test('the pinned port results are re-derived from the retained observations and tampered pinned results are refused', () => {
    const evidence = syntheticCursorEvidence();
    const clone = () => cloneEvidence(evidence);
    let tampered = clone();
    tampered.run.cases[1].pinned.termless.pass = !tampered.run.cases[1].pinned.termless.pass;
    assert.throws(() => build(tampered), /Pinned cursor.hide grading does not match its observed evidence/);
    tampered = clone();
    tampered.run.cases[0].pinned.app.observation.note += ' (edited)';
    assert.throws(() => build(tampered), /Pinned cursor.cup-scroll-region grading does not match/);
    tampered = clone();
    tampered.run.cases[3].pinned.app.writes = ['\x1b[?45h'];
    assert.throws(() => build(tampered), /app writes do not match|grading does not match/);
});

test('the geometry repair is refused unless the original failure, the exact metadata change and the measured geometry are all retained', () => {
    const evidence = syntheticCursorEvidence();
    const mutate = (edit, pattern) => {
        const copy = cloneEvidence(evidence);
        edit(copy.run.cases[3].pinned);
        assert.throws(() => build(copy), pattern);
    };
    mutate(pinned => { delete pinned.original; }, /original cursor.reverse-wrap failure was not retained/);
    mutate(pinned => { pinned.original.threw.name = 'Error'; }, /original cursor.reverse-wrap failure was not retained/);
    mutate(pinned => { pinned.original.writes = ['\x1b[?45h']; }, /original cursor.reverse-wrap failure was not retained/);
    mutate(pinned => { pinned.app.geometry = { rows: 1, cols: 80 }; }, /original cursor.reverse-wrap failure was not retained/);
    mutate(pinned => { pinned.repair.repairedTermNeedsGeometry = false; }, /geometry repair record changed/);
    mutate(pinned => { pinned.repair.termSha256 = '0'.repeat(64); }, /geometry repair record changed/);
    mutate(pinned => { pinned.repair.sameTerm = false; }, /geometry repair record changed/);
    mutate(pinned => { pinned.app.repaired = false; }, /original cursor.reverse-wrap failure was not retained/);
    const unrepaired = cloneEvidence(evidence);
    unrepaired.run.cases[1].pinned.original = unrepaired.run.cases[3].pinned.original;
    assert.throws(() => build(unrepaired), /Unexpected cursor.hide geometry repair evidence/);
});

test('changed, absent or drifting geometry cannot be graded', () => {
    const evidence = syntheticCursorEvidence();
    const resized = cloneEvidence(evidence);
    resized.frames[5].screen.rows = 25;
    assert.throws(() => build(resized), /Changed Cursor snapshot evidence|Invalid or unsupported observer geometry/);
    const small = cloneEvidence(evidence);
    for (const frame of small.frames) { frame.screen.rows = 5; frame.screen.lines = frame.screen.lines.slice(0, 5); }
    assert.throws(() => build(small), /at least 15 rows/);
    const missing = cloneEvidence(evidence);
    missing.frames[0].screen.rows = undefined;
    assert.throws(() => build(missing), /at least 15 rows|Invalid or unsupported/);
});

test('stale observations, wrong bytes, wrong barriers and a wrong cursor are refused', () => {
    const evidence = syntheticCursorEvidence();
    const clone = () => cloneEvidence(evidence);
    let stale = clone();
    stale.frames[10].screen = structuredClone(stale.frames[9].screen); rehash(stale.frames[10]);
    assert.throws(() => build(stale), /cursor differs from its read frame|barrier differs/);
    let bytes = clone();
    bytes.run.cases[0].feeds[2] = '\x1b[?6l';
    assert.throws(() => build(bytes), /feeds do not match the pinned contract/);
    let barrier = clone();
    barrier.run.cases[1].reads[0].barrier = '\x1b[?1;1c';
    assert.throws(() => build(barrier), /barrier differs from its read frame/);
    let cursor = clone();
    cursor.run.cases[2].reads[2].cursor = [9, 9];
    assert.throws(() => build(cursor), /cursor differs from its read frame/);
    let kind = clone();
    kind.run.cases[0].reads[0].kind = 'cpr';
    assert.throws(() => build(kind), /Wrong cursor.cup-scroll-region read kind/);
    let order = clone();
    [order.run.cases[0], order.run.cases[1]] = [order.run.cases[1], order.run.cases[0]];
    assert.throws(() => build(order), /case order differs/);
});

test('retained CPR exchanges are bound to their read frame and to their raw bytes', () => {
    const evidence = syntheticCursorEvidence();
    const raw = cloneEvidence(evidence);
    raw.run.cases[0].reads.find(read => read.kind === 'cpr').cpr.reply.raw = '\x1b[9;9R';
    assert.throws(() => build(raw), /disagrees with its raw bytes/);
    const swapped = cloneEvidence(evidence);
    const index = swapped.frames.findIndex(frame => frame.cpr);
    swapped.frames[index].cpr = { barrier: '\x1b[?62;4;22c', reply: { raw: '\x1b[2;2R', row: 2, col: 2 } };
    assert.throws(() => build(swapped), /report differs from its read frame/);
    const stray = cloneEvidence(evidence);
    stray.frames[evidence.frames.findIndex(frame => frame.kind === 'curq-read' && !frame.cpr && !frame.pixel)].cpr = swapped.frames[index].cpr;
    assert.throws(() => build(stray), /Unexpected cursor.* cursor report|differs from its read frame/);
});

test('retained captures are bound to the recorded digest and presentation phase', () => {
    const evidence = syntheticCursorEvidence();
    const digest = cloneEvidence(evidence);
    digest.run.cases[1].reads.find(read => read.kind === 'capture').pixelSha256 = '0'.repeat(64);
    assert.throws(() => build(digest), /capture differs from its read frame/);
    const phase = cloneEvidence(evidence);
    phase.frames.find(frame => frame.pixel?.presentationTimeMs === 530).pixel.presentationTimeMs = 0;
    assert.throws(() => build(phase), /capture differs from its read frame/);
    const stray = cloneEvidence(evidence);
    stray.run.cases[0].reads[0].pixelSha256 = 'a'.repeat(64);
    assert.throws(() => build(stray), /Unexpected cursor.cup-scroll-region cursor capture/);
});

test('adapter identity, run identity and the nonce binding are enforced', () => {
    const evidence = syntheticCursorEvidence();
    for (const edit of [copy => { copy.adapter.id = 'ntilde-owned-scrollback-grading-v1'; }, copy => { copy.adapter.derivedFromAdapterId = 'x'; }, copy => { copy.adapter.bundleSha256 = 'f'; },
        copy => { copy.run.adapterId = 'other'; }, copy => { copy.run.geometryRepairId = 'other'; }]) {
        const copy = cloneEvidence(evidence); edit(copy);
        assert.throws(() => build(copy), /Invalid Cursor adapter identity/);
    }
    const run = cloneEvidence(evidence); run.run.probeIds.pop();
    assert.throws(() => build(run), /does not cover the contracted cases/);
    assert.throws(() => buildCursorQualGradingReport(evidence.frames, evidence.run, 'b'.repeat(32), evidence.adapter, { imageOf: evidence.imageOf }), /not bound to this run/);
    const frames = cloneEvidence(evidence); frames.frames.pop();
    assert.throws(() => build(frames), /Incomplete Cursor snapshot sequence/);
});

test('an unrestored fixture is refused: the finish frame must equal the start frame', () => {
    const evidence = syntheticCursorEvidence();
    const last = evidence.frames.at(-1);
    last.screen = structuredClone(last.screen); last.screen.cursorVisible = false; rehash(last);
    assert.throws(() => build(evidence), /fixture was not restored/);
});

test('the controlled PNGs are re-read from retained bytes and must match their recorded digests', () => {
    const evidence = syntheticCursorEvidence();
    const output = mkdtempSync(join(tmpdir(), 'cq-grading-'));
    try {
        writeCursorPngs(evidence, output);
        const report = buildCursorQualGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { output });
        assert.deepEqual(outcomes(report), outcomes(build(evidence)));
        const [file] = [...evidence.pngs.keys()];
        writeFileSync(join(output, file), Buffer.concat([evidence.pngs.get(file), Buffer.from([0])]));
        assert.throws(() => buildCursorQualGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter, { output }), /Invalid or changed controlled pixel capture/);
    } finally { rmSync(output, { recursive: true, force: true }); }
});

test('cursor shapes are classified from the painted ink, not from the stored mode', () => {
    const grid = { cellWidthPx: cellWidth, cellHeightPx: cellHeight };
    const background = [0, 0, 0, 255], ink = [255, 255, 255, 255];
    const cell = paint => {
        const bytes = Buffer.alloc(cellWidth * cellHeight * 4);
        for (let y = 0; y < cellHeight; y++) for (let x = 0; x < cellWidth; x++) Buffer.from(paint(x, y) ? ink : background).copy(bytes, (y * cellWidth + x) * 4);
        return bytes;
    };
    assert.equal(cursorShapeOf(cell(() => false), grid, background), 'none');
    assert.equal(cursorShapeOf(cell(() => true), grid, background), 'block');
    assert.equal(cursorShapeOf(cell((x, y) => y >= cellHeight - 3 && y < cellHeight - 1), grid, background), 'underline');
    assert.equal(cursorShapeOf(cell(x => x === 0), grid, background), 'bar');
    assert.equal(cursorShapeOf(cell(x => x === 4), grid, background), 'other', 'a bar in the middle of the cell is not the left-aligned beam');
    assert.equal(cursorShapeOf(cell((x, y) => (x + y) % 2 === 0), grid, background), 'other');
});

test('validation exposes the contracts that the measured start frame implies', () => {
    const evidence = syntheticCursorEvidence();
    assert.deepEqual(validateCursorQualEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter).map(contract => contract.caseId),
        ['cursor.cup-scroll-region', 'cursor.hide', 'cursor.shape', 'cursor.reverse-wrap']);
    const screens = evidence.frames.slice(2, -1).map(frame => frame.screen);
    assert.throws(() => replayCursorQualPinned(syntheticCursorEvidence().run && validateCursorQualEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter)[0], screens.slice(0, 3), evidence.run.cases[0].reads.slice(0, 3)), /./);
});
