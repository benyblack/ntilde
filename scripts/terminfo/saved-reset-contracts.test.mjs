import assert from 'node:assert/strict';
import { test } from 'node:test';
import { savedResetContracts, savedResetCaseIds, savedResetReset, savedResetContractsVersion, savedResetSuiteId, savedResetEffectContractId,
    savedResetObservation, savedResetObserved, gradeSavedResetCase, savedResetFeeds, savedResetReads, validateSavedResetFeeds, projectSavedResetCells,
    savedResetCellAt, savedResetInk } from './saved-reset-contracts.mjs';
import { sgrAttrsCellAt } from './sgr-attrs-contracts.mjs';
import { syntheticSavedResetReads, syntheticSavedResetScreens, syntheticSavedResetScreen, syntheticSavedResetGrade, put, colorCell,
    paletteBinding, paletteChannel } from './fixtures/saved-reset-evidence.mjs';

const EXIT = 'modes.alt-screen.exit', M47 = 'modes.altscreen-47', M1047 = 'modes.altscreen-1047', SGR = 'reset.sgr', DECALN = 'reset.decaln';
/** The graded outcome of one case after editing its synthetic reads (the other cases stay valid). */
const graded = (caseId, edit, binding) => syntheticSavedResetGrade(caseId, binding, id => {
    const reads = syntheticSavedResetReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const outcome = (caseId, edit) => graded(caseId, edit).outcome;
const note = (caseId, edit) => graded(caseId, edit).note;
const primary = (cursor = [4, 6]) => ({ cells: [...put(0, 0, 'PRIMARY-A'), ...put(2, 0, 'PRIMARY-B')], cursor });

test('the five pinned ids, versions and identities are exact', () => {
    assert.deepEqual([...savedResetCaseIds], [EXIT, M47, M1047, SGR, DECALN]);
    assert.equal(savedResetContractsVersion, 1);
    assert.equal(savedResetSuiteId, 'ntilde-owned-saved-reset-graded-v1');
    assert.equal(savedResetEffectContractId, 'ntilde.saved-reset.effect/v1');
    assert.deepEqual(savedResetContracts.map(contract => contract.probeId), savedResetCaseIds);
    assert.equal(savedResetCaseIds.includes('reset.method'), false, 'reset.method needs a backend reset() API and is outside this batch');
    assert.equal(savedResetCaseIds.includes('scrollback.alt-screen'), false, 'scrollback.alt-screen belongs to the scrolling/history batch');
    assert.equal(savedResetCaseIds.includes('sgr.reset'), false, 'sgr.reset is already qualified and a different case');
});

test('the pinned fixture bytes are replayed exactly, in the pinned order', () => {
    const feeds = Object.fromEntries(savedResetContracts.map(contract => [contract.caseId, contract.pinnedFeeds]));
    assert.deepEqual(feeds[EXIT], ['\x1b[2J\x1b[1;1HPRIMARY-A', '\x1b[3;1HPRIMARY-B', '\x1b[5;7H', '\x1b[?1049h', '\x1b[1;1HALT-MARK', '\x1b[?1049l', '\x1b[?1049l\x1b[0m']);
    assert.deepEqual(feeds[M47], ['\x1b[?47h', '\x1b[?47l']);
    assert.deepEqual(feeds[M1047], ['\x1b[?1047h', '\x1b[?1047l']);
    assert.deepEqual(feeds[SGR], ['\x1b[1;3;7mX\x1b[0mY'], 'reset.sgr runs its own 1;3;7 fixture, not sgr.reset (1;3;4)');
    assert.deepEqual(feeds[DECALN], ['\x1b#8']);
    for (const contract of savedResetContracts) {
        const planned = savedResetFeeds(contract);
        // The pinned feeds appear as one contiguous run of the plan, in order, after the measured reset.
        const start = planned.indexOf(contract.pinnedFeeds[0], 1);
        assert.deepEqual(planned.slice(start, start + contract.pinnedFeeds.length), [...contract.pinnedFeeds], contract.caseId);
        assert.equal(planned[0], savedResetReset);
        assert.equal(contract.roles[0], 'initial');
        assert.equal(savedResetReads(contract), contract.roles.length);
    }
});

test('the measured reset normalizes both banks without RIS and leaves the main screen active', () => {
    assert.ok(savedResetReset.startsWith('\x1b[?1049l'), 'leaves the harness alternate screen first so the pinned bytes meet the main screen');
    assert.ok(savedResetReset.endsWith('\x1b[?47l\x1b[!p\x1b[?5l\x1b[?6l\x1b[?7h\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H'));
    for (const contract of savedResetContracts) {
        for (const feed of savedResetFeeds(contract)) assert.equal(feed.includes('\x1bc'), false, 'RIS is an operation under test elsewhere, never a setup step');
    }
    assert.ok(savedResetContracts.every(contract => savedResetFeeds(contract).length < 40));
});

test('plans stay inside the observer snapshot budget', () => {
    const total = savedResetContracts.reduce((sum, contract) => sum + savedResetReads(contract), 3);
    assert.ok(total <= 64, `${total} frames`);
});

test('every case is supported by valid synthetic observations', () => {
    for (const id of savedResetCaseIds) assert.equal(syntheticSavedResetGrade(id).outcome, 'supported', id);
});

test('the pinned reads map to the screens that follow each pinned feed', () => {
    const exit = savedResetContracts[0];
    assert.deepEqual(exit.roles.slice(0, 6), ['initial', 'seed', 'entered', 'marked', 'exited', 'final']);
    assert.deepEqual({ ...exit.pinnedReads }, { 0: 0, 3: 1, 4: 2, 5: 3, 6: 4, 7: 5 });
    assert.deepEqual({ ...savedResetContracts[1].pinnedReads }, { 0: 0, 1: 1, 2: 2 });
});

test('exit: cursor-only success (position restored, contents gone) is unsupported, never supported', () => {
    const reads = reads_ => { reads_.exited = { cells: [], cursor: [4, 6] }; };
    assert.equal(outcome(EXIT, reads), 'unsupported');
    assert.match(note(EXIT, reads), /came back blank/);
});

test('exit: contents restored but the saved cursor is not is unsupported', () => {
    assert.match(note(EXIT, reads => { reads.exited = primary([0, 0]); }), /saved cursor was not restored: it is at 0,0/);
    assert.equal(outcome(EXIT, reads => { reads.exited = primary([4, 7]); }), 'unsupported');
});

test('exit: alternate contents contaminating the main screen are unsupported', () => {
    assert.match(note(EXIT, reads => { reads.exited = { cells: [...put(0, 0, 'ALT-MARK'), ...put(2, 0, 'PRIMARY-B')], cursor: [4, 6] }; }), /ALT-MARK leaked/);
    assert.match(note(EXIT, reads => { reads.exited = { ...primary(), alt: true }; }), /still active/);
});

test('exit: a stale saved cursor (decoy DECSC position restored) is unsupported', () => {
    assert.match(note(EXIT, reads => { reads.staleExited = primary([1, 1]); }), /stale: exit restored 1,1/);
});

test('exit: stored attributes lost across the roundtrip are an incomplete restoration', () => {
    const plain = { cells: [...put(0, 0, 'STYLED-A'), ...put(2, 0, 'STYLED-B')], cursor: [4, 6] };
    assert.match(note(EXIT, reads => { reads.styledExited = plain; }), /incomplete restoration/);
});

test('exit: both banks sharing contents and accidental main clearing are unsupported', () => {
    assert.match(note(EXIT, reads => { reads.reentry = { ...primary(), alt: true }; }), /same contents/);
    assert.match(note(EXIT, reads => { reads.back = { cells: [], cursor: [4, 6] }; }), /cleared or changed/);
    assert.match(note(EXIT, reads => { reads.final = { cells: [], cursor: [4, 6] }; }), /trailing/);
});

test('exit: invalid setup or controls are inconclusive, never conclusive', () => {
    assert.equal(outcome(EXIT, reads => { reads.seed = primary([0, 0]); }), 'inconclusive');
    assert.equal(outcome(EXIT, reads => { delete reads.seed.cells; reads.seed = { cells: [], cursor: [4, 6] }; }), 'inconclusive');
    assert.equal(outcome(EXIT, reads => { reads.entered = { cells: [], cursor: [0, 0] }; }), 'inconclusive'); // entry did not switch: a no-op transition
    assert.equal(outcome(EXIT, reads => { reads.entered = { cells: put(0, 0, 'PRIMARY-A'), cursor: [0, 0], alt: true }; }), 'inconclusive'); // entry did not clear
    assert.equal(outcome(EXIT, reads => { reads.marked = { cells: [], cursor: [0, 0], alt: true }; }), 'inconclusive');
    assert.equal(outcome(EXIT, reads => { reads.reentry = { cells: [], cursor: [0, 0] }; }), 'inconclusive');
    assert.equal(outcome(EXIT, reads => { reads.staleSeed = primary([0, 0]); }), 'inconclusive');
    assert.equal(outcome(EXIT, reads => { reads.styledSeed = primary(); }), 'inconclusive');
    assert.equal(outcome(EXIT, reads => { reads.initial = primary(); }), 'inconclusive');
    assert.equal(outcome(EXIT, reads => { reads.initial = { cells: [], cursor: [0, 0], buffer: { scrollTop: 1 } }; }), 'inconclusive');
});

test('exit: unrelated state corrupted only in the conditional controls (stale save, styled pen, second switch) is unsupported', () => {
    for (const role of ['staleExited', 'styledExited', 'final', 'back']) {
        for (const extra of [{ buffer: { scrollTop: 1 } }, { buffer: { originMode: true } }, { buffer: { insertMode: true } }, { history: ['stray'] }]) {
            assert.equal(outcome(EXIT, reads => { reads[role] = { ...reads[role], ...extra }; }), 'unsupported', role + JSON.stringify(extra));
        }
    }
    assert.equal(outcome(EXIT, reads => { reads.staleSeed = { ...reads.staleSeed, history: ['stray'] }; }), 'inconclusive');
    assert.equal(outcome(M47, reads => { reads.back = { ...reads.back, buffer: { insertMode: true } }; }), 'unsupported');
});

test('exit: unrelated state changes during the roundtrip are unsupported', () => {
    assert.equal(outcome(EXIT, reads => { reads.exited = { ...primary(), buffer: { originMode: true } }; }), 'unsupported');
    assert.equal(outcome(EXIT, reads => { reads.exited = { ...primary(), history: ['stray'] }; }), 'unsupported');
});

test('?47: a no-op switch, shared bank contents and main clearing are unsupported', () => {
    assert.match(note(M47, reads => { reads.active = { cells: [], cursor: [0, 0] }; }), /no-op/);
    assert.match(note(M47, reads => { reads.active = { cells: put(0, 0, 'MAIN-47'), cursor: [2, 4], alt: true }; }), /share contents/);
    assert.match(note(M47, reads => { reads.exited = { cells: [], cursor: [2, 4] }; }), /main screen was cleared/);
    assert.match(note(M47, reads => { reads.exited = { cells: [...put(0, 0, 'MAIN-47'), ...put(1, 1, 'ALT-47')], cursor: [2, 4] }; }), /leaked/);
    assert.equal(outcome(M47, reads => { reads.exited = { cells: put(0, 0, 'MAIN-47'), cursor: [2, 4], alt: true }; }), 'unsupported');
    assert.match(note(M47, reads => { reads.back = { cells: [], cursor: [2, 4] }; }), /second screen switch/);
});

test('?47 retains the alternate buffer across exit; ?1047 clears it on exit (distinct semantics)', () => {
    assert.match(note(M47, reads => { reads.reentry = { cells: [], cursor: [0, 0], alt: true }; }), /not retained/);
    assert.match(note(M1047, reads => { reads.reentry = { cells: put(1, 1, 'ALT-1047'), cursor: [1, 9], alt: true }; }), /not cleared on exit/);
    // Each keeps its own semantics: ?1047 retaining is wrong, and ?47 clearing is wrong.
    assert.equal(outcome(M1047, reads => { reads.reentry = { cells: put(1, 1, 'ALT-1047'), cursor: [1, 9], alt: true }; }), 'unsupported');
    assert.equal(outcome(M47, reads => { reads.reentry = { cells: [], cursor: [1, 7], alt: true }; }), 'unsupported');
});

test('?47 and ?1047: invalid seeds and controls are inconclusive', () => {
    for (const id of [M47, M1047]) {
        assert.equal(outcome(id, reads => { reads.seed = { cells: [], cursor: [2, 4] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.marked = { cells: [], cursor: [0, 0], alt: true }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.reentry = { cells: [], cursor: [0, 0] }; }), 'inconclusive', id);
        assert.equal(outcome(id, reads => { reads.initial = { cells: put(0, 0, 'X'), cursor: [0, 1] }; }), 'inconclusive', id);
    }
    assert.equal(outcome(M1047, reads => { reads.preSeed = { cells: put(0, 0, 'JUNK'), cursor: [0, 0] }; }), 'inconclusive');
});

test('reset.sgr: styles active on X and cleared on Y; any remaining styled field on Y is unsupported', () => {
    const pair = (x, y) => ({ cells: [...put(0, 0, 'X', x), ...put(0, 1, 'Y', y)], cursor: [0, 2] });
    const styled = { attrs: ['bold', 'italic', 'inverse'] };
    assert.equal(outcome(SGR, reads => { reads.pinned = pair(styled, { attrs: ['bold'] }); }), 'unsupported');
    assert.equal(outcome(SGR, reads => { reads.pinned = pair(styled, { attrs: ['italic'] }); }), 'unsupported');
    assert.equal(outcome(SGR, reads => { reads.pinned = pair(styled, { attrs: ['inverse'] }); }), 'unsupported');
    // Damage the pinned criterion cannot see: a stuck underline, color or overline on Y.
    assert.equal(outcome(SGR, reads => { reads.pinned = pair(styled, { attrs: ['underline'], underlineStyle: 1 }); }), 'unsupported');
    assert.equal(outcome(SGR, reads => { reads.pinned = pair(styled, { fg: paletteChannel(1) }); }), 'unsupported');
    assert.equal(outcome(SGR, reads => { reads.full = pair({ attrs: ['bold', 'italic', 'inverse', 'underline', 'overline'], underlineStyle: 1, underlineColorIndex: 3 }, { attrs: ['overline'] }); }), 'inconclusive');
});

test('reset.sgr: unrelated state changed by the full-tuple reset is unsupported (cursor, buffer, margins, modes, history, extra cells)', () => {
    const withState = extra => reads => { reads.full = { ...reads.full, ...extra(reads.full) }; };
    assert.match(note(SGR, withState(item => ({ cursor: [3, 3] }))), /unrelated state/);
    assert.match(note(SGR, withState(() => ({ alt: true }))), /unrelated state/);
    assert.match(note(SGR, withState(() => ({ buffer: { scrollTop: 1 } }))), /unrelated state/);
    assert.match(note(SGR, withState(() => ({ buffer: { originMode: true } }))), /unrelated state/);
    assert.match(note(SGR, withState(() => ({ buffer: { insertMode: true } }))), /unrelated state/);
    assert.match(note(SGR, withState(() => ({ history: ['stray'] }))), /unrelated state/);
    assert.match(note(SGR, withState(item => ({ cells: [...item.cells, ...put(5, 5, 'Z')] }))), /unrelated state/);
    assert.equal(outcome(SGR, withState(() => ({}))), 'supported');
    // The no-reset and spelling controls must be contained too, or the run is inconclusive.
    assert.equal(outcome(SGR, reads => { reads.persist = { ...reads.persist, cursor: [2, 2] }; }), 'inconclusive');
    assert.equal(outcome(SGR, reads => { reads.omitted = { ...reads.omitted, history: ['stray'] }; }), 'inconclusive');
});

test('reset.sgr: every attribute of the full-style control must be stored on X, or the control is inconclusive', () => {
    const all = ['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline', 'underline'];
    for (const missing of ['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline']) {
        const x = {
            attrs: all.filter(name => name !== missing), underlineStyle: 1, underlineColorIndex: 3,
            fg: { kind: 'literal', rgb: { r: 1, g: 2, b: 3 } }, bg: { kind: 'literal', rgb: { r: 4, g: 5, b: 6 } },
        };
        assert.equal(outcome(SGR, reads => { reads.full = { cells: [...put(0, 0, 'X', x), ...put(0, 1, 'Y', {})], cursor: [0, 2] }; }), 'inconclusive', missing);
    }
});

test('reset.sgr: a missing or wrong styled control is inconclusive, and the reset is attributed only when styles persist without it', () => {
    const pair = (x, y) => ({ cells: [...put(0, 0, 'X', x), ...put(0, 1, 'Y', y)], cursor: [0, 2] });
    assert.equal(outcome(SGR, reads => { reads.pinned = pair({ attrs: ['bold', 'italic'] }, {}); }), 'inconclusive', 'inverse missing on X');
    assert.equal(outcome(SGR, reads => { reads.pinned = pair({}, {}); }), 'inconclusive', 'X never styled');
    assert.equal(outcome(SGR, reads => { reads.persist = pair({ attrs: ['bold', 'italic', 'inverse'] }, {}); }), 'inconclusive', 'the pen did not persist without a reset');
    assert.equal(outcome(SGR, reads => { reads.pinned = { ...reads.pinned, cursor: [0, 5] }; }), 'inconclusive', 'the cursor moved beyond the two cells');
    assert.equal(outcome(SGR, reads => { reads.pinned = { cells: [...reads.pinned.cells, ...put(3, 3, 'Z')], cursor: [0, 2] }; }), 'inconclusive', 'extra ink');
    assert.equal(outcome(SGR, reads => { reads.full = pair({ attrs: ['bold'] }, {}); }), 'inconclusive', 'the full-tuple control stored too little');
});

test('reset.decaln: no E in the stored cell is unsupported; a partial alignment is a recorded refinement failure', () => {
    assert.equal(outcome(DECALN, reads => { reads.pinned = { cells: [], cursor: [0, 0] }; }), 'unsupported');
    assert.match(note(DECALN, reads => { reads.pinned = { cells: put(0, 0, 'E'), cursor: [0, 0] }; }), /full grid/);
    const partial = graded(DECALN, reads => { reads.aligned = { cells: put(0, 0, 'E'), cursor: [0, 0] }; });
    assert.equal(partial.outcome, 'unsupported');
    assert.deepEqual(partial.layers.map(layer => [layer.name, layer.outcome]), [['pinned-cell-0-0', 'supported'], ['full-grid-refinement', 'unsupported']]);
    assert.equal(outcome(DECALN, reads => { reads.aligned = { ...reads.aligned, cursor: [3, 3] }; }), 'unsupported');
    assert.equal(outcome(DECALN, reads => { reads.aligned = { cells: [...put(0, 0, 'ABABABABABAB'), ...put(1, 0, 'BABABABABABA'), ...put(2, 0, 'ABABABABABAB')], cursor: [0, 0] }; }), 'unsupported');
});

test('reset.decaln: an unmeasured seed grid is inconclusive; the cursor-only control must not already hold E', () => {
    assert.equal(outcome(DECALN, reads => { reads.seed = { cells: [], cursor: [0, 0] }; }), 'inconclusive');
    assert.equal(outcome(DECALN, reads => { reads.seed = { cells: put(0, 0, 'E'), cursor: [0, 0] }; }), 'inconclusive');
    assert.equal(outcome(DECALN, reads => { reads.initial = { cells: put(0, 0, 'E'), cursor: [0, 1] }; }), 'inconclusive');
});

test('DECSCNM on is never graded and a too-small screen is inconclusive', () => {
    for (const id of savedResetCaseIds) {
        assert.equal(graded(id, null, { ...paletteBinding(), reverseVideo: true }).outcome, 'inconclusive', id);
    }
});

test('observed records keep the pinned inconclusive-by-design cases separate from effect observations', () => {
    const observed = Object.fromEntries(savedResetContracts.map(contract => {
        const screens = syntheticSavedResetScreens(contract.caseId);
        return [contract.caseId, savedResetObserved(contract, savedResetObservation(contract, screens))];
    }));
    assert.equal(observed[EXIT].observations.alternateRetainedAfter1049Exit, true);
    assert.deepEqual(observed[EXIT].observations.savedPenWitness, { char: 'W', bold: true, fgIndex: 4, restored: true });
    assert.equal(observed[M1047].observations.retainsAlternateContentsOnEntry, true);
    assert.equal(observed[SGR].observations.omittedSpellingClearsY, true);
    assert.equal(observed[DECALN].observations.gridCoversScreen, true);
    assert.deepEqual(observed[DECALN].observations.marginsAfterDecalnWithRegion, [2, 5]);
    assert.deepEqual(observed[EXIT].roles.seed.ink, [{ row: 0, cols: [0, 8], text: 'PRIMARY-A', stored: 'neutral' }, { row: 2, cols: [0, 8], text: 'PRIMARY-B', stored: 'neutral' }]);
    // A full DECALN grid collapses to one run per row.
    assert.equal(observed[DECALN].roles.aligned.ink.length, 10);
});

test('the independent grader refuses unknown revisions and incomplete observations', () => {
    const contract = savedResetContracts[1];
    const observation = savedResetObservation(contract, syntheticSavedResetScreens(contract.caseId));
    assert.throws(() => gradeSavedResetCase(contract, observation, 2), /Unknown saved-state contracts revision 2/);
    const { seed: _seed, ...missing } = observation;
    assert.throws(() => gradeSavedResetCase(contract, missing), /Missing modes\.altscreen-47 read captures/);
    assert.throws(() => savedResetObservation(contract, []), /Missing saved-state read captures/);
    assert.throws(() => validateSavedResetFeeds(contract, ['\x1b[?47h']), /do not match/);
    assert.doesNotThrow(() => validateSavedResetFeeds(contract, savedResetFeeds(contract)));
});

test('the fast projector equals the pinned per-cell projection and refuses contradictory evidence', () => {
    const screen = syntheticSavedResetScreens(EXIT)[11];
    const cellAt = projectSavedResetCells(screen);
    for (const [row, col] of [[0, 0], [0, 7], [2, 3], [4, 6], [9, 59]]) assert.deepEqual(cellAt(row, col), sgrAttrsCellAt(screen, row, col));
    assert.deepEqual(savedResetCellAt(screen, 0, 0), sgrAttrsCellAt(screen, 0, 0));
    assert.equal(savedResetInk(screen).length, 16);
    const contradictory = JSON.parse(JSON.stringify(screen));
    contradictory.attributeRuns[0].flags |= 0x0040;
    assert.throws(() => projectSavedResetCells(contradictory)(0, 0), /Contradictory stored cell projections/);
    assert.throws(() => savedResetInk(contradictory), /Contradictory stored cell projections/);
    const bare = { ...screen }; delete bare.bufferState;
    assert.throws(() => savedResetInk(bare), /buffer state and history/);
    assert.equal(syntheticSavedResetScreen({ cells: [{ row: 0, col: 0, spec: colorCell('Q') }] }).lines[0], 'Q');
});
