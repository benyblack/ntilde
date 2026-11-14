// Versioned input plan and independent oracle for the bounded ASCII reflow qualification batch:
// extensions.reflow (controlled resize/reflow of already-written ASCII text in the disposable main screen buffer).
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6,
// probeHash 16a690f34c0e): the extensions.reflow probe has no fixture, sends no resize request and reads no cells. Its termless
// arm consults only the declared reflow capability (`ctx.capabilities.reflow === true`), returns pass:false and observation
// inconclusive/insufficient-evidence (evidence "legacy") under the note "No controlled resize and cell-content readback to
// measure reflow"; its terminal path returns the same inconclusive without touching the terminal (evidence "none"). The pinned
// bundle declares no reflow capability for any terminal, so the pinned verdict is inconclusive by design and stays inconclusive.
//
// The separately identified effect contract (this module) is graded live against an independent reflow model that never calls
// the production algorithm: logical lines are re-laid-out by plain character slicing at each width, hard-line boundaries stay
// distinct, overflow scrolls complete physical rows out of the top into ordered history, and restoring width follows the
// documented viewport-anchor policy (VerticalGrow_ShouldNotPullHistory_AndAddPadding): the first visible logical-line offset
// stays visible, retained history is not pulled back, and vacated viewport rows become blank padding.
//
// Two experiments, both reported under the single benchmark id extensions.reflow:
//   reflow.width-roundtrip   long soft-wrapped hard lines redistribute at a smaller width while short hard lines, an
//                            authored-space line and a bold+underline attribute line stay intact; the cursor parked inside
//                            the first wrapped line moves with its logical offset; restoring the width rejoins every line.
//   reflow.history-roundtrip a full viewport of long hard lines rewraps into complete ordered history at a smaller grid; the
//                            cursor stays at the end of the last hard line; restoring follows the anchor policy with bottom
//                            padding instead of pulling history back.
// Character scope: stored ASCII single-width cells with neutral attributes plus one bold+underline probe. Authored spaces
// appear only as single spaces between position-pinning ink; unwritten row tails are never seeded. The App's cell projection
// cannot distinguish an authored trailing space from an unwritten cell, so no expectation depends on one. Unicode, wide-cell
// and grapheme reflow, images, rendered appearance, saved-bank behavior, remote delivery, other platforms and stock grades
// are not part of this contract.
import { compressExpected, matchesExpected } from './editing-expectations.mjs';
import { expandCellDetails } from './cell-details.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const asciiReflowContractsVersion = 1;
/** Identity of this suite's graded report. */
export const asciiReflowSuiteId = 'ntilde-owned-ascii-reflow-v1';
/** Identity of the separately identified effect contract. */
export const asciiReflowEffectContractId = 'ntilde-ascii-reflow-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const asciiReflowNoncePrefix = 'NTILDE_RF_';

/** The measured reset every fixture begins with: leave the alternate screen, DECSTR, origin/margins/insert off, SGR off, clear screen and history, home. */
export const asciiReflowReset = '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H';
/** Final cleanup: the measured reset plus the default visible cursor. */
export const asciiReflowFinalRestore = asciiReflowReset + '\x1b[?25h';

/** The pinned extensions.reflow criterion, byte for byte as retained by the terminfo.dev@4.0.0 bundle. */
export const asciiReflowPinned = Object.freeze({
    probeId: 'extensions.reflow',
    note: 'No controlled resize and cell-content readback to measure reflow',
    feeds: Object.freeze([]),
    termlessAlwaysInconclusive: true,
    pinnedCriterion: 'the pinned probe consults only the declared reflow capability (ctx.capabilities.reflow === true), returns pass:false and observation '
        + 'inconclusive/insufficient-evidence (termless evidence "legacy"); the terminal path returns the same inconclusive without touching the terminal '
        + '(evidence "none")',
    terminalFixture: { writes: [], demand: 'none: the pinned terminal path never touches the terminal and never carries a verdict', minimum: { rows: 1, cols: 1 },
        note: 'the pinned terminal path never carries a verdict' },
});

