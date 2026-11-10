// Versioned probe contract for the left/right-margin qualification batch: modes.left-right-margin (DECLRMM ?69 with DECSLRM).
//
// Pinned terminfo.dev@4.0.0 definition (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6,
// probeHash 16a690f34c0e). The termless probe feeds ESC[?69h, reads the mode metadata (getMode("leftRightMargin")), feeds ESC[?69l and
// returns parserStateResult(null, ...): ALWAYS inconclusive ("Mode metadata does not measure margins"), whatever the mode read. The
// terminal path returns inconclusive without touching the terminal. So the pinned verdict is inconclusive by design and is preserved;
// the response records the mode the App reported.
//
// The separately identified effect contract (docs/qa/terminfo/left-right-margins.md) is graded live: an independent margins model
// written for this suite interprets each control's operations over a measured block seed (ten characters at the left edge and ten at
// the right edge of seven rows) and must reproduce the App's whole screen, cursor, stored margins, DECLRMM, origin and insert mode
// after every control. Controls:
//   mode:      SCOSC while DECLRMM is reset (CSI Pl;Pr s saves the cursor, sets no margins); DECSLRM with margins 5-14 homes the
//              cursor; DECSLRM refusals (equal and inverted pairs) change nothing; resetting ?69 and DECSTR restore full width;
//   printing:  text wraps at the right margin back to the left margin; insert mode pushes text only to the right margin;
//   movement:  CUF/CUB stop at the margins from inside and at the page edges from outside; CR returns to the left margin from inside
//              and to the first column from outside; HT stops at the right margin;
//   scrolling: LF at the bottom margin scrolls only the band between the margins and does nothing outside them; RI at the top
//              margin; SU/SD wherever the cursor is; IL/DL; ICH/DCH (no-ops outside the margins);
//   columns:   DECIC/DECDC/SL/SR move only the columns between the margins;
//   origin:    DECOM homes to the top-left margin corner, CUP columns count from the left margin and clamp to the right margin, and a
//              DECERA rectangle's columns are offset by the left margin.
// Character scope: stored ASCII single-width cells with neutral attributes. CPR, wide glyphs, side tables, images and rendered
// appearance are covered by unit tests, not qualified here. DECRQSS for the margins, DECBI/DECFI and ED/EL/ECH bounding are not part of
// the contract.
import { planFeed, planRead, validateExactFeeds } from './graded-plan.mjs';
import { sgrAttrsReset } from './sgr-attrs-contracts.mjs';
import { selectiveEraseInk, validateSelectiveEraseScreen } from './selective-erase-contracts.mjs';
import { colEditFixture } from './col-edit-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const lrMarginsContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const lrMarginsSuiteId = 'ntilde-owned-left-right-margins-graded-v1';
/** Identity of the separately identified effect contract. */
export const lrMarginsEffectContractId = 'ntilde-left-right-margins-effect-v1';
/** The nonce prefix binding worker output to this suite. */
export const lrMarginsNoncePrefix = 'NTILDE_LM_';
/** The measured reset every plan begins with: DECLRMM, protection, DECSCNM, DECOM, margins, IRM and SGR off, screen cleared, home. */
export const lrMarginsReset = '\x1b[?69l\x1b[0"q' + sgrAttrsReset;
/** Margin screens: stored attributes, protection, buffer state, history and the App's stored horizontal margins. */
export function validateLrMarginsScreen(screen) {
    validateSelectiveEraseScreen(screen);
    const margins = screen.horizontalMargins;
    if (!margins || typeof margins.leftRightMarginMode !== 'boolean' || !Number.isSafeInteger(margins.leftMargin)
        || !Number.isSafeInteger(margins.rightMargin) || margins.leftMargin < 0 || margins.rightMargin >= screen.cols || margins.leftMargin > margins.rightMargin) {
        throw new Error('Left/right-margin screens need the stored horizontal margins.');
    }
}

/** The pinned fixture, byte for byte, and the pinned texts. */
export const pinned = Object.freeze({
    set: '\x1b[?69h', reset: '\x1b[?69l',
    expected: 'Left-right margin mode constrains horizontal operations',
    note: 'Mode metadata does not measure margins',
});

