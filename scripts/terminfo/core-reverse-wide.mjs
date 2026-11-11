// Exact Core fixtures for `modes.reverse-video` (a measured DECSCNM activation and restoration) and `text.wide.cjk` (the stored
// wide cell of U+4E2D). Coordinates are physical and zero-based; CPR replies are one-based. Every expectation below comes from
// the specification model written in this file (DECSET 5/DECRST 5 set and reset the mode; a two-column character stores a wide
// cell plus a continuation cell and advances two columns), never from the production terminal's own output.
//
// Observation layers. Reverse video: the raw DECRPM reply for mode 5 (DECRQM, `CSI ? 5 $ p`), and the App's stored mode readback
// (`readScreen` resolved-color binding `reverseVideo`, together with the presented default colors it reports, which swap exactly under
// DECSCNM). A mode flag is not rendered color inversion: no pixel or raster claim is made. Wide cells: the complete stored cell details
// (the Wide and WideContinuation storage flags) with the decoded text of the physical row, and the physical cursor with its raw CPR.
import { scrollbackReset, validateScrollbackScreen } from './scrollback-contracts.mjs';
import { validateGradedJournal, planFeed, planRead } from './graded-plan.mjs';
import { screenSnapshot, OwnedCellObserver } from './agent-observer.mjs';
import { validateCursorReport } from './cursor-report.mjs';
import { validateModeReport, modeReportQuery, parseModeReportFrame } from './mode-report.mjs';
import { charsetsRow, charsetsFlagBits } from './charsets-contracts.mjs';
import { expandCellDetails } from './cell-details.mjs';

export const reverseWideSuiteId = 'ntilde-core-reverse-wide-v1';
export const reverseWideNonce = 'NTILDE_RW_';
export const reverseWideIds = Object.freeze(['modes.reverse-video', 'text.wide.cjk']);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (condition, message) => { if (!condition) throw new Error('Core reverse video and wide cells: ' + message); };

const reset = scrollbackReset.replace('\x1b[?1049l', '') + '\x1b[?69l\x1b[?7h\x1b[?25h';
const guardRow = 9, keepRow = 1;
const marker = '\x1b[10;1HGUARD\x1b[2;1HKEEP\x1b[H';
const setup = reset + marker;
/** DECSET 5 and DECRST 5 (DECSCNM). */
export const decset5 = '\x1b[?5h', decrst5 = '\x1b[?5l';
/** The mode numbers whose DECRPM reply each reverse-video read retains: DECSCNM, and two unrelated modes that must not move. */
export const reverseModes = Object.freeze([5, 7, 25]);
/** The exact CJK specimen: U+4E2D. The owned output path writes the string as UTF-8. */
export const cjkChar = '中';
export const cjkUtf8Hex = Buffer.from(cjkChar, 'utf8').toString('hex');
/** A different wide character (U+4E16) used only as a stale-cell control; it can never stand in for the target. */
const otherCjk = '世';
/** TerminalCellFlags: Inverse is bit 1; default fg/bg markers are 0x300; the structural bits are Wide, WideContinuation and HasExtendedText. */
const flagBits = Object.freeze({ inverse: 0x2, ...charsetsFlagBits });
const structural = flagBits.wide | flagBits.continuation | flagBits.extended;

/** A seeded reverse-video row: plain AB, per-cell inverse (SGR 7) CD, plain E, then the cursor to row 2, column 5 (one-based 3;6). */
const rvSeed = '\x1b[1;1HAB\x1b[7mCD\x1b[0mE\x1b[3;6H';
const put = (row, col, char, kind = 'narrow', inverse = false) => ({ row, col, char, kind, inverse });
const text = (row, string, start = 0) => [...string].map((char, i) => put(row, start + i, char));
const guards = [...text(keepRow, 'KEEP'), ...text(guardRow, 'GUARD')];
const rvInk = [put(0, 0, 'A'), put(0, 1, 'B'), put(0, 2, 'C', 'narrow', true), put(0, 3, 'D', 'narrow', true), put(0, 4, 'E'), ...guards];
const byPosition = (a, b) => (a.row - b.row) || (a.col - b.col);
const ordered = ink => [...ink].sort(byPosition);

/** The ordered plan. Each entry: role, the fenced feed, the DECRPM modes asked after it, the independent expected ink and cursor.
 *  `status` is the expected DECRPM status of mode 5 (1 set, 2 reset); `stored` the expected stored mode readback. */