const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const LETTERS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
/** Stored neutral cell: the complete cell-detail tuple the observer reads (default color markers 256|512, no underline). */
const cell = (text = ' ') => ({ text, overline: false, flags: 768, fg: -1, bg: -1,
    foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null });
/** Bold + underline probe cells keep the default color markers plus bold(1) and underline(8) with stored style 1. */
const attrCell = (text = ' ') => ({ ...cell(text), flags: 777, underlineStyle: 1 });
const geometryUnavailable = reason => Object.assign(new Error('Unsupported bounded ASCII reflow grid: ' + reason), { code: 'asciiReflowGeometryUnavailable' });
const physicalRows = (length, width) => Math.max(1, Math.ceil(length / width));

/** Long-line text: the line's own rotated alphabet, with a decimal offset marker every ten columns, so every observed cell
 * pins its logical line and offset and any lost, duplicated, reordered or merged content breaks the exact grid. */
function longText(lineIndex, length) {
    let out = '';
    for (let offset = 0; offset < length; offset++) {
        out += offset % 10 === 9 ? String(Math.floor(offset / 10) % 10) : LETTERS[(offset + 7 * lineIndex) % 26];
    }
    return out;
}

/** Short hard line: fixed 15-character text whose single spaces sit between position-pinning ink. */
const shortText = index => 'RFs' + String(index).padStart(3, '0') + ' ATTR REST';

/** One logical line of the plan: exact text plus its optional inclusive bold+underline character span. */
const line = (text, attrSpan = null) => ({ text, attrSpan });

/** The exact fixture bytes for one logical line at its physical start row: a positioned write, with the attribute probe's
 * bold+underline segment sent under SGR 1;4 and explicitly reset before the rest of the line. */
function lineBytes(item, row) {
    if (item.attrSpan === null) return at(row, 0) + item.text;
    return at(row, 0) + '\x1b[1;4m' + item.text.slice(0, item.attrSpan[1] + 1) + '\x1b[0m' + item.text.slice(item.attrSpan[1] + 1);
}

/** The independent reflow model: naive character slicing of every logical line at `cols`, top overflow into ordered complete
 * history, blank padding below short content, and the cursor mapped by its logical line and offset. `anchoredHistory` pins the
 * documented viewport-anchor outcome when growing instead of re-deriving the shrink overflow. */
function expectedState(spec, rows, cols, anchoredHistory) {
    const counts = spec.lines.map(item => physicalRows(item.text.length, cols));
    const physical = spec.lines.flatMap((item, index) => Array.from({ length: counts[index] }, (_, part) => ({
        text: item.text.slice(part * cols, (part + 1) * cols),
        from: item.attrSpan === null ? Infinity : item.attrSpan[0] - part * cols,
        to: item.attrSpan === null ? -Infinity : item.attrSpan[1] - part * cols,
        hasAttr: item.attrSpan !== null && item.attrSpan[1] >= part * cols && item.attrSpan[0] < (part + 1) * cols,
    })));
    const historyCount = anchoredHistory ?? Math.max(0, counts.reduce((total, count) => total + count, 0) - rows);
    const prior = counts.slice(0, spec.cursorLine).reduce((total, count) => total + count, 0);
    const row = prior + Math.floor(spec.cursorOffset / cols) - historyCount;
    if (row < 0 || row >= rows) throw geometryUnavailable('the planned cursor would not be visible');
    const viewport = physical.slice(historyCount);
    while (viewport.length < rows) viewport.push({ text: '' });
    const grid = viewport.map(item => Array.from({ length: cols }, (_, col) =>
        item.hasAttr && col >= item.from && col <= item.to ? attrCell(item.text[col] ?? ' ') : cell(item.text[col] ?? ' ')));
    return { grid, row, col: spec.cursorOffset % cols, top: 0, bottom: rows - 1, alternate: false, originMode: false,
        history: physical.slice(0, historyCount).map(item => item.text) };
}

