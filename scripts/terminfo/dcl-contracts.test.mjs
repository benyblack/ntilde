import assert from 'node:assert/strict';
import { test } from 'node:test';
import { dclContracts, dclCaseIds, dclFeeds, dclFinish, dclFixture, dclPinnedScripts, dclPlanSeed, dclReads, deccolmWidths, pinnedModeState, replayDclTermless, replayDclTerminal,
    validateDclFeeds } from './dcl-contracts.mjs';
import { colorCell, dclSeedRows, kernelOf, resizedDclScreen, syntheticDclGrade, syntheticDclScreens } from './fixtures/dcl-evidence.mjs';
import { syntheticColEditScreen } from './fixtures/col-edit-evidence.mjs';
import { alternate, seedRows } from './fixtures/devq-evidence.mjs';

const [contract] = dclContracts;
const caseId = contract.caseId;
// The hand-written literals the contract must agree with (an independent spelling of the same bytes).
const literal = { set: '\x1b[?3h', reset: '\x1b[?3l', query: '\x1b[?3$p', allow: '\x1b[?40h', disallow: '\x1b[?40l', three: '\x1b[?3;0$y', forty: '\x1b[?40;0$y', witness: 'W\x1b[D', place: '\x1b[4;7H' };

test('the case is the pinned modes.deccolm and its fixtures are written out independently', () => {
    assert.deepEqual([...dclCaseIds], ['modes.deccolm']);
    assert.equal(dclFixture.setThree, literal.set);
    assert.equal(dclFixture.resetThree, literal.reset);
    assert.equal(dclFixture.queryThree, literal.query);
    assert.equal(dclFixture.notRecognizedThree, literal.three);
    assert.equal(dclFixture.notRecognizedForty, literal.forty);
    assert.deepEqual([...deccolmWidths], [80, 132]);
});

test('the pinned scripts name exactly the calls the pinned code makes', () => {
    const script = dclPinnedScripts[caseId];
    assert.deepEqual(script.termless, [['feed', literal.set], ['feedCapture', literal.query, 'replies'], ['feed', literal.reset]]);
    assert.deepEqual(script.terminal, [['queryMode', 3]]);
    assert.deepEqual([...contract.pinnedFeeds], [literal.set, literal.query, literal.reset]);
    assert.equal(contract.groups[contract.pinnedRead - 1].request, literal.set + literal.query + literal.reset + literal.witness, 'the pinned group sends exactly the pinned exchange, then the witness');
    assert.equal(contract.groups[contract.pinnedTerminalRead - 1].request, literal.query + literal.witness);
});

test('every request stays inside the owned exchange limit and the plan is exact', () => {
    for (const group of contract.groups) assert.ok(group.request.length <= 64, group.role);
    assert.equal(dclReads(contract), contract.groups.length + 1, 'an initial cells read and one report read per group');
    assert.equal(dclFeeds(contract).length, contract.groups.length + 1, 'the reseed and one captured exchange per group');
    assert.doesNotThrow(() => validateDclFeeds(contract, dclFeeds(contract)));
    assert.throws(() => validateDclFeeds(contract, [...dclFeeds(contract).slice(1)]));
    assert.deepEqual(contract.groups.filter(group => group.kind === 'control').map(group => group.role), ['recognized', 'control-clear']);
    assert.equal(contract.groups.at(-1).role, 'control-clear', 'the clear control runs last because it destroys the fixture');
});

test('every exchange ends with the witness, except the clear control, and the seed places the cursor away from home', () => {
    for (const group of contract.groups) assert.equal(group.request.endsWith(literal.witness), group.role !== 'control-clear', group.role);
    assert.ok(contract.plan[0].feed.endsWith(literal.place + literal.witness), 'the reseed places the cursor and writes the plain witness before the initial read');
    const [initial, , , , , , , , , , , , cleared] = syntheticDclScreens(contract);
    assert.deepEqual([initial.screen.cursorRow, initial.screen.cursorCol], [3, 6], 'the baseline cursor is not at home');
    assert.deepEqual([cleared.screen.cursorRow, cleared.screen.cursorCol], [0, 0], 'the clear control homes it, which the state reader can therefore see');
});

