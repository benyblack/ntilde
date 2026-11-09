// Versioned probe contracts for the erase-character / saved-lines qualification batch. Exactly two pinned terminfo.dev@4.0.0
// cases (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//
//   erase.character          eraseRowResult(ctx, "ABCDE\x1B[1G", "\x1B[3X", [blank, blank, blank, "D", "E"], false, 0)
//                            feed `ABCDE ESC[1G`; read cells 0..4 of row 0 (must be ABCDE) and the cursor (must be 0,0); feed `ESC[3X`
//                            (ECH, EXPLICIT count 3); read cells 0..4. Supported iff cells 0..2 are blank ("" or " ") and cells 3 and 4
//                            read D and E. A missing fixture, wrong cursor or an unreadable cell is inconclusive. It never reads the cursor
//                            after the erase, any column past 4, any other row or any attribute.
//   erase.screen.scrollback  feed `\r\n` x (rows + 2); the measured scrollback must grow past the screen (`totalLines > rows`, else
//                            inconclusive "No measured scrollback to erase"); feed `ESC[3J` (ED 3, EXPLICIT 3); supported iff the measured total
//                            equals the screen rows. It never reads the viewport, the cursor, the margins, any mode, the history contents, or
//                            whether the pre-erase history held anything but blank lines (the pinned fixture scrolls blank rows only).
//
// Both pinned termless probes decide (neither is null by construction). The pinned terminal pixel/query path of each case (the second
// function of its registration) writes other bytes, ends in a cursor-position query and returns the fixed inconclusive
// "Cursor response does not measure erased cells or scrollback", so it never carries a verdict and is not qualified.
//
// SPELLINGS ARE DISTINCT. erase.character grades the EXPLICIT count 3 (`ESC[3X`); erase.screen.scrollback grades the explicit `ESC[3J`. The
// omitted, explicit 0 and 1, count 5 and oversized ECH counts run as separate, labeled observations that are never grades, and no other
// spelling (ECH 1, ED 2, DCH, EL, scrolling) is substituted for a pinned one.
//
// This contract grades the pinned bytes against independently observed state. Every expected state comes from the byte-interpreting model
// (scrollback-model.mjs) over the exact bytes the plan feeds, never from terminal readback, and is compared with the complete viewport,
// cursor, margins, modes, active buffer, stored cell attributes and the actual ordered saved lines the App reports.
//
// erase.character demands, on top of the pinned cells: the whole row (not five cells) matches, so the erase is in place and neither shifts
// the suffix (DCH), nor erases the line, nor reaches past its count; rows above and below stay intact; the count clamps at the right edge
// without wrapping; the cursor does not move; retained cells keep their stored attributes; and erased cells are blank and carry no stale
// text attribute of the characters they replaced (their background is observed against a same-pen reference, not required).
//
// erase.screen.scrollback demands, beyond the pinned totals: an independently observed empty-history baseline, then nonempty history
// (the pinned fixture scrolls three blank rows; the marked control scrolls nine ordered `HIST-N` rows) before CSI 3J; after it the history is
// empty while the viewport, cursor, margins, modes and active (main) buffer are exactly preserved. Controls separate CSI 3J from CSI 2J (ED 2
// clears the viewport and keeps every saved line), from ordinary scrolling (saved lines only grow) and from a viewport clear (the marked rows
// survive CSI 3J), show that CSI 3J on an already-empty history changes nothing visible (so it cannot establish erasure) and that scrolling after
// CSI 3J saves only the newly scrolled rows (the old ones are gone, not merely hidden). History counts are PHYSICAL rows as stored: the
// fixtures write no wrapped (logical) lines, and no logical-line claim is made. Nothing here relies on the operation under test to
// establish its own baseline: the controls observe the baseline directly, and the pinned-fixture verdict needs only the controls that
// precede it on a pristine screen (a reset that depends on CSI 3J cannot rescue or condemn the pinned fixture).
//
// SCOPE. ASCII fixtures at the measured geometry only. ASCII evidence does not establish wide-cell or combining-character erasure, selective
// erasure, rectangles, wrapped-line or reflow history, resize, alternate-screen history or pixel appearance.
import { validateExactFeeds } from './graded-plan.mjs';
import { PlanBuilder, scrollbackReset, scrollbackMinimum, scrollbackMismatch, scrollbackView, compactLines } from './scrollback-contracts.mjs';
import { modelPlanStates } from './scrollback-model.mjs';
import { eraseLineInk } from './erase-line-contracts.mjs';
import { sgrAttrsClear, sgrAttrsPredicates } from './sgr-attrs-contracts.mjs';