/** The measured geometry the controls need. */
const minimum = Object.freeze({ rows: 13, cols: 48 });
/** Between groups: DECLRMM (and so the margins), region, origin, insert mode, SGR and protection off, screen cleared, cursor home. */
const clear = '\x1b[?69l\x1b[r\x1b[?6l\x1b[4l\x1b[0m\x1b[0"q\x1b[2J\x1b[H';
const { leftTexts, rightTexts, blockFeed } = colEditFixture;

// ---------------------------------------------------------------------------------------------------------------------------
// Operations: the bytes each sends, and the independent model's reading of them.

const csi = body => `\x1b[${body}`;
const op = (bytes, apply) => Object.freeze({ bytes, apply });
const ops = Object.freeze({
    cup: (row, col) => op(csi(`${row};${col}H`), m => m.cup(row, col)),
    text: text => op(text, m => { for (const char of text) m.print(char); }),
    cuf: n => op(csi(`${n}C`), m => m.cuf(n)),
    cub: n => op(csi(`${n}D`), m => m.cub(n)),
    cr: () => op('\r', m => m.cr()),
    lf: () => op('\n', m => m.lf()),
    ri: () => op('\x1bM', m => m.ri()),
    ht: () => op('\t', m => m.ht()),
    su: n => op(csi(`${n}S`), m => { for (let i = 0; i < n; i++) m.scroll(true); }),
    sd: n => op(csi(`${n}T`), m => { for (let i = 0; i < n; i++) m.scroll(false); }),
    il: n => op(csi(`${n}L`), m => m.lines(n, true)),
    dl: n => op(csi(`${n}M`), m => m.lines(n, false)),
    ich: n => op(csi(`${n}@`), m => m.chars(n, true)),
    dch: n => op(csi(`${n}P`), m => m.chars(n, false)),
    decic: n => op(csi(`${n}'}`), m => m.columns(n, true, true)),
    decdc: n => op(csi(`${n}'~`), m => m.columns(n, true, false)),
    sl: n => op(csi(`${n} @`), m => m.columns(n, false, false)),
    sr: n => op(csi(`${n} A`), m => m.columns(n, false, true)),
    stbm: (top, bottom) => op(csi(`${top};${bottom}r`), m => m.stbm(top, bottom)),
    origin: on => op(csi(`?6${on ? 'h' : 'l'}`), m => m.setOrigin(on)),
    irm: on => op(csi(`4${on ? 'h' : 'l'}`), m => { m.insert = on; }),
    lrmm: on => op(csi(`?69${on ? 'h' : 'l'}`), m => m.setMode(on)),
    slrm: (left, right) => op(csi(`${left ?? ''}${right === undefined ? '' : ';' + right}s`), m => m.slrm(left, right)),
    scorc: () => op(csi('u'), m => m.scorc()),
    decera: (top, left, bottom, right) => op(csi(`${top};${left};${bottom};${right}$z`), m => m.decera(top, left, bottom, right)),
    decstr: () => op(csi('!p'), m => m.decstr()),
});