export function reverseWideSteps() {
    const rv = (role, feed, status) => ({ part: 'reverse', role, feed, modes: reverseModes, ink: ordered(rvInk), cursor: [2, 5], status, stored: status === 1, projection: 'full' });
    // A frame holding a wide cell is observed through the stored-cell projection only: the App's attribute strings and resolved colors
    // describe the ASCII viewport and cannot represent a wide row (the charsets suite omits them for the same reason).
    const cjk = (role, feed, ink, cursor) => ({ part: 'wide', role, feed, modes: [], ink: ordered([...ink, ...guards]), cursor,
        projection: ink.some(cell => cell.kind !== 'narrow') ? 'cells' : 'full' });
    const target = [put(0, 0, cjkChar, 'wide'), put(0, 1, '', 'continuation')];
    return [
        // Reverse video: a seeded row with per-cell inverse cells, the explicit mode-off control, DECSET 5, DECRST 5.
        rv('rv:seed', setup + rvSeed, 2),
        rv('rv:off', decrst5, 2),
        rv('rv:set', decset5, 1),
        rv('rv:reset', decrst5, 2),
        // Wide cells: a different wide character first (stale cells), a measured clean home, then the exact specimen.
        cjk('wide:stale-seed', setup + otherCjk, [put(0, 0, otherCjk, 'wide'), put(0, 1, '', 'continuation')], [0, 2]),
        cjk('wide:setup', setup, [], [0, 0]),
        cjk('wide:target', cjkChar, target, [0, 2]),
        // Controls that reject false positives: one narrow character, and two narrow characters that reach the same cursor column.
        cjk('ctl:ascii:setup', setup, [], [0, 0]),
        cjk('ctl:ascii', 'A', [put(0, 0, 'A')], [0, 1]),
        cjk('ctl:narrow2:setup', setup, [], [0, 0]),
        cjk('ctl:narrow2', 'AB', [put(0, 0, 'A'), put(0, 1, 'B')], [0, 2]),
    ];
}

/** Each step is one fenced write, then its DECRPM exchanges, then one CPR exchange, then one parent observation. */
export function reverseWidePlan() {
    return reverseWideSteps().flatMap(step => [planFeed(step.feed), { ...planRead('state'), role: step.role, modes: step.modes, projection: step.projection }]);
}
/** The restoring bytes for the original DECSCNM state: the stable reset (which clears the mode) and the mode again when it was set. */
export const restoreFeed = originalReverse => reset + (originalReverse ? decset5 : '') + '\x1b7\x1b[?47l';

const detailCache = new WeakMap();
/** The expanded stored details of one screen, validated once. */
function detailsOf(screen) {
    let details = detailCache.get(screen);
    if (details === undefined) {
        details = expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true);
        detailCache.set(screen, details);
    }
    return details;
}
/** A stored tuple with nothing but default colors and no decoration (the structural and inverse bits are judged separately). */
const plainDetail = detail => detail.fg === -1 && detail.bg === -1 && detail.foregroundArgb == null && detail.backgroundArgb == null
    && detail.underlineStyle === 0 && detail.underlineColorIndex === -1 && detail.underlineArgb == null && detail.overline === false;

/** The storage kind of a decoded physical cell. */
function storageKind(cell) {
    if (cell.continuation) return 'continuation';
    return cell.wide ? 'wide' : 'narrow';
}

