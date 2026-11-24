import assert from 'node:assert/strict';
import { test } from 'node:test';
import { dslContracts, dslCaseIds, dslFeeds, dslObservation, gradeDslCase, dslFinish, dslFixture, dslPinnedScripts, dslPlanSeed, dslReads, dslBurstLines, dslPauseMs, dslSmoothLinesPerSecond, dslTimingBoundMs,
    pinnedModeState, replayDslTermless, replayDslTerminal, validateDslFeeds } from './dsl-contracts.mjs';
import { burstRoles, colorCell, dslBurstRows, literalChannel, dslSeedRows, resizedDslScreen, syntheticDslGrade, syntheticDslScreens, timingOf } from './fixtures/dsl-evidence.mjs';
import { syntheticColEditScreen } from './fixtures/col-edit-evidence.mjs';
import { alternate, seedRows } from './fixtures/devq-evidence.mjs';

const [contract] = dslContracts;
const caseId = contract.caseId;
// The hand-written literals the contract must agree with (an independent spelling of the same bytes).
const literal = { set: '\x1b[?4h', reset: '\x1b[?4l', query: '\x1b[?4$p', four: '\x1b[?4;0$y', witness: 'W\x1b[D', place: '\x1b[4;7H', bottom: '\x1b[6;1H' };

test('the case is the pinned modes.decsclm and its fixtures are written out independently', () => {
    assert.deepEqual([...dslCaseIds], ['modes.decsclm']);
    assert.equal(dslFixture.setFour, literal.set);
    assert.equal(dslFixture.resetFour, literal.reset);
    assert.equal(dslFixture.queryFour, literal.query);
    assert.equal(dslFixture.notRecognizedFour, literal.four);
    assert.equal(dslBurstLines, 40);
});

test('the burst outlasts smooth scrolling by twice the bound and the held exchange outlasts the bound', () => {
    assert.equal(dslSmoothLinesPerSecond, 6);
    assert.ok(dslBurstLines / dslSmoothLinesPerSecond * 1000 >= 2 * dslTimingBoundMs, 'forty lines at six lines per second take 6.6 seconds, over twice the 2 second bound');
    assert.ok(dslPauseMs > dslTimingBoundMs);
    const burst = contract.groups.find(group => group.role === 'burst-set').request;
    assert.equal(burst, literal.bottom + literal.set + '\n'.repeat(40) + literal.query + literal.witness, 'the burst is the cursor on the bottom margin row, the mode, forty line feeds and the query');
});

test('the pinned scripts name exactly the calls the pinned code makes', () => {
    const script = dslPinnedScripts[caseId];
    assert.deepEqual(script.termless, [['feed', literal.set], ['feedCapture', literal.query, 'replies'], ['feed', literal.reset]]);
    assert.deepEqual(script.terminal, [['queryMode', 4]]);
    assert.deepEqual([...contract.pinnedFeeds], [literal.set, literal.query, literal.reset]);
    assert.equal(contract.groups[contract.pinnedRead - 1].request, literal.set + literal.query + literal.reset + literal.witness, 'the pinned group sends exactly the pinned exchange, then the witness');
    assert.equal(contract.groups[contract.pinnedTerminalRead - 1].request, literal.query + literal.witness);
});

test('every request stays inside the owned exchange limit and the plan is exact', () => {
    for (const group of contract.groups) assert.ok(group.request.length <= 64, group.role);
    assert.equal(dslReads(contract), contract.groups.length + 1, 'an initial cells read and one report read per group');
    const reseeds = contract.groups.filter(group => group.reseed && group.expect !== 'cleared').length;
    assert.equal(dslFeeds(contract).length, contract.groups.length + 1 + reseeds, 'the first reseed, a reseed before each reseeding group and one captured exchange per group');
    assert.doesNotThrow(() => validateDslFeeds(contract, dslFeeds(contract)));
    assert.throws(() => validateDslFeeds(contract, [...dslFeeds(contract).slice(1)]));
    assert.deepEqual(contract.groups.filter(group => group.kind === 'control').map(group => group.role), ['recognized', 'burst-off', 'paced', 'control-clear']);
    assert.equal(contract.groups.at(-1).role, 'control-clear', 'the clear control runs last because it destroys the fixture');
    assert.deepEqual(contract.groups.filter(group => group.pauseMs !== null).map(group => group.role), ['paced'], 'only the instrument control is held');
    assert.equal(contract.groups.find(group => group.role === 'burst-held').reseed, false, 'the held burst keeps the mode the group before set');
    assert.deepEqual(contract.groups.filter(group => group.expect === 'burst').map(group => group.role), burstRoles);
});