test('the seed and the finish are exact and the finish leaves switching disallowed', () => {
    assert.throws(() => dclPlanSeed('nope'), /Invalid deccolm fixture run identity/);
    assert.ok(dclPlanSeed('a'.repeat(32)).includes('NTILDE_DCL_' + 'a'.repeat(32)));
    const finish = dclFinish('a'.repeat(32));
    assert.ok(finish.startsWith(literal.disallow + literal.reset) && finish.endsWith('\x1b[?1049l'));
});

test('the pinned termless replay is the real result for an answered query and refuses an unanswered one', () => {
    const replay = replayDclTermless(caseId, literal.three, 60);
    assert.deepEqual(replay, { pass: false, response: JSON.stringify({ response: literal.three, cols: 60 }), note: 'DECRPM state does not establish a width transition',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: 'DECRPM state does not establish a width transition' } });
    assert.equal(replayDclTermless(caseId, '\x1b[?3;1$y', 132).observation.outcome, 'inconclusive', 'a recognised state still cannot be supported or unsupported');
    assert.throws(() => replayDclTermless(caseId, '', 60), /was not answered/);
    assert.throws(() => replayDclTermless(caseId, literal.three, 0), /grid width/);
    assert.throws(() => replayDclTermless('modes.nope', literal.three, 60), /Unknown deccolm case/);
});

test('the pinned terminal replay maps the mode state exactly as queryMode and decrpmResult do', () => {
    assert.deepEqual(replayDclTerminal(caseId, [literal.three]), { pass: false, note: 'DECRPM mode 3: not recognized; mode behavior unmeasured', response: 'unknown',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note: 'DECRPM mode 3: not recognized; mode behavior unmeasured' } });
    assert.deepEqual(replayDclTerminal(caseId, []).observation, { outcome: 'inconclusive', reason: 'no-response', evidence: 'query', note: 'No DECRPM response' });
    for (const status of [1, 2, 3, 4]) assert.throws(() => replayDclTerminal(caseId, [`\x1b[?3;${status}$y`]), /would pass without a width measurement/, `status ${status}`);
    assert.deepEqual([0, 1, 2, 3, 4].map(status => pinnedModeState([`\x1b[?3;${status}$y`])), ['unknown', 'set', 'reset', 'set', 'reset']);
    assert.equal(pinnedModeState(['\x1b[?40;0$y']), null, 'another mode is not the pinned reply');
    assert.equal(pinnedModeState(['\x1b[?3;5$y']), null, 'status 5 is not a DECRPM status');
});

test('the synthetic ignored behavior grades unsupported', () => {
    assert.equal(syntheticDclGrade().outcome, 'unsupported');
});

/** Replace one role's read with a changed one. */
const changed = (role, change) => (_case, at, read) => (at === role ? change(read) : read);
const grade = mutate => syntheticDclGrade(caseId, undefined, mutate);
const inconclusive = (mutate, pattern) => {
    const result = grade(mutate);
    assert.equal(result.outcome, 'inconclusive');
    assert.match(result.note, pattern);
};
const kernelWidth = (cols, rows = 13) => read => ({ ...read, kernel: kernelOf(rows, cols) });
/** The App and the kernel both report the new grid. */
const resized = (cols, rows = 13) => read => ({ ...read, screen: resizedDclScreen(rows, cols, read.screen.colorBinding), kernel: kernelOf(rows, cols) });
/** Only the App reports the new grid. */
const appResized = (cols, rows = 13) => read => ({ ...read, screen: resizedDclScreen(rows, cols, read.screen.colorBinding) });

test('a grid change in the App, in the kernel or in both makes the effect inconclusive, wherever it happens', () => {
    for (const role of contract.groups.map(group => group.role).filter(name => name !== 'control-clear')) {
        inconclusive(changed(role, resized(132)), /changed the grid width or height/);
        inconclusive(changed(role, kernelWidth(132)), /disagree/);
        inconclusive(changed(role, appResized(132)), /disagree/);
        inconclusive(changed(role, kernelWidth(60, 24)), /disagree/);
    }
    // The kernel and the App agree on a changed grid: still a change from the baseline.
    inconclusive(changed('allow-set', resized(80)), /changed the grid width or height/);
    inconclusive(changed('set', resized(60, 24)), /changed the grid width or height/);
});

test('a baseline width of 80 or 132 cannot show a transition in both directions and is a failed seed', () => {
    for (const width of deccolmWidths) {
        const result = grade((_case, _role, read) => resized(width)(read));
        assert.equal(result.outcome, 'inconclusive');
        assert.match(result.note, /attributed seed/);
    }
});