/** One decoded physical cell: text, storage kind, per-cell inverse, and whether it stores anything the contract never sets. */
function cellOf(screen, row, col) {
    const cell = charsetsRow(screen, row)[col], detail = detailsOf(screen)[row * screen.cols + col];
    const kind = storageKind(cell);
    const color = detail.flags & ~structural & ~flagBits.inverse;
    return { char: cell.char, kind, inverse: (detail.flags & flagBits.inverse) !== 0,
        clean: !cell.extended && color === flagBits.defaultColors && plainDetail(detail) && (kind !== 'continuation' || cell.char === '') };
}
/** Every cell that is not a blank narrow default cell, in row-major order, or `null` when a cell carries anything the contract never sets. */
function inkOf(screen) {
    const ink = [];
    for (let row = 0; row < screen.rows; row++) {
        for (let col = 0; col < screen.cols; col++) {
            const cell = cellOf(screen, row, col);
            if (!cell.clean) return null;
            const blank = cell.char === ' ' && cell.kind === 'narrow' && !cell.inverse;
            if (!blank) ink.push(put(row, col, cell.char, cell.kind, cell.inverse));
        }
    }
    return ink;
}
/** The stable state every observation must share. */
function stable(frame) {
    const s = frame.screen, b = s.bufferState, m = s.horizontalMargins;
    return b.alternateScreen && !b.originMode && !b.insertMode && b.autoWrap && !b.pendingWrap && s.cursorVisible
        && b.scrollTop === 0 && b.scrollBottom === s.rows - 1 && b.scrollbackLines === 0 && s.history.lines.length === 0
        && m?.leftRightMarginMode === false && m.leftMargin === 0 && m.rightMargin === s.cols - 1;
}
/** Why one observation differs from its expected ink and cursor, or `null`. */
function inkMismatch(frame, step) {
    if (!stable(frame)) return 'state';
    const ink = inkOf(frame.screen), s = frame.screen;
    if (ink === null) return 'stored-attributes';
    if (!same(ink, step.ink)) return 'cells';
    if (s.cursorRow !== step.cursor[0] || s.cursorCol !== step.cursor[1]) return 'cursor';
    return null;
}
const binding = frame => frame.screen.colorBinding;
/** The raw DECRPM status of one mode in one frame, or `null` when the reply is absent. */
const statusOf = (frame, mode) => frame.modes?.find(m => m.mode === mode)?.reply?.status ?? null;
/** The presented default colors swap exactly under DECSCNM and under nothing else. */
const defaultsSwapped = (a, b) => binding(a).foregroundArgb === binding(b).backgroundArgb && binding(a).backgroundArgb === binding(b).foregroundArgb;
const defaultsEqual = (a, b) => binding(a).foregroundArgb === binding(b).foregroundArgb && binding(a).backgroundArgb === binding(b).backgroundArgb;
const sameDetails = (a, b) => same(a.screen.cellDetails, b.screen.cellDetails) && same(a.screen.attributeRuns, b.screen.attributeRuns) && same(a.screen.lines, b.screen.lines);

/** The per-cell inverse (SGR 7) seed: columns 2 and 3 are inverse cells, columns 0, 1 and 4 are not. */
const hasInverseCells = frame => [2, 3].every(col => cellOf(frame.screen, 0, col).inverse) && [0, 1, 4].every(col => !cellOf(frame.screen, 0, col).inverse);
/** A frame with DECSCNM reported reset by both layers. */
const reportsReset = frame => statusOf(frame, 5) === 2 && binding(frame).reverseVideo === false;
/** The as-found state: its DECRPM reply and the App's stored readback must agree, whatever the state is, and equal the collector's record. */
function foundCoherent(start, original) {
    const status = statusOf(start, 5);
    return [1, 2].includes(status) && (status === 1) === binding(start).reverseVideo && original === binding(start).reverseVideo;
}
/** The mode-5 transition as observed: each step's raw DECRPM status and stored readback, and what the default colors did. */
function transitionOf(frames) {
    const { start, off, set, reset: after, finish } = frames;
    return {
        initial: { decrpm: statusOf(start, 5), stored: binding(start).reverseVideo },
        off: { decrpm: statusOf(off, 5), stored: binding(off).reverseVideo },
        set: { decrpm: statusOf(set, 5), stored: binding(set).reverseVideo, defaultsSwapped: defaultsSwapped(off, set) },
        reset: { decrpm: statusOf(after, 5), stored: binding(after).reverseVideo, defaultsRestored: defaultsEqual(off, after) },
        restored: { decrpm: statusOf(finish, 5), stored: binding(finish).reverseVideo },
    };
}
/** Activation must not rewrite stored text, per-cell rendition, the cursor, margins or buffer modes, and must restore the same. */
function toggleLeavesCellsUntouched({ off, set, reset: after }) {
    const cursor = frame => [frame.screen.cursorRow, frame.screen.cursorCol];
    return sameDetails(off, set) && sameDetails(off, after) && same(cursor(off), cursor(set))
        && same(off.screen.horizontalMargins, set.screen.horizontalMargins) && same(off.screen.bufferState, set.screen.bufferState);
}
/** Whether every control of the reverse-video fixture held: shapes, unrelated modes, the as-found state, the mode-off and per-cell inverse
 *  controls and the restoration. */