/** The independent margins model: a grid of single characters with a cursor, the region, the margins and the modes. */
class MarginsModel {
    constructor(rows, cols) {
        this.rows = rows; this.cols = cols;
        this.grid = Array.from({ length: rows }, () => Array.from({ length: cols }, () => ' '));
        Object.assign(this, { row: 0, col: 0, pending: false, top: 0, bottom: rows - 1, left: 0, right: cols - 1,
            mode: false, origin: false, insert: false, saved: [0, 0] });
    }
    get inMargins() { return this.col >= this.left && this.col <= this.right; }
    cup(row, col) {
        const r = row - 1 + (this.origin ? this.top : 0);
        this.row = this.origin ? Math.min(Math.max(r, this.top), this.bottom) : Math.min(Math.max(r, 0), this.rows - 1);
        this.col = this.origin ? Math.min(Math.max(col - 1 + this.left, this.left), this.right) : Math.min(Math.max(col - 1, 0), this.cols - 1);
        this.pending = false;
    }
    print(char) {
        if (this.pending) { this.col = this.left; this.lf(); }
        const edge = this.col <= this.right ? this.right : this.cols - 1;
        if (this.insert && this.inMargins) this.shift(this.row, this.col, this.right, 1, true);
        this.grid[this.row][this.col] = char;
        if (this.col + 1 > edge) { this.pending = true; this.col = edge; } else this.col += 1;
    }
    cuf(n) { this.col = Math.min(this.col <= this.right ? this.right : this.cols - 1, this.col + n); this.pending = false; }
    cub(n) { this.col = Math.max(this.col >= this.left ? this.left : 0, this.col - n); this.pending = false; }
    cr() { this.col = this.origin || this.col >= this.left ? this.left : 0; this.pending = false; }
    lf() {
        if (this.row === this.bottom) { if (this.inMargins) this.scroll(true); } else this.row = Math.min(this.row + 1, this.rows - 1);
        this.pending = false;
    }
    ri() {
        if (this.row === this.top) { if (this.inMargins) this.scroll(false); } else this.row = Math.max(this.row - 1, 0);
        this.pending = false;
    }
    ht() {
        const limit = this.col <= this.right ? this.right : this.cols - 1;
        let next = this.col + 1;
        while (next < this.cols - 1 && next % 8 !== 0) next++;
        this.col = Math.min(next, limit); this.pending = false;
    }
    /** Move the band of rows top..bottom, columns left..right, up or down by n; vacated band rows go blank. */
    band(top, bottom, n, up) {
        const width = this.right - this.left + 1;
        const copy = this.grid.slice(top, bottom + 1).map(line => line.slice(this.left, this.left + width));
        for (let y = 0; y <= bottom - top; y++) {
            const source = up ? y + n : y - n;
            for (let x = 0; x < width; x++) this.grid[top + y][this.left + x] = source >= 0 && source <= bottom - top ? copy[source][x] : ' ';
        }
    }
    scroll(up) { this.band(this.top, this.bottom, 1, up); }
    lines(n, insert) {
        if (this.row < this.top || this.row > this.bottom || !this.inMargins) return;
        this.band(this.row, this.bottom, Math.min(n, this.bottom - this.row + 1), !insert);
    }
    /** Insert (blanks at `start`) or delete (blanks at `end`) n cells of one row between `start` and `end` inclusive. */
    shift(row, start, end, n, insert) {
        const cells = this.grid[row].slice(start, end + 1);
        const count = Math.min(n, cells.length);
        const moved = insert ? [...Array(count).fill(' '), ...cells.slice(0, cells.length - count)] : [...cells.slice(count), ...Array(count).fill(' ')];
        moved.forEach((char, index) => { this.grid[row][start + index] = char; });
    }
    chars(n, insert) { if (this.inMargins) this.shift(this.row, this.col, this.right, n, insert); }
    columns(n, atCursor, insert) {
        if (this.row < this.top || this.row > this.bottom || !this.inMargins) return;
        const start = atCursor ? this.col : this.left;
        for (let row = this.top; row <= this.bottom; row++) this.shift(row, start, this.right, n, insert);
    }
    home() { this.row = this.origin ? this.top : 0; this.col = this.origin ? this.left : 0; this.pending = false; }
    stbm(top, bottom) { if (top < bottom && bottom <= this.rows) { this.top = top - 1; this.bottom = bottom - 1; } this.home(); }
    setOrigin(on) { this.origin = on; this.home(); }
    setMode(on) { this.mode = on; if (!on) { this.left = 0; this.right = this.cols - 1; } }
    slrm(left, right) {
        if (!this.mode) { this.saved = [this.row, this.col]; return; }
        const l = ((left ?? 0) > 0 ? left : 1) - 1;
        const r = ((right ?? 0) > 0 && right <= this.cols ? right : this.cols) - 1;
        if (l >= r) return;
        this.left = l; this.right = r; this.home();
    }
    scorc() { [this.row, this.col] = this.saved; this.pending = false; }
    decera(top, left, bottom, right) {
        const rowOffset = this.origin ? this.top : 0, colOffset = this.origin ? this.left : 0;
        const clamp = (value, max) => Math.min(Math.max(value, 0), max);
        const t = clamp(top - 1 + rowOffset, this.rows - 1), b = clamp(bottom - 1 + rowOffset, this.rows - 1);
        const l = clamp(left - 1 + colOffset, this.cols - 1), r = clamp(right - 1 + colOffset, this.cols - 1);
        for (let row = t; row <= b; row++) for (let col = l; col <= r; col++) this.grid[row][col] = ' ';
    }
    decstr() { Object.assign(this, { top: 0, bottom: this.rows - 1, left: 0, right: this.cols - 1, origin: false, insert: false }); }
}