const { neutral } = sgrAttrsPredicates;

/** Contracts v1 is the first measured semantics of this batch. */
export const eraseCharsHistoryContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const eraseCharsHistorySuiteId = 'ntilde-owned-erase-chars-history-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const eraseCharsHistoryNoncePrefix = 'NTILDE_EH_';
/** The measured reset every plan begins with: main screen, DECSCNM, DECOM, margins, IRM and SGR off, screen and history cleared, cursor home. */
export const eraseCharsHistoryReset = scrollbackReset;
/** The exact pinned case ids, in suite order. */
export const eraseCharsHistoryCaseIds = Object.freeze(['erase.character', 'erase.screen.scrollback']);
/** The smallest geometry the contracts accept (the observer admits at most 128 rows and 256 columns). */
export const eraseCharsHistoryMinimum = scrollbackMinimum;
// The shared screen validation (attribute runs, stored details, resolved colors, buffer state and actual history must agree) and the pinned getCell projection.
export { validateScrollbackScreen as validateEraseCharsHistoryScreen, scrollbackCellAt as eraseCharsHistoryCellAt } from './scrollback-contracts.mjs';

/** The pinned fixture notes, byte for byte. */
export const pinnedEraseCharsHistoryNotes = Object.freeze({
    geometry: 'Measured geometry cannot fit the erase fixture',
    fixture: 'Erase fixture, cursor setup, or cell metadata unavailable',
    historyGeometry: 'Need a measured screen size for history setup',
    historyNone: 'No measured scrollback to erase',
    historyReadback: 'Scrollback readback or geometry changed unexpectedly',
});
/** The pinned `expected` assertion text of each case, byte for byte. */
const pinnedExpectedText = Object.freeze({
    'erase.character': 'row 0 blank,blank,blank,D,E; cursor before 0,0',
    'erase.screen.scrollback': 'ED3 removes measured scrollback while keeping screen geometry',
});

const clear = sgrAttrsClear;
const at = (row, col = 1) => `\x1b[${row};${col}H`;
const wideSeed = '\x1b[1;1HABOVE-ROW!\x1b[2;1HABCDEFGHIJ\x1b[3;1HBELOW-ROW!\x1b[2;3H';
/** Three characters ending in the last column, cursor on the first of them (the write ends in a pending wrap; CUB clears it). */
const edgeSeed = '\x1b[1;1H\x1b[999C\x1b[2DWXY\x1b[2D';
/** A: bold, bold, plain, bold, bold, plain, plain; cursor at column 2 and a blue-background pen active. */
const attrSeed = '\x1b[1mAB\x1b[0mC\x1b[1mDE\x1b[0mFG\x1b[1;3H\x1b[44m';
/** The reference write: one cell written under the same SGR 44 pen. */
const penReference = '\x1b[44mX';
const echSetup = 'ABCDE\x1b[1G';
const echPinned = '\x1b[3X';
const echSpellings = Object.freeze({ omitted: '\x1b[X', explicit0: '\x1b[0X', explicit1: '\x1b[1X', explicit5: '\x1b[5X' });
const historyEraseBytes = '\x1b[3J';