function reverseControlsHold(roles, specs, frames, original) {
    const { start, off, finish } = frames, seed = roles['rv:seed'];
    const shapes = ['rv:seed', 'rv:off', 'rv:set', 'rv:reset'].every(role => inkMismatch(roles[role], specs[role]) === null);
    const neighbors = ['rv:seed', 'rv:off', 'rv:set', 'rv:reset'].every(role => statusOf(roles[role], 7) === 1 && statusOf(roles[role], 25) === 1);
    const modeOff = reportsReset(seed) && reportsReset(off) && defaultsEqual(seed, off);
    const inverseDistinct = modeOff && hasInverseCells(seed) && hasInverseCells(off);
    const restored = statusOf(finish, 5) === (original ? 1 : 2) && binding(finish).reverseVideo === original;
    return shapes && neighbors && foundCoherent(start, original) && inverseDistinct && restored;
}

/** Grade `modes.reverse-video` from the ordered observations; the start frame is the as-found state, the finish frame the restoration. */
function gradeReverse(roles, steps, start, finish, original) {
    const frames = { start, finish, off: roles['rv:off'], set: roles['rv:set'], reset: roles['rv:reset'] };
    const transition = { ...transitionOf(frames), toggleLeavesStoredCellsUntouched: toggleLeavesCellsUntouched(frames) };
    const specs = Object.fromEntries(steps.map(step => [step.role, step]));
    if (!reverseControlsHold(roles, specs, frames, original)) return { outcome: 'inconclusive', reason: 'observation-controls-invalid', transition };
    const activated = transition.set.decrpm === 1 && transition.set.stored === true && transition.set.defaultsSwapped;
    const ignored = transition.set.decrpm === 2 && transition.set.stored === false && defaultsEqual(frames.off, frames.set);
    const deactivated = transition.reset.decrpm === 2 && transition.reset.stored === false && transition.reset.defaultsRestored;
    if (!deactivated || !transition.toggleLeavesStoredCellsUntouched) return { outcome: 'inconclusive', reason: 'transition-observation-incoherent', transition };
    if (activated) return { outcome: 'supported', transition };
    return ignored ? { outcome: 'unsupported', reason: 'decset-ignored', transition } : { outcome: 'inconclusive', reason: 'transition-observation-incoherent', transition };
}

/** Grade `text.wide.cjk` from the ordered observations. */
function gradeWide(roles, steps) {
    const specs = Object.fromEntries(steps.map(step => [step.role, step]));
    const controlRoles = ['wide:stale-seed', 'wide:setup', 'ctl:ascii:setup', 'ctl:ascii', 'ctl:narrow2:setup', 'ctl:narrow2'];
    const controls = controlRoles.every(role => inkMismatch(roles[role], specs[role]) === null);
    const target = roles['wide:target'], spec = specs['wide:target'];
    const mismatch = inkMismatch(target, spec);
    const first = stable(target) ? cellOf(target.screen, 0, 0) : null, second = stable(target) ? cellOf(target.screen, 0, 1) : null;
    const observed = { char: first?.char ?? null, kind: first?.kind ?? null, continuation: second?.kind ?? null,
        cursor: [target.screen.cursorRow, target.screen.cursorCol] };
    if (!controls) return { outcome: 'inconclusive', reason: 'observation-controls-invalid', observed };
    return mismatch === null ? { outcome: 'supported', observed } : { outcome: 'unsupported', reason: 'target-mismatch:' + mismatch, observed };
}

/** Grade both specimens from complete, ordered observations. A missing or reordered role is an invalid journal, never a verdict. */
export function gradeReverseWide(roles, start, finish, original) {
    const steps = reverseWideSteps();
    for (const step of steps) check(roles[step.role], 'missing observation: ' + step.role);
    check(start && finish, 'missing start or finish observation');
    return {
        'modes.reverse-video': { id: 'modes.reverse-video', layer: 'DECRPM reply and stored mode readback', ...gradeReverse(roles, steps, start, finish, original) },
        'text.wide.cjk': { id: 'text.wide.cjk', layer: 'stored physical cells and cursor', ...gradeWide(roles, steps) },
    };
}