/** The block seed on a page, with the cursor (and pending wrap) the seed leaves: the last column of row 7. */
function seededModel(rows, cols) {
    const model = new MarginsModel(rows, cols);
    leftTexts.forEach((text, row) => [...text].forEach((char, col) => { model.grid[row][col] = char; }));
    rightTexts.forEach((text, row) => [...text].forEach((char, index) => { model.grid[row][cols - text.length + index] = char; }));
    Object.assign(model, { row: leftTexts.length - 1, col: cols - 1, pending: true });
    return model;
}

// ---------------------------------------------------------------------------------------------------------------------------
// The controls: each is one fenced write (clear, block seed, operations) followed by one read.

const margins = [ops.lrmm(true), ops.slrm(5, 14)];
const control = (role, steps) => Object.freeze({ role, steps: Object.freeze(steps), feed: clear + blockFeed + steps.map(step => step.bytes).join('') });
const controls = Object.freeze([
    control('scosc', [ops.cup(2, 3), ops.slrm(5, 14), ops.cup(1, 1), ops.scorc()]),
    control('setMargins', [...margins]),
    control('refusals', [...margins, ops.cup(2, 2), ops.slrm(9, 9), ops.slrm(12, 5), ops.text('Q')]),
    control('print', [...margins, ops.cup(2, 12), ops.text('abcdefgh')]),
    control('insert', [...margins, ops.irm(true), ops.cup(1, 6), ops.text('xy')]),
    control('moves', [...margins, ops.cup(3, 8), ops.cuf(20), ops.text('X'), ops.cup(4, 8), ops.cub(20), ops.text('Y'), ops.cup(5, 8), ops.cr(), ops.text('Z'),
        ops.cup(6, 2), ops.cr(), ops.text('W'), ops.cup(7, 20), ops.cuf(500), ops.text('V'), ops.cup(8, 6), ops.ht(), ops.text('T'), ops.cup(9, 2), ops.cub(5), ops.text('U')]),
    control('lfBand', [ops.stbm(1, 10), ...margins, ops.cup(10, 6), ops.lf(), ops.text('L')]),
    control('lfOutside', [ops.stbm(1, 10), ...margins, ops.cup(10, 2), ops.lf(), ops.text('M'), ops.cup(10, 16), ops.lf(), ops.text('N')]),
    control('ri', [ops.stbm(3, 6), ...margins, ops.cup(3, 6), ops.ri(), ops.cup(6, 6), ops.lf()]),
    control('suSd', [...margins, ops.cup(1, 1), ops.su(2), ops.sd(1)]),
    control('ilDl', [...margins, ops.cup(2, 6), ops.il(2), ops.cup(5, 6), ops.dl(1), ops.cup(3, 2), ops.il(1)]),
    control('ichDch', [...margins, ops.cup(1, 6), ops.ich(2), ops.cup(2, 6), ops.dch(3), ops.cup(3, 2), ops.ich(1), ops.cup(4, 16), ops.dch(1)]),
    control('columns', [...margins, ops.cup(4, 6), ops.decic(2), ops.sl(1), ops.decdc(1), ops.sr(2)]),
    control('origin', [ops.stbm(3, 8), ...margins, ops.origin(true), ops.text('O'), ops.cup(2, 20), ops.text('P'), ops.decera(1, 1, 1, 2)]),
    control('decstr', [...margins, ops.decstr()]),
    control('modeReset', [...margins, ops.lrmm(false)]),
]);

/** Every read group, in plan order. */
const groups = Object.freeze([
    { role: 'initial', feeds: [] },
    { role: 'set', feeds: [pinned.set] },
    { role: 'reset', feeds: [pinned.reset] },
    { role: 'blockBefore', feeds: [clear + blockFeed] },
    ...controls.map(item => ({ role: item.role, feeds: [item.feed] })),
].map(Object.freeze));