test('every exchange ends with the witness, except the clear control, and the seed places the cursor away from home', () => {
    for (const group of contract.groups) assert.equal(group.request.endsWith(literal.witness), group.role !== 'control-clear', group.role);
    assert.ok(contract.plan[0].feed.endsWith(literal.place + literal.witness), 'the reseed places the cursor and writes the plain witness before the initial read');
    assert.ok(contract.plan[0].feed.startsWith('\x1b[2;1H\x1b[2K'), 'the band is cleared before it is seeded again, so a burst leaves no stale cell behind');
    const screens = syntheticDslScreens(contract);
    const at = role => screens.find(item => item.role === role).screen;
    assert.deepEqual([at('initial').cursorRow, at('initial').cursorCol], [3, 6], 'the baseline cursor is not at home');
    assert.deepEqual([at('burst-set').cursorRow, at('burst-set').cursorCol], [5, 0], 'a burst leaves the cursor on the bottom margin row');
    assert.deepEqual([at('control-clear').cursorRow, at('control-clear').cursorCol], [0, 0], 'the clear control homes it, which the state reader can therefore see');
});

test('the seed and the finish are exact and the finish resets the mode', () => {
    assert.throws(() => dslPlanSeed('nope'), /Invalid decsclm fixture run identity/);
    assert.ok(dslPlanSeed('a'.repeat(32)).includes('NTILDE_DSL_' + 'a'.repeat(32)));
    const finish = dslFinish('a'.repeat(32));
    assert.ok(finish.startsWith(literal.reset) && finish.endsWith('\x1b[?1049l'));
});

test('the pinned termless replay is the real result, whatever the reply was', () => {
    const note = 'DECRPM state does not measure scroll timing';
    assert.deepEqual(replayDslTermless(caseId, literal.four), { pass: false, response: JSON.stringify({ response: literal.four }), note,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } });
    assert.equal(replayDslTermless(caseId, '\x1b[?4;1$y').observation.outcome, 'inconclusive', 'a recognised state still cannot be supported or unsupported');
    assert.equal(replayDslTermless(caseId, '').observation.outcome, 'inconclusive', 'the pinned code returns the same verdict for an empty capture');
    assert.throws(() => replayDslTermless(caseId, undefined), /Missing decsclm reply text/);
    assert.throws(() => replayDslTermless('modes.nope', literal.four), /Unknown decsclm case/);
});

test('the pinned terminal replay maps the mode state exactly as queryMode and decrpmResult do', () => {
    assert.deepEqual(replayDslTerminal(caseId, [literal.four]), { pass: false, note: 'DECRPM mode 4: not recognized; mode behavior unmeasured', response: 'unknown',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note: 'DECRPM mode 4: not recognized; mode behavior unmeasured' } });
    assert.deepEqual(replayDslTerminal(caseId, []).observation, { outcome: 'inconclusive', reason: 'no-response', evidence: 'query', note: 'No DECRPM response' });
    for (const status of [1, 2, 3, 4]) assert.throws(() => replayDslTerminal(caseId, [`\x1b[?4;${status}$y`]), /would pass without any scroll timing/, `status ${status}`);
    assert.throws(() => replayDslTerminal(caseId, 'no'), /Missing decsclm reply list/);
    assert.throws(() => replayDslTerminal('modes.nope', []), /Unknown decsclm case/);
    assert.deepEqual([0, 1, 2, 3, 4].map(status => pinnedModeState([`\x1b[?4;${status}$y`])), ['unknown', 'set', 'reset', 'set', 'reset']);
    assert.equal(pinnedModeState(['\x1b[?3;0$y']), null, 'another mode is not the pinned reply');
    assert.equal(pinnedModeState(['\x1b[?4;5$y']), null, 'status 5 is not a DECRPM status');
});