/** The exact specimens the report carries beside their observations. */
export const reverseWideSpecimens = Object.freeze({
    'modes.reverse-video': { fixture: decset5 + ' then ' + decrst5, sequence: [decset5, decrst5], requests: reverseModes.map(mode => `\x1b[?${mode}$p`),
        before: 'rv:off', after: 'rv:set', restored: 'rv:reset' },
    'text.wide.cjk': { fixture: cjkChar, utf8Hex: cjkUtf8Hex, codePoint: 'U+4E2D', before: 'wide:setup', after: 'wide:target' },
});

/** The retained summary of one frame. The as-found and restored main screens carry no decoded cells (the shell's own content is not a fixture). */
const view = (frame, decoded = true) => ({ lines: frame.screen.lines.map(line => line.trimEnd()).filter(Boolean), cursor: [frame.screen.cursorRow, frame.screen.cursorCol],
    ...(decoded ? { ink: inkOf(frame.screen) } : {}), cpr: frame.cpr?.reply ?? null, modes: frame.modes?.map(m => ({ mode: m.mode, raw: m.reply?.raw ?? null, status: m.reply?.status ?? null })) ?? [],
    ...(binding(frame) ? { reverseVideo: binding(frame).reverseVideo, defaults: { foregroundArgb: binding(frame).foregroundArgb, backgroundArgb: binding(frame).backgroundArgb } } : { projection: 'cells' }) });

/** One read: its role, retained exchanges and ordering barrier must match the plan, and the raw CPR the physical cursor. */
function checkedRead(frame, record, step) {
    const { role, modes, projection } = step;
    check(record.role === role && same(record.cpr, frame.cpr) && same(record.modes, frame.modes), 'read order or retained reports changed');
    // The projection is part of the plan: a frame is stored-cell-only exactly where the plan says, in the record and in the journal.
    check(record.projection === projection && (frame.projection === 'cells') === (projection === 'cells') && (frame.screen.attributeRuns === undefined) === (projection === 'cells'), 'read projection changed');
    const { barrier, reply } = validateCursorReport(frame.cpr);
    check(barrier === frame.barrier && reply !== null, 'missing ordered CPR');
    check(reply.row === frame.screen.cursorRow + 1 && reply.col === frame.screen.cursorCol + 1, 'CPR disagrees with physical cursor');
    checkedModes(frame.modes, modes);
    return frame;
}
/** The retained DECRPM exchanges must be exactly the planned modes, each valid, each a raw reply of its own mode. */
function checkedModes(retained, modes) {
    check(Array.isArray(retained) && same(retained.map(m => m.mode), modes), 'retained mode reports differ from the plan');
    for (const entry of retained) {
        const checked = validateModeReport(entry);
        check(same(checked, entry), 'retained mode report is not canonical');
        check(checked.reply !== null, 'mode ' + checked.mode + ' has no DECRPM reply');
    }
}
/** DECSCNM coherence of one frame: the raw DECRPM status of mode 5 must agree with the stored readback it was taken beside. */
const coherent = frame => { const s = statusOf(frame, 5); return [1, 2].includes(s) && (s === 1) === binding(frame).reverseVideo; };

/** A stored-cell-only frame: complete cell details and buffer state, and none of the attribute, resolved-color or binding projections. */
function validateCellsScreen(screen) {
    check(screen.attributeRuns === undefined && screen.colorBinding === undefined && screen.resolvedColors === undefined, 'a cells frame carries a projection it cannot support');
    check(Boolean(screen.bufferState && screen.history) && screen.history.lines.length === screen.bufferState.scrollbackLines, 'cells frame is missing buffer state or history');
    for (let row = 0; row < screen.rows; row++) charsetsRow(screen, row); // complete, coherent physical rows (wide cells have their continuations)
}
/** The screen validator every journaled frame must pass: coherent stored cells and (for a full frame) color projections with the binding that proves the
 *  mode readback. A frame is a stored-cell-only frame exactly when it has no attribute runs. */
export const reverseWideScreenOf = screen => {
    if (screen.attributeRuns === undefined) {
        check(screen.colorBinding === undefined && screen.resolvedColors === undefined, 'a cells frame carries a projection it cannot support');
        const result = screenSnapshot(screen, false, true, false, true, true, false, false, false, true);
        validateCellsScreen(result);
        return result;
    }
    const result = screenSnapshot(screen, true, true, false, true, true, true, false, false, true);
    // The fixture screens (all on the alternate screen) must have coherent stored projections; the shell's own main screen is only hashed.
    if (result.bufferState.alternateScreen) validateScrollbackScreen(result);
    return result;
};