function makeContract() {
    const plan = [planFeed(lrMarginsReset)];
    for (const item of groups) {
        for (const feed of item.feeds) plan.push(planFeed(feed));
        plan.push(planRead('cells'));
    }
    const roles = Object.freeze(groups.map(item => item.role));
    return Object.freeze({
        caseId: 'modes.left-right-margin', probeId: 'modes.left-right-margin', id: 'DECLRMM', sequence: pinned.set,
        roles, others: Object.freeze(controls.map(item => item.role)),
        pinnedPath: 'termless', setRead: roles.indexOf('set'), resetRead: roles.indexOf('reset'),
        pinnedAlwaysInconclusive: true, effectContract: lrMarginsEffectContractId,
        pinnedExpected: pinned.expected,
        pinnedCriterion: 'ESC[?69h, the mode metadata, then ESC[?69l: parserStateResult(null): always inconclusive ("Mode metadata does not measure margins")',
        pinnedFeeds: Object.freeze([pinned.set, pinned.reset]), plan: Object.freeze(plan),
        terminalFixture: { writes: [], demand: 'none: the pinned terminal path returns inconclusive without writing', minimum: { rows: 1, cols: 1 },
            note: 'the pinned terminal path never touches the terminal and never carries a verdict' },
        appearanceScope: 'stored ASCII cells, the cursor, the stored top/bottom and left/right margins, DECLRMM, origin and insert mode after each '
            + 'control; CPR, wide glyphs, side tables, images and rendered appearance are not claimed',
        grade: observation => gradeEffect(observation),
        observed: observation => briefLrMargins(observation),
    });
}
const contract = makeContract();
/** The single pinned case. */
export const lrMarginsContracts = Object.freeze([contract]);
/** Contract case ids in suite order. */
export const lrMarginsCaseIds = Object.freeze(lrMarginsContracts.map(item => item.caseId));

// ---------------------------------------------------------------------------------------------------------------------------
// Grading.

/** A group's observation: its ink, cursor, geometry and the App's stored region, margins and modes. */
const groupOf = screen => ({ ink: selectiveEraseInk(screen), cursor: [screen.cursorRow, screen.cursorCol], rows: screen.rows, cols: screen.cols,
    region: [screen.bufferState.scrollTop, screen.bufferState.scrollBottom], origin: screen.bufferState.originMode, insert: screen.bufferState.insertMode,
    mode: screen.horizontalMargins.leftRightMarginMode, margins: [screen.horizontalMargins.leftMargin, screen.horizontalMargins.rightMargin] });
const neutral = cell => ['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline'].every(name => cell[name] === false)
    && cell.underline === false && cell.fgIndex === -1 && cell.bgIndex === -1 && cell.protected === false;

/** Whether an observed group equals the model: every stored character, neutral attributes, the cursor, region, margins and modes. */
function matchesModel(state, model) {
    const expected = model.grid.flatMap((line, row) => line.flatMap((char, col) => (char === ' ' ? [] : [[row, col, char]])));
    const inkOk = state.ink.length === expected.length && expected.every(([row, col, char], index) => {
        const cell = state.ink[index];
        return cell.row === row && cell.col === col && cell.char === char && neutral(cell);
    });
    return inkOk && state.cursor[0] === model.row && state.cursor[1] === model.col
        && state.region[0] === model.top && state.region[1] === model.bottom && state.margins[0] === model.left && state.margins[1] === model.right
        && state.mode === model.mode && state.origin === model.origin && state.insert === model.insert;
}

/** The model after a control's steps over the measured block seed. */
function modelOf(item, rows, cols) {
    const model = seededModel(rows, cols);
    for (const step of item.steps) step.apply(model);
    return model;
}

const inconclusive = note => ({ outcome: 'inconclusive', note });
const unsupported = note => ({ outcome: 'unsupported', note });
const fullWidth = state => state.margins[0] === 0 && state.margins[1] === state.cols - 1;