test('the synthetic ignored behavior grades unsupported, and the observed record names the timing and the controls', () => {
    assert.equal(syntheticDslGrade().outcome, 'unsupported');
    const reads = syntheticDslScreens(contract);
    const observation = dslObservation(contract, reads, reads.map(read => read.replies), 'NTILDE_DSL_' + 'a'.repeat(32));
    const observed = contract.observed(observation);
    assert.deepEqual(observed.timing, { boundMs: 2000, pauseMs: 2500, burstLines: 40, smoothLinesPerSecond: 6, smoothFloorMs: 6667, clock: 'parent-monotonic' });
    assert.equal(observed.groups.paced.pauseMs, 2500);
    assert.equal(observed.groups.set.elapsedMs, 15);
    assert.equal(observed.groups.paced.elapsedMs, 2590);
    assert.equal(observed.groups['burst-held'].burst, true);
    assert.equal(observed.groups.set.unchanged, true);
    assert.equal(observed.groups['control-clear'].unchanged, null);
    assert.equal(observed.groups['burst-set'].unchanged, null);
    assert.throws(() => dslObservation(contract, reads.slice(1), reads.map(read => read.replies), 'n'), /Missing decsclm read captures/);
    assert.throws(() => gradeDslCase(contract, observation, 2), /Unknown decsclm contracts revision/);
    assert.throws(() => gradeDslCase(contract, {}), /Missing modes.decsclm read captures/);
});

/** Replace one role's read with a changed one. */
const changed = (role, change) => (_case, at, read) => (at === role ? change(read) : read);
const grade = mutate => syntheticDslGrade(caseId, undefined, mutate);
const inconclusive = (mutate, pattern) => {
    const result = grade(mutate);
    assert.equal(result.outcome, 'inconclusive');
    assert.match(result.note, pattern);
};
const timed = ms => read => ({ ...read, timing: timingOf(ms) });
const ordinary = contract.groups.map(group => group.role).filter(role => role !== 'paced');

test('an exchange over the bound makes the effect inconclusive, wherever it happens, and the bound itself holds', () => {
    for (const role of ordinary) {
        inconclusive(changed(role, timed(dslTimingBoundMs + 1)), /over the 2000 ms bound: output scrolling was paced/);
        assert.equal(grade(changed(role, timed(dslTimingBoundMs))).outcome, 'unsupported', `${role} at exactly the bound`);
        assert.equal(grade(changed(role, timed(0))).outcome, 'unsupported', `${role} at zero`);
    }
    // A scroll paced at six lines per second is a forty-line burst taking 6.6 seconds: far over the bound.
    inconclusive(changed('burst-set', timed(6667)), /burst-set exchange took 6667 ms/);
});

test('the timing instrument control must reach its hold, or the reader cannot see a slow exchange', () => {
    assert.equal(grade(changed('paced', timed(dslPauseMs))).outcome, 'unsupported');
    for (const ms of [0, 15, dslTimingBoundMs, dslPauseMs - 1]) inconclusive(changed('paced', timed(ms)), /Setup control failed.*cannot see a slow exchange/);
});