/** The bold-only stored tuple the attribute control seeds. */
const boldOnly = cell => cell.bold === true && !cell.dim && !cell.italic && !cell.inverse && !cell.hidden && !cell.strikethrough
    && !cell.blink && !cell.overline && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.bgIndex === -1
    && cell.foregroundArgb === null && cell.backgroundArgb === null && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
/** A stored cell with no text attribute, default foreground and no underline: whatever its background. */
const textNeutral = cell => !cell.bold && !cell.dim && !cell.italic && !cell.inverse && !cell.hidden && !cell.strikethrough
    && !cell.blink && !cell.overline && cell.underlineStyle === 0 && cell.fgIndex === -1 && cell.foregroundArgb === null
    && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
/** Whether a cell is stored with some non-default background. */
const painted = cell => cell !== undefined && (cell.bgIndex >= 0 || cell.backgroundArgb !== null);

/** Build one frozen contract from a finished plan. */
function finish(rows, cols, { caseId, builder, criterion, fixture, minimum, family, terminalWrites }) {
    const states = modelPlanStates(builder.steps, rows, cols);
    const roles = builder.roles.map((entry, index) => {
        const alternatives = entry.alternatives.map(({ label, feedIndex, replacement }) =>
            ({ label, state: modelPlanStates(builder.steps, rows, cols, (position, bytes) => (position === feedIndex ? replacement : bytes))[index] }));
        return Object.freeze({ role: entry.role, kind: entry.kind, options: entry.options, expected: states[index], alternatives });
    });
    const contract = {
        caseId, probeId: caseId, family, pinnedPath: 'termless', rows, cols, roles: Object.freeze(roles), pinnedReadAt: Object.freeze({ ...builder.readAt }),
        pinnedFeeds: Object.freeze([...builder.pinned]), pinnedCriterion: criterion, pinnedExpected: pinnedExpectedText[caseId],
        plan: Object.freeze(builder.steps),
        // The measured layer of this case: ECH reads stored cells and attributes only (its saved-line checks are preserved-state controls).
        evidence: family === 'erase-character' ? 'owned-stored-cells' : 'owned-stored-cells-and-actual-scrollback',
        terminalFixture: { writes: terminalWrites, demand: fixture, minimum,
            note: 'upstream pixel/query path writes different bytes and ends in a cursor-position query that never measures cells or saved lines: it carries no verdict and is not qualified' },
        appearanceScope: family === 'erase-character'
            ? 'stored ASCII cells only: the exact erased range and every retained cell across the whole row, unaffected rows, the cursor, margins and modes, and stored attributes of retained and '
                + 'erased cells; the background of erased cells is observed against a same-pen reference, not required; no wide-cell or combining-character erasure and no pixels are claimed'
            : 'stored viewport text, cursor, margins, modes, active buffer and the actual ordered saved physical rows and their count; no wrapped (logical) line, wide-cell, reflow, resize, '
                + 'alternate-screen history, terminal query or pixel claim',
    };
    contract.grade = observation => gradeCase(contract, observation);
    contract.observed = observation => briefCase(contract, observation);
    return Object.freeze(contract);
}