/** The documented growing policy: the first visible logical-line offset at the smaller width stays visible at the restored
 * width; retained history is never pulled back into the viewport. */
function restoredAnchor(spec, smallerCols, smallerHistory, restoredCols) {
    let remaining = smallerHistory, restored = 0;
    for (const item of spec.lines) {
        const smallerRows = physicalRows(item.text.length, smallerCols);
        if (remaining < smallerRows) return restored + Math.floor(remaining * smallerCols / restoredCols);
        remaining -= smallerRows;
        restored += physicalRows(item.text.length, restoredCols);
    }
    throw geometryUnavailable('the resize anchor exceeded the known hard-line plan');
}

/** The exact seed bytes for one experiment: measured reset, every hard line written once at its planned physical start row,
 * then the cursor parked at its planned logical offset. No row tail beyond the written content is ever seeded. */
function seedBytes(spec, cols) {
    const counts = spec.lines.map(item => physicalRows(item.text.length, cols));
    let out = asciiReflowReset, cursor = 0;
    spec.lines.forEach((item, index) => {
        out += lineBytes(item, cursor);
        cursor += counts[index];
    });
    const prior = counts.slice(0, spec.cursorLine).reduce((total, count) => total + count, 0);
    return out + at(prior + Math.floor(spec.cursorOffset / cols), spec.cursorOffset % cols);
}

/** Two explicit shrink/restore plans; complete history and cell details must fit their read budgets. */
export function makeReflowCases(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < 12 || rows > 52 || !Number.isSafeInteger(cols)
        || cols < 55 || cols > 256 || rows * cols > 8192) throw geometryUnavailable('outside the 12-52 row, 55-256 column, 8192-cell envelope');
    if (rows < 24) throw geometryUnavailable('the width fixture needs at least 24 rows');
    const smallerCols = cols - 7, smallerRows = rows - 4;
    // Experiment A: six long hard lines interleaved with short ones; the smaller width moves each long line's wrap point and
    // the parked cursor's column right by exactly the removed columns, without displacing any row into history.
    const longLength = cols + Math.floor(cols / 2);
    const cursorOffset = cols + Math.floor(cols / 3);
    const widthLine = index => {
        if (index % 2 === 0) return line(longText(index / 2, longLength));
        return line(shortText(Math.floor(index / 2)), index === 1 ? [0, 4] : null);
    };
    const widthLines = Array.from({ length: 12 }, (_, index) => widthLine(index));
    const fillerCount = Math.max(0, rows - 12 - widthLines.length);
    for (let index = 6; index < 6 + fillerCount; index++) widthLines.push(line(shortText(index)));
    const widthSpec = { id: 'reflow.width-roundtrip', lines: widthLines, cursorLine: 0, cursorOffset };
    // Experiment B: one full viewport of long hard lines; the smaller grid displaces complete ordered rows into history and
    // the cursor follows the last line's end.
    const historySpec = { id: 'reflow.history-roundtrip', lines: Array.from({ length: rows }, (_, index) => line(longText(index, cols - 2))),
        cursorLine: rows - 1, cursorOffset: cols - 2 };
    const specs = [
        { spec: widthSpec, target: { rows, cols: smallerCols } },
        { spec: historySpec, target: { rows: smallerRows, cols: smallerCols } },
    ];
    return specs.map(({ spec, target }) => {
        const before = expectedState(spec, rows, cols);
        const after = expectedState(spec, target.rows, target.cols);
        if (after.history.length > 64) throw geometryUnavailable('the shrink plan exceeds the complete 64-row history read budget');
        if (spec === widthSpec && after.history.length !== 0) throw geometryUnavailable('the width plan must not displace history');
        if (spec === historySpec && after.history.length < 1) throw geometryUnavailable('the history plan must displace at least one complete row');
        // The width plan's cursor must sit inside the first line's wrapped continuation at both widths, strictly off every
        // wrap boundary, and change column with the width, so its mapping proves a wrapped row, not a lucky same-position read.
        if (spec === widthSpec && (Math.floor(spec.cursorOffset / cols) < 1 || Math.floor(spec.cursorOffset / target.cols) < 1
            || spec.cursorOffset % cols === spec.cursorOffset % target.cols
            || spec.cursorOffset % cols === 0 || spec.cursorOffset % target.cols === 0)) {
            throw geometryUnavailable('the planned cursor would not prove wrapped-row mapping');
        }
        const anchor = restoredAnchor(spec, target.cols, after.history.length, cols);
        const restored = expectedState(spec, rows, cols, anchor);
        return { id: spec.id, setup: seedBytes(spec, cols), target, before, after, restored };
    });
}