test('a burst that does not scroll the band blank, leaves another cursor, pen or mode, or changes the geometry is not a jump scroll', () => {
    const blankBandWith = rows => read => ({ ...read, screen: alternate(syntheticColEditScreen(rows, [5, 0], { margins: [1, 5] }, [], structuredClone(read.screen.colorBinding)), read.screen.lines[0]) });
    for (const role of burstRoles) {
        const control = ['burst-off', 'paced'].includes(role) ? /Setup control failed/ : /did not end as a jump scroll/;
        inconclusive(changed(role, read => ({ ...read, screen: syntheticDslScreens(contract).find(item => item.role === 'initial').screen })), control);
        inconclusive(changed(role, blankBandWith(seedRows().map((row, at) => (at === 5 ? [...row, colorCell('W')] : row)))), /did not end as a jump scroll/);
        inconclusive(changed(role, blankBandWith(dslBurstRows(colorCell('W', { attrs: ['bold'] })))), /bottom margin row/);
        inconclusive(changed(role, read => ({ ...read, screen: { ...read.screen, cursorRow: 4 } })), /did not end on the bottom margin row/);
        inconclusive(changed(role, read => ({ ...read, screen: { ...read.screen, cursorCol: 1 } })), /did not end on the bottom margin row/);
        inconclusive(changed(role, read => ({ ...read, screen: { ...read.screen, bufferState: { ...read.screen.bufferState, insertMode: true } } })), /insert, active screen|buffer state/);
        inconclusive(changed(role, read => ({ ...read, screen: { ...read.screen, cursorVisible: !read.screen.cursorVisible } })), /cursor mode, the buffer state/);
        inconclusive(changed(role, read => ({ ...read, screen: { ...read.screen, lines: ['not-the-nonce', ...read.screen.lines.slice(1)] } })), /did not end as a jump scroll|nonce/);
    }
});

test('a truecolor, underline-colored or otherwise non-neutral witness ends no burst, and a held burst cannot hide it', () => {
    const blankBandWith = rows => read => ({ ...read, screen: alternate(syntheticColEditScreen(rows, [5, 0], { margins: [1, 5] }, [], structuredClone(read.screen.colorBinding)), read.screen.lines[0]) });
    for (const pen of [{ fg: literalChannel(255, 0, 0) }, { bg: literalChannel(0, 0, 255) }, { underlineStyle: 1, underlineColorIndex: 3 }]) {
        for (const role of ['burst-set', 'burst-held']) inconclusive(changed(role, blankBandWith(dslBurstRows(colorCell('W', pen)))), /plain witness/);
        // The same pen on both the set burst and the held one (a reseed after it would erase it) is refused at the first.
        inconclusive((_case, role, read) => (['burst-set', 'burst-held'].includes(role) ? blankBandWith(dslBurstRows(colorCell('W', pen)))(read) : read), /burst-set burst did not end as a jump scroll/);
    }
});

test('a screen, cursor, margin or mode change by a mode 4 sequence makes the effect inconclusive', () => {
    const withCleared = read => ({ ...read, screen: alternate(syntheticColEditScreen([], [0, 0], { margins: [1, 5] }, [], structuredClone(read.screen.colorBinding))) });
    inconclusive(changed('set', withCleared), /changed the screen, the cursor/);
    inconclusive(changed('reset', read => ({ ...read, screen: { ...read.screen, cursorRow: 4, cursorCol: 7 } })), /changed the screen, the cursor/);
    inconclusive(changed('pinned', read => ({ ...read, screen: { ...read.screen, bufferState: { ...read.screen.bufferState, scrollTop: 0, scrollBottom: 12 } } })), /margins/);
    inconclusive(changed('restored', read => ({ ...read, screen: { ...read.screen, bufferState: { ...read.screen.bufferState, insertMode: true } } })), /insert modes/);
    inconclusive(changed('pinned-query', read => ({ ...read, screen: { ...read.screen, horizontalMargins: { leftRightMarginMode: true, leftMargin: 5, rightMargin: 40 } } })), /changed the screen, the cursor/);
    inconclusive(changed('restored', read => ({ ...read, screen: { ...read.screen, cursorVisible: !read.screen.cursorVisible } })), /changed the screen, the cursor/);
});

test('a grid change in any group is inconclusive', () => {
    for (const role of ordinary.filter(name => name !== 'control-clear')) {
        inconclusive(changed(role, read => ({ ...read, screen: resizedDslScreen(13, 132, read.screen.colorBinding) })), /changed the grid/);
    }
});