/** The ECH case: the pinned fixture plus its discriminating controls, in a fixed plan. */
function characterCase(rows, cols) {
    const b = new PlanBuilder();
    b.feed(eraseCharsHistoryReset);
    b.read('initial', 'control', { pinned: true });
    b.pin(echSetup);
    b.read('seed', 'control', { pinned: true });
    const erase = b.pin(echPinned);
    const erased = b.read('erased', 'measured', { pinned: true });
    const wrongRange = [['no-op: nothing was erased', '\x1b[0m'], ['DCH shifted the suffix left instead of erasing in place', '\x1b[3P'],
        ['the whole line was erased', '\x1b[2K'], ['erased to the end of the line', '\x1b[K'], ['erased 2 cells instead of 3', '\x1b[2X'], ['erased 4 cells instead of 3', '\x1b[4X']];
    for (const [label, bytes] of wrongRange) b.alternative(erased, label, erase, bytes);
    for (const [name, bytes] of Object.entries(echSpellings)) {
        b.feed(clear); b.feed(echSetup); b.feed(bytes);
        b.read(name, 'observed');
    }
    b.feed(clear); b.feed(wideSeed);
    b.read('wideBefore', 'control');
    const wideErase = b.feed(echPinned);
    const wide = b.read('wide', 'measured');
    for (const [label, bytes] of wrongRange.slice(0, 4)) b.alternative(wide, label, wideErase, bytes);
    // A count past the end of the line is clamped there: it neither wraps onto the next row nor erases the rows around it (graded, so a spill cannot be masked by the next clear).
    b.feed(clear); b.feed(wideSeed);
    const oversizedErase = b.feed('\x1b[999X');
    const oversized = b.read('wideOversized', 'measured');
    b.alternative(oversized, 'no-op: nothing was erased', oversizedErase, '\x1b[0m');
    b.alternative(oversized, 'the whole line was erased', oversizedErase, '\x1b[2K');
    b.alternative(oversized, 'the erase did not stop at the cursor: it started at the line start', oversizedErase, '\x1b[1K');
    b.feed(clear); b.feed(edgeSeed);
    b.read('edgeBefore', 'control');
    const edgeErase = b.feed('\x1b[2X');
    const edge = b.read('edge', 'measured');
    b.alternative(edge, 'no-op: nothing was erased at the right edge', edgeErase, '\x1b[0m');
    b.alternative(edge, 'erased 3 cells instead of 2: the last cell was erased too', edgeErase, '\x1b[3X');
    b.feed(clear); b.feed(attrSeed);
    b.read('attrBefore', 'control', { options: { attributes: true } });
    b.feed(echPinned);
    b.read('attr', 'measured', { options: { attributes: true } });
    b.feed(clear); b.feed(penReference);
    b.read('pen', 'control', { options: { attributes: true } });
    return finish(rows, cols, { caseId: 'erase.character', builder: b, family: 'erase-character',
        criterion: 'ECH `ESC[3X` at column 0 of ABCDE erases exactly cells 0..2 in place: cells 0..2 read blank and cells 3 and 4 read D and E, after a setup cursor of 0,0',
        fixture: 'exact erased range, retained cells, unaffected rows and columns, cursor, and stored attributes', minimum: { rows: 1, cols: 6 },
        terminalWrites: ['\x1b[1;1H\x1b[2K', 'ABCD', '\x1b[1;2H', '\x1b[2X'] });
}