/** The two fixed experiment ids in run order. */
export const reflowExperimentIds = Object.freeze(['reflow.width-roundtrip', 'reflow.history-roundtrip']);
/** Plan stages per experiment, in run order; `after` and `restored` are parent-selected resize actions. */
export const reflowStageIds = Object.freeze(['control', 'after', 'restored']);
/** The single pinned benchmark case of this suite. */
export const asciiReflowCaseIds = Object.freeze(['extensions.reflow']);
/** The recorded resize-action policy of this suite, beside the historical resize one. */
export const ownedAsciiReflowPolicy = 'parent-selected App resize in proved private pane; independent kernel geometry and -opost; two ASCII reflow experiments on one disposable main screen';

/** The expected geometry of one experiment stage: control and restored keep the original grid, after takes the plan target. */
export function reflowStageTarget(cases, caseId, stageId) {
    const item = cases.find(entry => entry.id === caseId);
    if (!item || !reflowStageIds.includes(stageId)) throw new Error('Unknown ASCII reflow plan stage.');
    const original = { rows: item.before.grid.length, cols: item.before.grid[0].length };
    return stageId === 'after' ? item.target : original;
}

/** The independent model state of one experiment stage. */
export function reflowStageState(cases, caseId, stageId) {
    const item = cases.find(entry => entry.id === caseId);
    if (!item || !reflowStageIds.includes(stageId)) throw new Error('Unknown ASCII reflow plan stage.');
    const byStage = { control: item.before, after: item.after, restored: item.restored };
    return byStage[stageId];
}

/** Whether one stage's complete observation equals its independently derived model state: every stored cell (including the
 * bold+underline probe), the cursor, the modes, and the complete ordered history text. */
export function matchesReflowState(screen, expected) {
    return matchesExpected(screen, compressExpected(expected),
        expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true));
}

// ---------------------------------------------------------------------------------------------------------------------------
// Grading.

const inconclusive = note => ({ outcome: 'inconclusive', note });

/** The effect verdict. Inconclusive without every prepared seed (an incomplete binding, seed or complete history read cannot
 * become evidence); unsupported at the first stage that differs from the model; supported only when both experiments' full
 * shrink/restore sequences reproduce the independent model exactly. */