test('a screen, cursor, margin or mode change by a mode 3 sequence makes the effect inconclusive', () => {
    const screenOf = read => read.screen;
    const withCleared = read => ({ ...read, screen: alternate(syntheticColEditScreen([], [0, 0], { margins: [1, 5] }, [], structuredClone(screenOf(read).colorBinding))) });
    inconclusive(changed('set', withCleared), /changed the screen, the cursor/);
    inconclusive(changed('allow-set', read => ({ ...read, screen: { ...read.screen, cursorRow: 4, cursorCol: 7 } })), /changed the screen, the cursor/);
    inconclusive(changed('reset', read => ({ ...read, screen: { ...read.screen, bufferState: { ...read.screen.bufferState, scrollTop: 0, scrollBottom: 12 } } })), /margins/);
    inconclusive(changed('allow-cycle', read => ({ ...read, screen: { ...read.screen, horizontalMargins: { leftRightMarginMode: true, leftMargin: 5, rightMargin: 40 } } })), /changed the screen, the cursor/);
    inconclusive(changed('restored', read => ({ ...read, screen: { ...read.screen, cursorVisible: !read.screen.cursorVisible } })), /changed the screen, the cursor/);
});

test('a recognised mode 3 or mode 40, a missing reply or an extra reply makes the effect inconclusive', () => {
    inconclusive(changed('set', read => ({ ...read, replies: ['\x1b[?3;2$y'] })), /replies differ/);
    inconclusive(changed('pinned', read => ({ ...read, replies: ['\x1b[?3;1$y'] })), /replies differ/);
    inconclusive(changed('pinned-query', read => ({ ...read, replies: [] })), /replies differ/);
    inconclusive(changed('allow', read => ({ ...read, replies: [literal.three, '\x1b[?40;2$y'] })), /replies differ/);
    inconclusive(changed('allow-set', read => ({ ...read, replies: [literal.three, literal.three] })), /replies differ/);
    inconclusive(changed('control-clear', read => ({ ...read, replies: [literal.three] })), /answered with 1 reply where the contract demands none/);
});

test('a failed control makes the effect inconclusive instead of letting a silence stand for ignoring', () => {
    inconclusive(changed('recognized', read => ({ ...read, replies: [] })), /Setup control failed/);
    inconclusive(changed('recognized', read => ({ ...read, replies: [literal.three, literal.three] })), /Setup control failed/);
    // The clear control must visibly clear and home: a screen that kept its ink or cursor means the reader could not have seen a DECCOLM clear.
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
    for (const role of contract.groups.map(group => group.role).filter(name => name !== 'control-clear')) {
        inconclusive(changed(role, read => ({ ...read, screen: { ...read.screen, cursorRow: 0, cursorCol: 0 } })), /changed the screen, the cursor/);
        // The witness rewritten after the exchange shows a pen the exchange left changed (bold here, a color or underline likewise).
        inconclusive(changed(role, read => ({ ...read, screen: syntheticDclScreens(contract, undefined, (_case, name, fresh) => (name === role ? {
            ...fresh, screen: { ...fresh.screen, ...syntheticColEditScreen(dclSeedRows(colorCell('W', { attrs: ['bold'] })), [3, 6], { margins: [1, 5] }, [], structuredClone(fresh.screen.colorBinding)),
                lines: fresh.screen.lines, bufferState: fresh.screen.bufferState, horizontalMargins: fresh.screen.horizontalMargins, cursorMode: fresh.screen.cursorMode } } : fresh)).find(item => item.role === role).screen })), /changed the screen, the cursor/);
    }
});

test('a seed at home, or with a witness that is not plain, is a failed seed', () => {
    const seeded = mutate => grade(changed('initial', mutate));
    assert.match(seeded(read => ({ ...read, screen: { ...read.screen, cursorRow: 0, cursorCol: 0 } })).note, /attributed seed/);
    const bold = syntheticColEditScreen(dclSeedRows(colorCell('W', { attrs: ['bold'] })), [3, 6], { margins: [1, 5] }, [], undefined);
    assert.match(seeded(read => ({ ...read, screen: { ...read.screen, cellDetails: bold.cellDetails, attributeRuns: bold.attributeRuns } })).note, /attributed seed/);
});