/** The ED 3 case: the pinned fixture plus marked-history controls, in a fixed plan. `rows` is the measured screen height. */
function historyCase(rows, cols) {
    const b = new PlanBuilder();
    const pinStage = { stage: 'pinned' }, extra = { stage: 'extra' };
    b.feed(eraseCharsHistoryReset);
    b.read('initial', 'control', { pinned: true, options: pinStage });
    b.pin('\r\n'.repeat(rows + 2));
    b.read('before', 'control', { pinned: true, options: pinStage });
    const erase = b.pin(historyEraseBytes);
    const after = b.read('after', 'measured', { pinned: true, options: pinStage });
    b.alternative(after, 'no-op or ED 2: the saved lines were not erased', erase, '\x1b[0m');
    const lines = Array.from({ length: rows + 8 }, (_, index) => `HIST-${index}\r\n`).join('');
    const cursor = at(7, 12);
    const seed = () => { b.feed(eraseCharsHistoryReset); b.feed(lines); b.feed(cursor); };
    seed();
    b.read('seed', 'control', { options: extra });
    const marked = b.feed(historyEraseBytes);
    const erased = b.read('erase3', 'measured', { options: extra });
    b.alternative(erased, 'no-op: the saved lines were not erased', marked, '\x1b[0m');
    b.alternative(erased, 'ED 2 instead of ED 3: the viewport was cleared and the saved lines kept', marked, '\x1b[2J');
    b.alternative(erased, 'the viewport was cleared along with the saved lines', marked, '\x1b[3J\x1b[2J');
    seed();
    b.feed('\x1b[2J');
    b.read('ed2', 'control', { options: extra });
    seed();
    b.feed(`${at(rows)}\r\n\r\n\r\n`);
    b.read('scrolled', 'control', { options: extra });
    b.feed(eraseCharsHistoryReset);
    b.feed(Array.from({ length: rows }, (_, index) => `${at(index + 1)}ROW-${index}`).join('') + cursor);
    b.read('emptyBefore', 'control', { options: extra });
    const empty = b.feed(historyEraseBytes);
    const emptied = b.read('emptyErase', 'measured', { options: extra });
    b.alternative(emptied, 'the viewport was cleared although there were no saved lines', empty, '\x1b[3J\x1b[2J');
    seed();
    const regrowErase = b.feed(historyEraseBytes);
    b.feed(`${at(rows)}\r\n\r\n`);
    const regrown = b.read('regrow', 'measured', { options: extra });
    b.alternative(regrown, 'the earlier saved lines remained: they were hidden, not erased', regrowErase, '\x1b[0m');
    return finish(rows, cols, { caseId: 'erase.screen.scrollback', builder: b, family: 'erase-saved-lines',
        criterion: 'ED 3 `ESC[3J` removes the measured saved lines (the screen is scrolled by rows+2 line feeds first) and the measured total returns to the screen rows',
        fixture: 'nonempty ordered saved lines before, empty after, with viewport, cursor, margins, modes and the main buffer preserved', minimum: { rows: 5, cols: 5 },
        terminalWrites: ['\x1b[5;5H', '\x1b[3J'] });
}

/** Build the pinned contracts for the measured geometry. */
export function makeEraseCharsHistoryContracts(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < eraseCharsHistoryMinimum.rows || rows > 128 || !Number.isSafeInteger(cols) || cols < eraseCharsHistoryMinimum.cols || cols > 256) {
        throw new Error('Erase-character/saved-lines contracts need at least 12 rows and 48 columns, and at most 128 rows and 256 columns (the observer limits).');
    }
    return Object.freeze([characterCase(rows, cols), historyCase(rows, cols)]);
}

/** A group's observation: the state view, and the stored ink of the cell groups. */
const viewOf = (screen, withInk) => ({ ...scrollbackView(screen), ...(withInk ? { ink: eraseLineInk(screen) } : {}) });

/** Whether DECSCNM was off in every read (false), on in any read (true), or not proven either way (null). */
function reverseVideoOf(values) {
    if (values.every(value => value === false)) return false;
    return values.includes(true) ? true : null;
}

/** Group the read screens of one case by role. */
export function eraseCharsHistoryObservation(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contract.roles.length) throw new Error('Missing erase-character/saved-lines read captures.');
    const withInk = contract.family === 'erase-character';
    const observation = Object.fromEntries(contract.roles.map((entry, index) => [entry.role, viewOf(screens[index], withInk)]));
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    observation.reverseVideo = reverseVideoOf(binding);
    return observation;
}

/** The first way an observed role differs from its modeled state, or null. Attribute roles skip only the default-storage requirement:
 * their stored tuples are checked by the attribute control. */
const mismatchOf = (role, view) => scrollbackMismatch(role.options.attributes ? { ...view, storedDefault: true } : view, role.expected, role.options);

/** The wording for one failed measured role: a labeled wrong behavior when the observation equals it, else the generic difference. */
function diagnose(role, view, mismatch) {
    for (const alternative of role.alternatives) {
        if (scrollbackMismatch(role.options.attributes ? { ...view, storedDefault: true } : view, alternative.state, role.options) === null) return `${role.role}: ${alternative.label}`;
    }
    return `${role.role}: ${mismatch.note}`;
}

const sameCursor = (state, row, col) => state.cursor[0] === row && state.cursor[1] === col;
const seedCells = [['A', true], ['B', true], ['C', false], ['D', true], ['E', true], ['F', false], ['G', false]];