test('a recognised mode 4, a missing reply or an extra reply makes the effect inconclusive', () => {
    inconclusive(changed('set', read => ({ ...read, replies: ['\x1b[?4;2$y'] })), /replies differ/);
    inconclusive(changed('pinned', read => ({ ...read, replies: ['\x1b[?4;1$y'] })), /replies differ/);
    inconclusive(changed('pinned-query', read => ({ ...read, replies: [] })), /replies differ/);
    inconclusive(changed('burst-held', read => ({ ...read, replies: ['\x1b[?4;2$y'] })), /replies differ/);
    inconclusive(changed('restored', read => ({ ...read, replies: [literal.four, literal.four] })), /replies differ/);
    inconclusive(changed('control-clear', read => ({ ...read, replies: [literal.four] })), /answered with 1 reply where the contract demands none/);
});

test('a failed control makes the effect inconclusive instead of letting a silence stand for ignoring', () => {
    inconclusive(changed('recognized', read => ({ ...read, replies: [] })), /Setup control failed/);
    inconclusive(changed('recognized', read => ({ ...read, replies: [literal.four, literal.four] })), /Setup control failed/);
    inconclusive(changed('control-clear', read => ({ ...read, screen: alternate(syntheticColEditScreen(seedRows(), [0, 0], { margins: [1, 5] }, [], structuredClone(read.screen.colorBinding))) })), /Setup control failed.*did not clear/);
    inconclusive(changed('control-clear', read => ({ ...read, screen: { ...read.screen, cursorRow: 2, cursorCol: 3 } })), /Setup control failed.*did not clear/);
});

test('an earlier mismatch is reported before a later group it damaged, and a failed seed before any group', () => {
    const result = grade((_case, role, read) => (role === 'set' || role === 'restored' ? { ...read, replies: [] } : read));
    assert.match(result.note, /The set replies differ/);
    const seed = grade(changed('initial', read => ({ ...read, screen: { ...read.screen, lines: ['not-the-nonce', ...read.screen.lines.slice(1)] } })));
    assert.equal(seed.outcome, 'inconclusive');
    assert.match(seed.note, /attributed seed/);
});

test('a home-only effect and a pen-only effect are not masked by the seed', () => {
    for (const role of ['pinned', 'pinned-query', 'set', 'reset', 'restored']) {
        inconclusive(changed(role, read => ({ ...read, screen: { ...read.screen, cursorRow: 0, cursorCol: 0 } })), /changed the screen, the cursor/);
        // The witness rewritten after the exchange shows a pen the exchange left changed (bold here, a color or underline likewise).
        inconclusive(changed(role, read => ({ ...read, screen: syntheticDslScreens(contract, undefined, (_case, name, fresh) => (name === role ? {
            ...fresh, screen: { ...fresh.screen, ...syntheticColEditScreen(dslSeedRows(colorCell('W', { attrs: ['bold'] })), [3, 6], { margins: [1, 5] }, [], structuredClone(fresh.screen.colorBinding)),
                lines: fresh.screen.lines, bufferState: fresh.screen.bufferState, horizontalMargins: fresh.screen.horizontalMargins, cursorMode: fresh.screen.cursorMode } } : fresh)).find(item => item.role === role).screen })), /changed the screen, the cursor/);
    }
});

test('a seed at home, or with a witness that is not plain, is a failed seed', () => {
    const seeded = mutate => grade(changed('initial', mutate));
    assert.match(seeded(read => ({ ...read, screen: { ...read.screen, cursorRow: 0, cursorCol: 0 } })).note, /attributed seed/);
    const bold = syntheticColEditScreen(dslSeedRows(colorCell('W', { attrs: ['bold'] })), [3, 6], { margins: [1, 5] }, [], undefined);
    assert.match(seeded(read => ({ ...read, screen: { ...read.screen, cellDetails: bold.cellDetails, attributeRuns: bold.attributeRuns } })).note, /attributed seed/);
});
