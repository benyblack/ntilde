// The effect contract is distinct from the pinned termless and terminal paths.
// It covers ASCII stored cells only, with DECAWM disabled throughout.
import { planFeed, planRead, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsExpand, sgrAttrsPredicates, sgrAttrsReset, sgrAttrsClear, validateSgrAttrsScreen } from './sgr-attrs-contracts.mjs';

export const insertReplaceContractsVersion = 1;
export const insertReplaceSuiteId = 'ntilde-owned-insert-replace-effect-v1';
export const insertReplaceNoncePrefix = 'NTILDE_IRM_';
export const insertReplaceReset = `${sgrAttrsReset}\x1b[?69l\x1b[?7l`;
export const insertReplaceCaseIds = Object.freeze(['modes.insert-replace']);
export const insertReplacePinnedFeeds = Object.freeze(['\x1b[4l\x1b[1;1H\x1b[2KABC', '\x1b[1;1H', '\x1b[4h', 'X', '\x1b[4l']);
export const insertReplaceTerminalWrites = Object.freeze(['\x1b[1;1H\x1b[2K', 'ABCD', '\x1b[1;2H', '\x1b[4h', 'X', '\x1b[4l']);

const groups = Object.freeze([
    { role: 'initial', feeds: [] },
    { role: 'seed', feeds: [insertReplacePinnedFeeds[0]] },
    { role: 'home', feeds: [insertReplacePinnedFeeds[1]] },
    { role: 'enabled', feeds: [insertReplacePinnedFeeds[2]] },
    { role: 'inserted', feeds: [insertReplacePinnedFeeds[3]] },
    { role: 'disabled', feeds: [insertReplacePinnedFeeds[4]] },
    { role: 'replaced', feeds: ['Y'] },
    { role: 'wideBefore', feeds: [sgrAttrsClear, '\x1b[1;1HABOVE-ROW!\x1b[2;1HABCDEFGHIJ\x1b[3;1HBELOW-ROW!\x1b[2;3H'] },
    { role: 'wideInserted', feeds: ['\x1b[4hxy'] },
    { role: 'wideReplaced', feeds: ['\x1b[4lZ'] },
    { role: 'edgeBefore', feeds: [sgrAttrsClear, '\x1b[1;1H\x1b[999C\x1b[2DWXY\x1b[2D'] },
    { role: 'edgeInserted', feeds: ['\x1b[4hQR\x1b[4l'] },
    { role: 'attrBefore', feeds: [sgrAttrsClear, '\x1b[1;1HABC\x1b[1mDE\x1b[0mFGH\x1b[1;4H\x1b[44m'] },
    { role: 'attrInserted', feeds: ['\x1b[4hxy\x1b[4l'] },
    { role: 'pen', feeds: [sgrAttrsClear, '\x1b[44mxy'] },
    { role: 'terminal', feeds: [sgrAttrsClear, ...insertReplaceTerminalWrites] },
]);
const plan = [planFeed(insertReplaceReset)];
for (const group of groups) {
    for (const bytes of group.feeds) plan.push(planFeed(bytes));
    plan.push(planRead('cells'));
}
export const insertReplaceContract = Object.freeze({ caseId: 'modes.insert-replace', roles: Object.freeze(groups.map(group => group.role)),
    pinnedFeeds: insertReplacePinnedFeeds, plan: Object.freeze(plan) });
export const insertReplaceFeeds = () => planFeeds(insertReplaceContract);
export const insertReplaceReads = () => planReads(insertReplaceContract);
export const validateInsertReplaceFeeds = feeds => validateExactFeeds(insertReplaceContract, feeds, 'insert/replace contract');

/** Convert a validated owned screen into dense, independently comparable cells. */
function state(screen) {
    validateSgrAttrsScreen(screen);
    const facts = screen.bufferState;
    if (!facts || !Number.isSafeInteger(facts.scrollTop) || !Number.isSafeInteger(facts.scrollBottom)
        || typeof facts.originMode !== 'boolean' || typeof facts.autoWrap !== 'boolean' || typeof facts.insertMode !== 'boolean'
        || typeof facts.pendingWrap !== 'boolean' || typeof facts.alternateScreen !== 'boolean'
        || !Number.isSafeInteger(facts.scrollbackLines)) throw new Error('Missing or malformed IRM buffer-state evidence.');
    const expanded = sgrAttrsExpand(screen);
    if (!screen.history || !Array.isArray(screen.history.lines) || !screen.horizontalMargins
        || typeof screen.horizontalMargins.leftRightMarginMode !== 'boolean' || !Number.isSafeInteger(screen.horizontalMargins.leftMargin)
        || !Number.isSafeInteger(screen.horizontalMargins.rightMargin)) throw new Error('Missing IRM history or horizontal-margin evidence.');
    return { rows: screen.rows, cols: screen.cols, cursor: [screen.cursorRow, screen.cursorCol], visible: screen.cursorVisible, facts, history: screen.history,
        horizontalMargins: screen.horizontalMargins,
        cells: Array.from({ length: screen.rows }, (_, row) => Array.from({ length: screen.cols }, (_, col) => sgrAttrsCellAt(screen, row, col, expanded))) };
}

const equal = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const cursor = (screen, row, col) => equal(screen.cursor, [row, col]);
const neutral = sgrAttrsPredicates.neutral;
const boldOnly = cell => sgrAttrsPredicates.exactly(cell, ['bold']);
const textRows = (screen, rows) => screen.cells.every((cells, row) => cells.every((cell, col) =>
    cell.char === ((rows[row] ?? '')[col] ?? ' ') && neutral(cell)));