/** The ECH attribute control: retained text keeps its stored tuple, erased cells are blank with no stale text attribute, and the
 * erased background is observed against a same-pen reference. */
function characterAttributes(observation) {
    const before = observation.attrBefore, after = observation.attr, penCell = observation.pen.ink.find(cell => cell.char === 'X');
    const seeded = before.ink.length === seedCells.length && before.ink.every((cell, col) => cell.row === 0 && cell.col === col && cell.char === seedCells[col][0]
        && (seedCells[col][1] ? boldOnly(cell) : neutral(cell))) && sameCursor(before, 0, 2);
    const text = after.ink.filter(cell => cell.char !== ' ');
    const kept = [0, 1, 5, 6];
    const retainedOk = text.length === kept.length && text.every((cell, index) => cell.row === 0 && cell.col === kept[index]
        && cell.char === seedCells[cell.col][0] && (seedCells[cell.col][1] ? boldOnly(cell) : neutral(cell)));
    const blanks = after.ink.filter(cell => cell.char === ' ');
    const blanksOk = blanks.every(cell => cell.row === 0 && [2, 3, 4].includes(cell.col));
    const noStale = blanks.every(textNeutral);
    const cells = [2, 3, 4].map(col => blanks.find(cell => cell.col === col));
    const penMeasured = painted(penCell);
    const storedOf = cell => (cell === undefined ? null : { bgIndex: cell.bgIndex, backgroundArgb: cell.backgroundArgb, resolved: cell.bg });
    return { seeded, retainedOk, blanksOk, noStale, cursorOk: sameCursor(after, 0, 2), background: backgroundObservation(cells, penCell, penMeasured),
        detail: { pen: penMeasured ? storedOf(penCell) : null, firstErased: storedOf(cells[0]) } };
}

/** Compare the stored background of the erased cells with a same-pen reference: resolved color and stored representation separately. */
function backgroundObservation(cells, penCell, penMeasured) {
    if (cells.every(cell => cell === undefined)) return 'erased cells keep the default background';
    if (!penMeasured) return 'unmeasured: the reference pen cell was not stored with a background';
    const sameResolved = cell => painted(cell) && JSON.stringify(cell.bg) === JSON.stringify(penCell.bg);
    const sameStored = cell => sameResolved(cell) && cell.bgIndex === penCell.bgIndex && cell.backgroundArgb === penCell.backgroundArgb;
    if (cells.every(sameStored)) return 'erased cells carry the current background';
    if (cells.every(sameResolved)) return 'erased cells carry the current background color, stored differently from the pen cell (palette index versus literal ARGB)';
    if (cells.every(painted) && !cells.some(sameResolved)) return 'erased cells carry a different non-default background color than the current pen';
    return 'mixed';
}

/** The attribute control's verdict once its seed and the measured roles held: a failure, or null. */
function attributeVerdict(attributes) {
    if (!attributes.retainedOk || !attributes.blanksOk) {
        return { outcome: 'unsupported', note: 'attr: retained cells lost their stored text or attributes, or a blank appeared outside the erased range' };
    }
    if (!attributes.noStale) return { outcome: 'unsupported', note: 'attr: an erased cell kept a stale text attribute of the character it replaced' };
    if (!attributes.cursorOk) return { outcome: 'unsupported', note: 'attr: the cursor moved during the erase' };
    return null;
}