/** The worker's own records (feeds, barriers, read order, retained exchanges) must equal the trusted plan and the parent's journal. */
function checkRunRecords(frames, run, plan, reads) {
    check(same(run.feeds, plan.filter(step => step.feed !== undefined).map(step => step.feed)), 'fixture bytes changed');
    check(Array.isArray(run.barriers) && run.barriers.length === run.feeds.length && run.barriers.every(b => /^\x1b\[\?[\d;]+c$/.test(b)), 'missing write barriers');
    check(Array.isArray(run.reads) && run.reads.length === reads.length, 'incomplete reads');
    const [start, bind, finish] = [frames[0], frames[1], frames.at(-1)];
    // The as-found DECRPM, the restoring bytes and the final DECRPM are retained exchanges, bound to the frames that observed them.
    checkedModes(start.modes, [5]);
    check(same(run.initial?.modes, start.modes) && start.barrier === null, 'as-found mode report changed');
    check(typeof run.original === 'boolean' && run.original === binding(start).reverseVideo && coherent(start), 'original state is not coherent');
    check(run.restoreFeed === restoreFeed(run.original), 'restoring bytes changed');
    checkedModes(finish.modes, [5]);
    check(same(run.final?.modes, finish.modes) && finish.barrier === finish.modes.at(-1).barrier, 'final mode report changed');
    check(coherent(finish) && bind.modes === undefined, 'restoration observation is not coherent');
}

/** Re-derive from the trusted plan and parent-journaled observations, never the worker's reported grade. */
export function buildReverseWideReport(frames, run, runId) {
    check(run?.suiteId === reverseWideSuiteId && run.runId === runId, 'identity mismatch');
    check(Array.isArray(frames) && frames.length > 0, 'incomplete journal');
    const cols = frames[0].screen.cols;
    check(Number.isSafeInteger(cols) && cols >= 48 && cols <= 256, 'unsupported geometry');
    const plan = reverseWidePlan(), reads = plan.filter(step => step.read);
    validateGradedJournal(frames, { frameCount: reads.length + 3, prefix: 'rw', noncePrefix: reverseWideNonce, runId, label: 'Core reverse video and wide cells',
        screenOf: reverseWideScreenOf });
    checkRunRecords(frames, run, plan, reads);
    const roles = Object.fromEntries(reads.map((step, i) => [step.role, checkedRead(frames[i + 2], run.reads[i], step)]));
    for (const role of ['rv:seed', 'rv:off', 'rv:set', 'rv:reset']) check(coherent(roles[role]), 'DECRPM disagrees with the stored readback: ' + role);
    const effects = gradeReverseWide(roles, frames[0], frames.at(-1), run.original);
    for (const id of reverseWideIds) {
        const specimen = reverseWideSpecimens[id];
        Object.assign(effects[id], { fixture: specimen.fixture, specimen: structuredClone(specimen),
            observed: { ...effects[id].observed, before: view(roles[specimen.before]), after: view(roles[specimen.after]) } });
    }
    const controls = Object.fromEntries(['rv:seed', 'rv:off', 'rv:set', 'rv:reset', 'wide:stale-seed', 'ctl:ascii', 'ctl:narrow2'].map(role => [role, view(roles[role])]));
    controls.asFound = view(frames[0], false); controls.restored = view(frames.at(-1), false);
    return { schemaVersion: 1, suiteId: reverseWideSuiteId, runId, cols, original: run.original, effects, controls, snapshots: frames };
}

/** Restore the exact original DECSCNM state (and the main screen), even when the run aborted. A failed fence rejects ordinary writes, so restoration then
 *  takes the bounded cleanup path (no barrier, no observation: the run is already failing); otherwise it is observed through DECRPM and the finish frame. */
async function restoreOriginal({ original, fence, owner, modes }) {
    if (fence.failed) {
        await fence.writeCleanup(restoreFeed(original));
        return [];
    }
    const barrier = await fence.write(restoreFeed(original));
    const final = await modes([5]);
    await owner('rw-finish', final.at(-1)?.barrier ?? barrier, { modes: final });
    return final;
}

/** Execute every write and its observation in order; restoration of the original DECSCNM state is attempted on every path. */
export async function collectReverseWide({ config, fence, owner }) {
    const feeds = [], barriers = [], reads = [];
    const modes = async list => {
        const out = [];
        for (const mode of list) out.push({ mode, ...(await fence.exchange(modeReportQuery(mode), bytes => parseModeReportFrame(bytes, mode))) }); // NOSONAR: exchanges must stay serial.
        return out;
    };
    // The as-found state is read before this suite writes anything: DECRPM first, then the parent observation.
    const initial = await modes([5]);
    const started = await owner('rw-start', null, { modes: initial });
    check(started.screen.rows >= 12 && started.screen.cols >= 48, 'geometry too small');
    const original = started.screen.colorBinding.reverseVideo;
    check(typeof original === 'boolean', 'as-found DECSCNM state missing');
    let primary;
    try {
        await owner('rw-bind', await fence.write('\x1b[?47h' + reset + reverseWideNonce + config.runId));
        for (const step of reverseWidePlan()) {
            if (step.feed !== undefined) {
                feeds.push(step.feed);
                barriers.push(await fence.write(step.feed)); // NOSONAR: fixture writes and reads must remain serial.
            } else {
                const asked = await modes(step.modes); // NOSONAR: each reply orders after its fixture write.
                const cpr = await fence.cursorReport(); // NOSONAR: each CPR orders after its fixture write.
                await owner('rw-read', cpr.barrier, { cpr, modes: asked, projection: step.projection }); // NOSONAR: observation must finish before the next write.
                reads.push({ role: step.role, modes: asked, cpr, projection: step.projection });
            }
        }
    } catch (error) { primary = error; }
    const final = [];
    try { final.push(...await restoreOriginal({ original, fence, owner, modes })); }
    catch (error) {
        if (primary) throw new AggregateError([primary, error], 'Core reverse video and wide cell collection and restoration failed.');
        throw error;
    }
    if (primary) throw primary;
    return { schemaVersion: 1, suiteId: reverseWideSuiteId, runId: config.runId, original, restoreFeed: restoreFeed(original),
        feeds, barriers, initial: { modes: initial }, reads, final: { modes: final } };
}

/** Two complete reads must agree before a frame can enter the journal; each read also journals the exchanges bound to its barrier. A read that
 *  asks for the stored-cell projection drops the attribute strings and resolved colors for that read only. */
export class ReverseWideObserver extends OwnedCellObserver {
    async capture(budget) {
        const first = await super.capture(budget);
        this.lastAttempt = { first, second: await super.capture(budget) };
        check(same(first, this.lastAttempt.second), 'paired reads disagree');
        return first;
    }
    async handleRequest(request, budget) {
        const isRead = request.kind === 'rw-read', isStart = request.kind === 'rw-start', isFinish = request.kind === 'rw-finish';
        check(isRead || request.cpr === undefined, 'unexpected CPR');
        check(isRead || request.projection === undefined, 'unexpected projection');
        check(!isRead || ['full', 'cells'].includes(request.projection), 'unknown projection');
        check(isRead || isStart || isFinish ? Array.isArray(request.modes) : request.modes === undefined, 'unexpected mode reports');
        const cpr = isRead ? validateCursorReport(request.cpr) : null;
        check(!isRead || cpr.barrier === request.barrier, 'CPR barrier mismatch');
        const modes = request.modes === undefined ? undefined : request.modes.map(entry => validateModeReport(entry));
        check(modes === undefined || modes.length <= 3, 'too many mode reports');
        check(!isFinish || modes.at(-1)?.barrier === request.barrier, 'mode report barrier mismatch');
        const cells = request.projection === 'cells', projection = { includeAttributes: this.includeAttributes, includeResolvedColors: this.includeResolvedColors };
        if (cells) Object.assign(this, { includeAttributes: false, includeResolvedColors: false });
        let result;
        try { result = await super.handle(request.kind, budget, request.barrier); } finally { Object.assign(this, projection); }
        if (isRead) { this.frames.at(-1).cpr = cpr; this.frames.at(-1).projection = request.projection; }
        if (modes !== undefined) this.frames.at(-1).modes = modes;
        return result;
    }
}