export function gradeReflowContract(stages) {
    const seeds = [
        [() => stages.width !== undefined && stages.history !== undefined, 'Both reflow experiments were not measured'],
        [() => stages.width.initial.geometry.cols > stages.width.shrunk.geometry.cols, 'The width experiment did not actually reduce the width'],
        [() => stages.history.initial.geometry.cols > stages.history.shrunk.geometry.cols
            && stages.history.initial.geometry.rows > stages.history.shrunk.geometry.rows, 'The history experiment did not actually reduce the grid'],
        [() => stages.width.restored.geometry.cols === stages.width.initial.geometry.cols
            && stages.history.restored.geometry.cols === stages.history.initial.geometry.cols
            && stages.history.restored.geometry.rows === stages.history.initial.geometry.rows, 'The original geometry was not restored before the next experiment or at the end'],
    ];
    const gap = seeds.find(([holds]) => !holds());
    if (gap) return inconclusive(gap[1]);
    const checks = [
        ['width.control', stages.width.initial], ['width.after', stages.width.shrunk], ['width.restored', stages.width.restored],
        ['history.control', stages.history.initial], ['history.after', stages.history.shrunk], ['history.restored', stages.history.restored],
    ];
    const failed = checks.find(([, observed]) => !observed.matches);
    if (failed) {
        return unsupportedNote(failed[0]);
    }
    return { outcome: 'supported' };
}

/** The stage-specific refusal note: a difference from the model names what the reflow got wrong. */
function unsupportedNote(stage) {
    const notes = {
        'width.control': 'The prepared width fixture differs from the independent plan, so no reflow conclusion can be drawn from it',
        'width.after': 'At the smaller width the stored cells, cursor, attributes or modes differ from the independent character-slicing model: content was lost, duplicated, reordered, rewrapped at the wrong boundary, merged across a hard line, or the cursor did not follow its logical offset',
        'width.restored': 'After the width was restored the rejoined hard lines, cursor or attributes differ from the original layout, so the round trip did not preserve the logical content',
        'history.control': 'The prepared history fixture differs from the independent plan, so no reflow conclusion can be drawn from it',
        'history.after': 'At the smaller grid the displaced history, its order, the rewrapped viewport or the cursor differ from the independent model: history rows were lost, duplicated, reordered or truncated, hard lines merged, or the cursor did not follow its logical offset',
        'history.restored': 'After the original geometry was restored the viewport-anchor mapping, retained history, bottom padding or cursor differ from the documented top-anchor policy',
    };
    return { outcome: 'unsupported', note: notes[stage] ?? 'A stage differed from the independent reflow model' };
}

/** A stage's head rows as text, `_` for blank, for the published observation brief. */
const render = (screen, rows) => rows.map(row => (screen.lines[row] ?? '').padEnd(Math.min(screen.cols, 24), '_').slice(0, 24));
/** The observed record: per-stage geometry, cursor, history count and head rows, kept bounded for the published case file. */
export function briefReflowObservation(stages) {
    const brief = stage => ({
        geometry: stage.geometry, cursor: stage.cursor, history: stage.historyCount,
        head: render(stage.screen, [0, 1, 2, 3, 4, 5]),
        matchesModel: stage.matches,
    });
    return {
        width: { initial: brief(stages.width.initial), shrunk: brief(stages.width.shrunk), restored: brief(stages.width.restored) },
        history: { initial: brief(stages.history.initial), shrunk: brief(stages.history.shrunk), restored: brief(stages.history.restored) },
    };}

/** Independent effect grader entry: validates the stage record shape, then grades. */
export function gradeReflowCase(stages, revision = asciiReflowContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown ASCII reflow contracts revision ${revision}.`);
    if (!stages?.width || !stages?.history) throw new Error('Missing extensions.reflow stage captures.');
    for (const experiment of ['width', 'history']) {
        for (const stage of ['initial', 'shrunk', 'restored']) {
            const observed = stages[experiment][stage];
            if (!Array.isArray(observed?.screen?.cellDetails)) {
                throw new TypeError(`Missing ${experiment}.${stage} read captures.`);
            }
        }
    }
    return gradeReflowContract(stages);
}

/** The fixed fixture facts, exposed so tests can cross-check them. */
export const asciiReflowFixture = Object.freeze({ pinned: asciiReflowPinned, reset: asciiReflowReset, finalRestore: asciiReflowFinalRestore });
/** The independent model, exposed so tests can cross-check it. */
export const asciiReflowModel = Object.freeze({ expectedState, restoredAnchor, physicalRows, longText, shortText });