const attrSeed = screen => screen.cells.every((cells, row) => cells.every((cell, col) => {
    const text = row === 0 ? 'ABCDEFGH' : '';
    return cell.char === (text[col] ?? ' ') && (row === 0 && (col === 3 || col === 4) ? boldOnly(cell) : neutral(cell));
}));
const withChar = (cell, char) => ({ ...cell, char });

/** Independent row model: insertion shifts stored tuples, replacement overwrites. */
export function insertReplaceModel(cells, col, charsToPrint, insert, pen) {
    const result = cells.map(row => row.map(cell => ({ ...cell })));
    for (const char of charsToPrint) {
        const row = result[col[0]], column = col[1];
        if (insert) {
            row.splice(column, 0, withChar(pen, char));
            row.pop();
        } else row[column] = withChar(pen, char);
        col = [col[0], Math.min(column + 1, row.length - 1)]; // DECAWM is disabled
    }
    return { cells: result, cursor: col };
}

/** Derive the effect verdict from the ordered complete screens; identity and cleanup are receipt gates. */
export function gradeInsertReplaceEffect(screens) {
    if (!Array.isArray(screens) || screens.length !== groups.length) throw new Error('Missing IRM read captures.');
    const states = Object.fromEntries(groups.map((group, index) => [group.role, state(screens[index])]));
    const s = states.seed;
    if (s.rows < 3 || s.cols < 12) return { outcome: 'inconclusive', note: 'Measured geometry cannot fit IRM controls.' };
    if (screens.some(screen => screen.colorBinding?.reverseVideo !== false)) return { outcome: 'inconclusive', note: 'Reverse video was not proven off.' };
    const baseline = states.initial;
    const modeOn = new Set(['enabled', 'inserted', 'wideInserted']);
    for (const [role, item] of Object.entries(states)) {
        const f = item.facts;
        if (item.rows !== s.rows || item.cols !== s.cols || item.visible !== baseline.visible || f.scrollTop !== 0 || f.scrollBottom !== s.rows - 1
            || f.originMode || f.autoWrap || f.pendingWrap || !f.alternateScreen || f.insertMode !== modeOn.has(role)
            || f.scrollbackLines !== baseline.facts.scrollbackLines || !equal(item.history, baseline.history)
            || item.horizontalMargins.leftRightMarginMode || item.horizontalMargins.leftMargin !== 0 || item.horizontalMargins.rightMargin !== s.cols - 1) {
            return { outcome: 'inconclusive', note: `Unverified IRM mode, geometry or unrelated state at ${role}.` };
        }
    }
    const edge = ' '.repeat(s.cols - 3) + 'WXY';
    const pen = states.pen.cells[0][0];
    const validPen = pen.char === 'x' && pen.bgIndex === 4 && pen.bgSource === 'palette' && pen.fgIndex === -1
        && pen.fgSource === 'default' && pen.foregroundArgb === null && pen.backgroundArgb === null
        && ['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline'].every(key => pen[key] === false)
        && pen.underlineStyle === 0 && pen.underlineColorIndex === -1 && pen.underlineArgb === null
        && equal(withChar(pen, 'y'), states.pen.cells[0][1])
        && states.pen.cells.every((row, r) => row.every((cell, c) => (r === 0 && c < 2) || (cell.char === ' ' && neutral(cell))));
    const controls = textRows(states.initial, []) && cursor(states.initial, 0, 0)
        && textRows(s, ['ABC']) && cursor(s, 0, 3)
        && textRows(states.home, ['ABC']) && cursor(states.home, 0, 0)
        && equal(states.home.cells, states.enabled.cells) && cursor(states.enabled, 0, 0)
        && textRows(states.wideBefore, ['ABOVE-ROW!', 'ABCDEFGHIJ', 'BELOW-ROW!']) && cursor(states.wideBefore, 1, 2)
        && textRows(states.edgeBefore, [edge]) && cursor(states.edgeBefore, 0, s.cols - 3)
        && attrSeed(states.attrBefore) && cursor(states.attrBefore, 0, 3) && validPen && cursor(states.pen, 0, 2);
    if (!controls) return { outcome: 'inconclusive', note: 'A seed, cursor or pen control was not established.' };
    const defaultPen = s.cells[0][0];
    const check = (before, after, at, text, insert, currentPen) => {
        const expected = insertReplaceModel(before.cells, at, text, insert, currentPen);
        return equal(expected.cells, after.cells) && equal(expected.cursor, after.cursor);
    };
    if (!check(states.enabled, states.inserted, [0, 0], 'X', true, defaultPen)
        || !equal(states.inserted.cells, states.disabled.cells) || !cursor(states.disabled, 0, 1)
        || !check(states.disabled, states.replaced, [0, 1], 'Y', false, defaultPen)
        || !check(states.wideBefore, states.wideInserted, [1, 2], 'xy', true, defaultPen)
        || !check(states.wideInserted, states.wideReplaced, [1, 4], 'Z', false, defaultPen)
        || !check(states.edgeBefore, states.edgeInserted, [0, s.cols - 3], 'QR', true, defaultPen)
        || !check(states.attrBefore, states.attrInserted, [0, 3], 'xy', true, pen)) {
        return { outcome: 'unsupported', note: 'Printing, cursor, shifted attributes or an unaffected cell differed from the model.' };
    }
    return { outcome: 'supported' };
}