const seedChecks = Object.freeze([
    [o => o.blockBefore.rows >= minimum.rows && o.blockBefore.cols >= minimum.cols, 'Measured geometry cannot fit the left/right-margin controls'],
    [o => o.reverseVideo === false, 'DECSCNM reverse video was not proven off, so stored attributes cannot be separated from it'],
    [o => o.initial.ink.length === 0 && o.initial.mode === false && fullWidth(o.initial), 'Blank neutral screen with DECLRMM reset was not measured'],
    [o => o.set.mode === true && fullWidth(o.set) && o.set.ink.length === 0, 'The pinned ESC[?69h was not measured setting DECLRMM with full-width margins'],
    [o => o.reset.mode === false && fullWidth(o.reset), 'The pinned ESC[?69l was not measured resetting DECLRMM'],
    [o => {
        const model = seededModel(o.blockBefore.rows, o.blockBefore.cols);
        return matchesModel(o.blockBefore, model);
    }, 'The block control seed was not measured, so a moved, dropped or smeared column cannot be distinguished'],
]);

/** The effect verdict: inconclusive without its seeds, unsupported at the first control that differs from the model. */
function gradeEffect(observation) {
    const gap = seedChecks.find(([holds]) => !holds(observation));
    if (gap) return inconclusive(gap[1]);
    const { rows, cols } = observation.blockBefore;
    const failed = controls.find(item => !matchesModel(observation[item.role], modelOf(item, rows, cols)));
    return failed ? unsupported(`The ${failed.role} control differs from the margins model: a margin bound, wrap, scroll band, cursor move, origin offset or mode was wrong`)
        : { outcome: 'supported' };
}

/** A group's head rows as text, `_` for blank. */
const render = (state, rows, width = 20) => rows.map(row => Array.from({ length: width }, (_, col) => {
    const cell = state.ink.find(item => item.row === row && item.col === col);
    return cell === undefined || cell.char === ' ' ? '_' : cell.char;
}).join(''));

/** The observed record: the pinned reads, then every control's head rows, cursor, region, margins, modes and model agreement. */
function briefLrMargins(observation) {
    const { rows, cols } = observation.blockBefore;
    const head = Array.from({ length: 11 }, (_, row) => row);
    const state = item => ({ text: render(item, head), cursor: item.cursor, region: item.region, margins: item.margins, mode: item.mode,
        origin: item.origin, insert: item.insert, ink: item.ink.length });
    return { geometry: { rows, cols }, reverseVideo: observation.reverseVideo, pinnedFeeds: contract.pinnedFeeds,
        pinnedReads: { set: { mode: observation.set.mode, margins: observation.set.margins }, reset: { mode: observation.reset.mode, margins: observation.reset.margins } },
        blockSeed: state(observation.blockBefore),
        controls: Object.fromEntries(controls.map(item => [item.role, { sequences: item.steps.map(step => step.bytes.replaceAll('\x1b', String.raw`\e`)),
            matchesModel: matchesModel(observation[item.role], modelOf(item, rows, cols)), ...state(observation[item.role]) }])) };
}

/** The shared reverse-video reading of every screen: off, on, or not provable (mixed or unreported). */
function reverseVideoOf(binding) {
    if (binding.every(value => value === false)) return false;
    return binding.includes(true) ? true : null;
}

/** Group the read screens of the case by role. */
export function lrMarginsObservation(item, screens) {
    if (!Array.isArray(screens) || screens.length !== item.roles.length) throw new Error('Missing left/right-margin read captures.');
    const observation = Object.fromEntries(item.roles.map((role, index) => [role, groupOf(screens[index])]));
    observation.reverseVideo = reverseVideoOf(screens.map(screen => screen.colorBinding.reverseVideo));
    return observation;
}

/** Independent effect grader entry. */
export function gradeLrMarginsCase(item, observation, revision = lrMarginsContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown left/right-margin contracts revision ${revision}.`);
    if (!observation || item.roles.some(role => !observation[role] || !Array.isArray(observation[role].ink))) throw new Error(`Missing ${item.caseId} read captures.`);
    return item.grade(observation);
}

export { planFeeds as lrMarginsFeeds, planReads as lrMarginsReads } from './graded-plan.mjs';

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateLrMarginsFeeds(item, feeds) {
    validateExactFeeds(item, feeds, 'contract');
}

/** The fixed fixture facts and the independent model, exposed so tests can cross-check them. */
export const lrMarginsFixture = Object.freeze({ pinned, minimum, clear, controls, groups, ops });
export const lrMarginsModel = Object.freeze({ MarginsModel, seededModel, modelOf, matchesModel });