/** One stage: its controls (inconclusive when one fails), then its measured roles and the attribute control (unsupported when one fails), or null. */
function gradeStage(contract, observation, stage, attributes) {
    const inStage = contract.roles.filter(entry => (entry.options.stage ?? 'pinned') === stage);
    for (const role of inStage.filter(entry => entry.kind === 'control')) {
        const mismatch = mismatchOf(role, observation[role.role]);
        if (mismatch) return { outcome: 'inconclusive', note: `Control ${role.role} was not established (${mismatch.note}), so the operation cannot be graded` };
    }
    if (attributes && !attributes.seeded) {
        return { outcome: 'inconclusive', note: 'The attribute control seed was not measured, so retained and erased attributes cannot be distinguished' };
    }
    for (const role of inStage.filter(entry => entry.kind === 'measured')) {
        const mismatch = mismatchOf(role, observation[role.role]);
        if (mismatch) return { outcome: 'unsupported', note: diagnose(role, observation[role.role], mismatch) };
    }
    return attributes ? attributeVerdict(attributes) : null;
}

/** Grade one case: geometry and DECSCNM, the controls of each stage, then the measured roles of that stage. */
function gradeCase(contract, observation) {
    const { rows, cols } = contract;
    const first = observation[contract.roles[0].role];
    if (first.rows < eraseCharsHistoryMinimum.rows || first.cols < eraseCharsHistoryMinimum.cols || first.rows !== rows || first.cols !== cols) {
        return { outcome: 'inconclusive', note: `Measured geometry ${first.rows}x${first.cols} cannot fit the ${contract.caseId} fixtures` };
    }
    if (observation.reverseVideo !== false) {
        return { outcome: 'inconclusive', note: 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it' };
    }
    const attributes = contract.family === 'erase-character' ? characterAttributes(observation) : null;
    for (const stage of ['pinned', 'extra']) {
        const verdict = gradeStage(contract, observation, stage, stage === 'pinned' ? attributes : null);
        if (verdict) return verdict;
    }
    return { outcome: 'supported', layers: contract.family === 'erase-character' ? ['stored cells', 'cursor', 'stored attributes', 'margins and modes']
        : ['actual scrollback', 'stored cells', 'cursor', 'margins and modes', 'active buffer'] };
}

/** The observed record: every role's compact viewport, history, margins, screen and cursor, with whether it matched the model. */
function briefCase(contract, observation) {
    const roles = Object.fromEntries(contract.roles.map(role => {
        const view = observation[role.role];
        const mismatch = mismatchOf(role, view);
        return [role.role, { kind: role.kind, matchesModel: mismatch === null, ...(mismatch ? { differs: mismatch.part } : {}),
            screen: view.alternate ? 'alternate' : 'main', margins: [view.top, view.bottom], cursor: view.cursor,
            viewport: compactLines(view.lines), history: compactLines(view.history), historyCount: view.count, totalLines: view.rows + view.count }];
    }));
    const first = observation[contract.roles[0].role];
    const common = { geometry: { rows: first.rows, cols: first.cols }, reverseVideo: observation.reverseVideo,
        units: 'physical rows: no wrapped (logical) lines are written, so a history count is a count of stored physical rows and totalLines is rows plus that count',
        roles };
    if (contract.family !== 'erase-character') return common;
    const attributes = characterAttributes(observation);
    return { ...common, spellingControls: { policy: 'observed, not graded: separate ECH spellings, kept distinct from the pinned explicit-3 spelling',
        names: ['omitted', 'explicit0', 'explicit1', 'explicit5'] },
    attributes: { policy: 'retained cells keep their stored tuple and erased cells carry no stale text attribute (required); the erased background is observed against a same-pen reference, not required',
        retainedKeepAttributes: attributes.retainedOk, erasedNoStaleAttributes: attributes.noStale, createdBlankBackground: attributes.background,
        createdBlankBackgroundDetail: attributes.detail } };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeEraseCharsHistoryCase(contract, observation, revision = eraseCharsHistoryContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown erase-character/saved-lines contracts revision ${revision}.`);
    if (!observation || contract.roles.some(role => !observation[role.role] || !Array.isArray(observation[role.role].lines))) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}

export { planFeeds as eraseCharsHistoryFeeds, planReads as eraseCharsHistoryReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateEraseCharsHistoryFeeds(contract, feeds) {
    validateExactFeeds(contract, feeds, 'contract');
}

