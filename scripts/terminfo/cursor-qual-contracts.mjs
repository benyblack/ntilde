// Versioned probe contracts for the cursor qualification batch. Exactly four pinned terminfo.dev@4.0.0 cases (bundle SHA-256
// aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6, probeHash 16a690f34c0e):
//
//   cursor.cup-scroll-region  DECSTBM 5;15, DECOM on, CUP 1;1 (termless: physical row 4, col 0). The pinned app callback also asks for CPR
//                             at CUP 1;1, CUP 2;1 and CUD 999 and is ALWAYS inconclusive: "the physical row 5 and the margins still need an
//                             independent observable". The contract supplies that observable: the physical cursor, the actual margins and
//                             the origin mode before and after, with the raw CPR retained beside them (DECOM makes it region-relative).
//   cursor.hide               termless: stored visibility shown -> hidden -> restored. The pinned app callback captures a steady block cursor
//                             at row 2 column 3 (control) and the same cell after DECTCEM reset (target) and is ALWAYS inconclusive:
//                             "control must visibly show the cursor". The contract supplies controlled pixels for both.
//   cursor.shape              termless: CSI 6 SP q stores a beam (bar) style. The pinned app callback captures the default shape and the
//                             shape after CSI 5 SP q and is ALWAYS inconclusive ("shape difference requires review"); the stock run supplies
//                             no capture adapter, so it never gets that far. Its record also lacks termNeedsGeometry, and it reads ctx.rows
//                             and ctx.cols after the captures, so a capturing run throws UndeclaredTerminalGeometry too (latent in stock).
//                             The contract supplies the stored mode and controlled pixels, with shape, blink, effective visibility and
//                             host-default intent kept separate.
//   cursor.reverse-wrap       termless and app: with ?7 and ?45 set, `A` x cols then `B` wraps to row 2 column 2 and two BS return to row 1
//                             column cols. The ORIGINAL app callback throws UndeclaredTerminalGeometry before sending a byte (the probe
//                             record lacks termNeedsGeometry); the repaired execution declares the geometry, proves it before and after
//                             and measures the movement.
//
// Everything is graded from independent parent observations: the complete viewport text, the physical cursor, margins, origin and wrap
// state, stored cursor mode and visibility, that no cell stores a non-default attribute, the raw CPR replies and controlled render captures.
// Expected states are written down here from the protocol, never derived from terminal readback.
//
// SCOPE. Stored state, physical cursor, retained CPR replies and synthetic offscreen render phases (presentation time 0 and 530 ms) of ASCII
// fixtures at the measured geometry. No desktop blink cadence, focus, compositor, Unicode, wide-cell, reflow or resize claim.
import { planFeed, planRead, planFeeds, planReads } from './graded-plan.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { expandScreenFlags } from './ascii-sgr-contracts.mjs';

/** Contracts v1 is the first measured semantics of this batch. */
export const cursorQualContractsVersion = 1;
/** Identity of this suite's layered extension output. */
export const cursorQualSuiteId = 'ntilde-owned-cursor-qualification-graded-v1';
/** The nonce prefix binding worker output to this suite. */
export const cursorQualNoncePrefix = 'NTILDE_CQ_';
/** The measured reset every group begins with: SGR off, DECSCNM/DECOM off, auto-wrap on, reverse-wrap off, margins reset, IRM off, screen cleared,
 * cursor home, visible and in its host-default shape. */
export const cursorQualReset = '\x1b[0m\x1b[?5l\x1b[?6l\x1b[?7h\x1b[?45l\x1b[r\x1b[4l\x1b[2J\x1b[H\x1b[?25h\x1b[0 q';
/** The exact pinned case ids, in suite order. */
export const cursorQualCaseIds = Object.freeze(['cursor.cup-scroll-region', 'cursor.hide', 'cursor.shape', 'cursor.reverse-wrap']);
/** The smallest measured geometry: the pinned CUP fixture needs 15 rows; the observer needs 48 columns. */
export const cursorQualMinimum = Object.freeze({ rows: 15, cols: 48 });
/** The presentation phases the captures use: 0 ms is the visible half-period, 530 ms the hidden one (synthetic, not a desktop cadence). */
export const cursorQualPhases = Object.freeze({ visible: 0, hidden: 530 });
/** The pinned note texts, byte for byte. */
export const pinnedCursorNotes = Object.freeze({
    hideParser: 'Parser visibility state only; display pixels were not captured',
    hideRestoration: 'Shown control, hidden readback or initial-state restoration was not established',
    hidePixels: 'Seeded cursor pixels require independent review: control must visibly show the cursor; blink phase or missing focus cannot establish a negative',
    shapePixels: 'Cursor pixels captured; shape difference requires review',
});

const at = (row, col = 1) => `\x1b[${row};${col}H`;
/** The seeded hide/shape scene: two text rows and the cursor between L and R (row 2, column 3, physical 1,2). */
export const cursorScene = '\x1b[1;1HCURSOR\x1b[2;1HL  R\x1b[2;3H';
export const cursorSceneText = Object.freeze({ 0: 'CURSOR', 1: 'L  R' });
/** The pinned hide fixture bytes. */
export const hideClear = '\x1b[0m\x1b[2J\x1b[H';
export const hideSeed = cursorScene + '\x1b[2 q\x1b[?25h';
export const hideCleanup = '\x1b[0m\x1b[2J\x1b[H\x1b[?25h\x1b[0 q';

/** The stored modes DECSCUSR requests: 0 is the host default, 1/2 block, 3/4 underline, 5/6 bar (odd blinks). */
export function requestedCursorMode(code) {
    if (code === 0) return 'default';
    return { style: code >= 5 ? 1 : code >= 3 ? 2 : 0, blink: code % 2 === 1 };
}
const modeLabel = mode => (mode === 'default' ? 'host-default' : `${['block', 'bar', 'underline'][mode.style]}/${mode.blink ? 'blinking' : 'steady'}`);

/** The shared state-read screen: attributes, stored details, cursor mode and buffer state. */
export const validateCursorQualScreen = screen => {
    const flags = expandScreenFlags(screen);
    const details = expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true);
    if (flags.length !== details.length || details.some((detail, position) => detail.flags !== flags[position])) {
        throw new Error('Contradictory stored cell projections in cursor evidence.');
    }
    if (!screen.bufferState || !screen.cursorMode || screen.history?.lines?.length !== screen.bufferState.scrollbackLines) {
        throw new Error('Cursor evidence is missing buffer state or cursor mode.');
    }
};

/** Whether one stored cell detail is the default tuple. */
const defaultDetail = detail => detail.flags === 0x300 && detail.fg === -1 && detail.bg === -1 && detail.foregroundArgb == null
    && detail.backgroundArgb == null && detail.overline === false && detail.underlineStyle === 0
    && detail.underlineColorIndex === -1 && detail.underlineArgb == null;

/** What one state read shows, projected for comparison with an expectation. */
export function cursorQualView(screen) {
    validateCursorQualScreen(screen);
    const state = screen.bufferState;
    return { rows: screen.rows, cols: screen.cols, lines: screen.lines.map(line => line.trimEnd()), cursor: [screen.cursorRow, screen.cursorCol],
        visible: screen.cursorVisible, mode: { style: screen.cursorMode.style, blink: screen.cursorMode.blink, isDefault: screen.cursorMode.isDefault },
        top: state.scrollTop, bottom: state.scrollBottom, alternate: state.alternateScreen, origin: state.originMode, autoWrap: state.autoWrap,
        insert: state.insertMode, pending: state.pendingWrap, history: state.scrollbackLines,
        storedDefault: expandCellDetails(screen.cellDetails, screen.rows * screen.cols, true).every(defaultDetail) };
}

/** The CPR a conforming terminal answers for an OBSERVED physical cursor: relative to the region top under DECOM, absolute otherwise (1-based). */
export function expectedCursorReport(view) {
    return [view.cursor[0] - (view.origin ? view.top : 0) + 1, view.cursor[1] + 1];
}

/** The first way an observed state differs from an expectation, or null. `baseline` is the start frame's view (the host-default mode);
 * `prior` holds the views of earlier reads for expectations relative to them. */
export function cursorQualMismatch(view, expect, baseline, prior = []) {
    if (!view.alternate) return { part: 'screen', note: 'The alternate screen was not active' };
    const margins = expect.margins ?? [0, view.rows - 1];
    if (view.top !== margins[0] || view.bottom !== margins[1]) {
        return { part: 'margins', note: `Margins ${view.top + 1};${view.bottom + 1} differ from the contracted ${margins[0] + 1};${margins[1] + 1}` };
    }
    if (view.origin !== (expect.origin ?? false)) return { part: 'origin', note: `Origin mode is ${view.origin ? 'set' : 'reset'} but ${expect.origin ? 'set' : 'reset'} was contracted` };
    if (view.autoWrap !== (expect.autoWrap ?? true) || view.insert || view.pending !== (expect.pending ?? false) || view.history !== 0) {
        return { part: 'modes', note: 'Auto-wrap, insert mode, pending wrap or scrollback drifted from the contracted state' };
    }
    if (view.visible !== (expect.visible ?? true)) return { part: 'visibility', note: `The stored cursor is ${view.visible ? 'visible' : 'hidden'} but ${expect.visible ?? true ? 'visible' : 'hidden'} was contracted` };
    const wanted = expect.mode ?? 'default';
    const modeOk = wanted === 'default' ? view.mode.isDefault && view.mode.style === baseline.mode.style && view.mode.blink === baseline.mode.blink
        : !view.mode.isDefault && view.mode.style === wanted.style && view.mode.blink === wanted.blink;
    if (!modeOk) return { part: 'cursor-mode', note: `Stored cursor mode ${JSON.stringify(view.mode)} differs from the contracted ${modeLabel(wanted)}` };
    const lines = expect.textSameAs === undefined ? Array.from({ length: view.rows }, (_, row) => expect.text?.[row] ?? '') : prior[expect.textSameAs].lines;
    const row = lines.findIndex((line, index) => view.lines[index] !== line.trimEnd());
    if (row >= 0) return { part: 'viewport', row, note: `Viewport row ${row} differs from the contracted contents` };
    if (!view.storedDefault) return { part: 'attributes', note: 'A cell stores a non-default attribute or color that no contracted operation sets' };
    const cursor = expect.cursorSameAs === undefined ? expect.cursor : prior[expect.cursorSameAs].cursor;
    if (view.cursor[0] !== cursor[0] || view.cursor[1] !== cursor[1]) {
        return { part: 'cursor', note: `The physical cursor is at ${view.cursor.join(',')} instead of the contracted ${cursor.join(',')}` };
    }
    return null;
}

/** The first way a raw CPR differs from the report the OBSERVED physical state implies, or null. A missing reply is its own refusal. This cross-checks
 * two independent observables (the stored physical cursor and the terminal's own report); the contracted expectation is judged on the physical state. */
export function cursorReportMismatch(report, view) {
    if (!report) return { part: 'cpr-missing', note: 'No cursor position report was received' };
    // Under DECOM a cursor outside the region has no region-relative report; the physical mismatch itself is the finding.
    if (view.origin && (view.cursor[0] < view.top || view.cursor[0] > view.bottom)) return null;
    const wanted = expectedCursorReport(view);
    if (report.row !== wanted[0] || report.col !== wanted[1]) {
        const physical = view.origin ? 'region-relative' : 'absolute';
        return { part: 'cpr', note: `The reply ${JSON.stringify(report.raw)} differs from the ${physical} report ${wanted.join(';')} the observed physical cursor implies` };
    }
    return null;
}

/** A group under construction: feed and read steps, the per-read record and the pinned bookkeeping the recording contexts need. */
class GroupBuilder {
    constructor(id, kind) { this.id = id; this.kind = kind; this.steps = []; this.reads = []; this.pinned = []; this.readAt = {}; this.captureAt = []; this.feeds = 0; this.tailCount = 0; }
    /** The last `count` pinned feeds are the probe's `finally` cleanup, which also runs when the probe returns early. */
    tail(count) { this.tailCount = count; return this; }
    /** One exact feed (setup, control or extension bytes). */
    feed(bytes) { this.steps.push(planFeed(bytes)); this.feeds++; return this; }
    /** One exact pinned feed. */
    pin(bytes) { this.pinned.push(bytes); return this.feed(bytes); }
    /** One read. `kind` is state, cpr or capture; `role` is control, measured or restoration; `pinned` maps the read to the pinned code's
     * state read at the current pinned feed count; `pinnedCapture` marks the capture the pinned callback itself requests. */
    read(kind, role, expect, { timeMs, pinned = false, pinnedCapture = false, label } = {}) {
        this.steps.push(planRead(kind));
        const index = this.reads.length;
        this.reads.push({ kind, role, expect, label: label ?? `${this.id}:${index}`, ...(kind === 'capture' ? { timeMs: timeMs ?? 0 } : {}) });
        if (pinned) this.readAt[this.pinned.length] = index;
        if (pinnedCapture) this.captureAt.push(index);
        return this;
    }
    done() {
        return Object.freeze({ id: this.id, kind: this.kind, plan: Object.freeze([...this.steps]), reads: Object.freeze(this.reads.map(Object.freeze)),
            pinnedFeeds: Object.freeze([...this.pinned]), pinnedReadAt: Object.freeze({ ...this.readAt }), pinnedCaptureAt: Object.freeze([...this.captureAt]), tailCount: this.tailCount });
    }
}

/** The stored state expectation helper. */
const state = (cursor, extra = {}) => ({ cursor, ...extra });

/** The cup-scroll-region groups: the pinned termless and app fixtures plus DECOM off/on, boundary and restoration controls. */
function cupGroups(rows) {
    const margins = [4, 14], region = { margins, origin: true };
    const target = Math.max(999, rows + 1);
    const groups = [];
    groups.push(new GroupBuilder('termless', 'pinned-termless').feed(cursorQualReset)
        .pin('\x1b[5;15r').pin('\x1b[?6h').pin('\x1b[1;1H')
        .read('state', 'measured', state([4, 0], region), { pinned: true, label: 'CUP 1;1 inside the DECOM region' })
        .pin('\x1b[?6l').pin('\x1b[r')
        .read('state', 'restoration', state([0, 0]), { label: 'origin mode and margins restored' }).tail(2).done());
    groups.push(new GroupBuilder('app', 'pinned-app').feed(cursorQualReset)
        .pin('\x1b[5;15r').pin('\x1b[?6h').pin('\x1b[1;1H')
        .read('cpr', 'measured', state([4, 0], region), { label: 'region-top: CUP 1;1' })
        .pin('\x1b[2;1H')
        .read('cpr', 'measured', state([5, 0], region), { label: 'region-row-2: CUP 2;1' })
        .pin(`\x1b[${target}B`)
        .read('cpr', 'measured', state([14, 0], region), { label: `region-bottom: CUD ${target}` })
        .pin('\x1b[?6l').pin('\x1b[r')
        .read('state', 'restoration', state([0, 0]), { label: 'origin mode and margins restored' }).tail(2).done());
    groups.push(new GroupBuilder('origin-off', 'control').feed(cursorQualReset).feed('\x1b[5;15r').feed('\x1b[1;1H')
        .read('cpr', 'control', state([0, 0], { margins }), { label: 'DECOM off: CUP 1;1 is the screen home' })
        .feed('\x1b[15;1H')
        .read('cpr', 'control', state([14, 0], { margins }), { label: 'DECOM off: CUP 15;1 is the last region row' })
        .feed('\x1b[3;3H')
        .read('cpr', 'control', state([2, 2], { margins }), { label: 'DECOM off: CUP 3;3 is above the region' }).done());
    groups.push(new GroupBuilder('origin-on', 'boundary').feed(cursorQualReset).feed('\x1b[5;15r').feed('\x1b[?6h').feed('\x1b[11;1H')
        .read('cpr', 'measured', state([14, 0], region), { label: 'DECOM on: CUP 11;1 is the last region row' })
        .feed('\x1b[12;1H')
        .read('cpr', 'measured', state([14, 0], region), { label: 'DECOM on: CUP 12;1 clamps at the region bottom' })
        .feed('\x1b[999A')
        .read('cpr', 'measured', state([4, 0], region), { label: 'DECOM on: CUU stops at the top margin' })
        .feed('\x1b[?6l').feed('\x1b[r').feed('\x1b[3;5H')
        .read('cpr', 'restoration', state([2, 4]), { label: 'DECOM and margins reset: CUP 3;5 is absolute again' }).done());
    return groups;
}

/** The hide groups: the pinned termless visibility sequence, the pinned app capture fixture with a re-rendered control, and restoration. */
function hideGroups() {
    const scene = { text: cursorSceneText };
    const steady = { ...scene, mode: requestedCursorMode(2) };
    const groups = [];
    groups.push(new GroupBuilder('termless', 'pinned-termless').feed(cursorQualReset)
        .read('state', 'control', state([0, 0]), { pinned: true, label: 'initial visibility' })
        .pin('\x1b[?25h').read('state', 'control', state([0, 0]), { pinned: true, label: 'shown' })
        .pin('\x1b[?25l').read('state', 'measured', state([0, 0], { visible: false }), { pinned: true, label: 'hidden' })
        .pin('\x1b[?25h').read('state', 'restoration', state([0, 0]), { pinned: true, label: 'initial visibility restored' }).tail(1).done());
    groups.push(new GroupBuilder('app', 'pinned-app').feed(cursorQualReset)
        .pin(hideClear).pin(hideSeed)
        .read('capture', 'control', state([1, 2], steady), { pinnedCapture: true, label: 'Shown steady block cursor requested at row 2 column 3' })
        .pin('\x1b[?25l')
        .read('capture', 'measured', state([1, 2], { ...steady, visible: false }), { pinnedCapture: true, label: 'Hidden cursor requested at the same cell' })
        .pin(hideCleanup).tail(1)
        .read('state', 'restoration', state([0, 0]), { label: 'cleanup restored the default visible cursor' })
        .feed(hideSeed)
        .read('capture', 'control', state([1, 2], steady), { label: 'the control scene rendered again' })
        .feed(cursorQualReset)
        .read('state', 'restoration', state([0, 0]), { label: 'fixture reset' }).done());
    return groups;
}

/** The shape groups: the pinned termless style, the pinned app captures (visible and hidden phase), and steady-shape reference renders. */
function shapeGroups() {
    const scene = { text: cursorSceneText };
    const groups = [];
    groups.push(new GroupBuilder('termless', 'pinned-termless').feed(cursorQualReset)
        .read('state', 'control', state([0, 0]), { pinned: true, label: 'host-default shape before the request' })
        .pin('\x1b[6 q').read('state', 'measured', state([0, 0], { mode: requestedCursorMode(6) }), { pinned: true, label: 'CSI 6 SP q stores a steady bar' })
        .feed('\x1b[0 q').read('state', 'restoration', state([0, 0]), { label: 'host-default shape restored' }).done());
    groups.push(new GroupBuilder('app', 'pinned-app-repaired').feed(cursorQualReset).feed(cursorScene)
        .read('capture', 'control', state([1, 2], scene), { pinnedCapture: true, label: 'Default cursor shape' })
        .pin('\x1b[5 q')
        .read('capture', 'measured', state([1, 2], { ...scene, mode: requestedCursorMode(5) }), { pinnedCapture: true, label: 'Bar cursor shape' })
        .read('capture', 'measured', state([1, 2], { ...scene, mode: requestedCursorMode(5) }), { timeMs: cursorQualPhases.hidden, label: 'Bar cursor shape in the hidden half-period' })
        .pin('\x1b[0 q').tail(1)
        .read('state', 'restoration', state([1, 2], scene), { label: 'host-default shape restored beside the unchanged scene' }).done());
    const shapes = new GroupBuilder('shapes', 'control').feed(cursorQualReset).feed(cursorScene);
    for (const code of [2, 4, 6]) {
        shapes.feed(`\x1b[${code} q`).read('capture', 'control', state([1, 2], { ...scene, mode: requestedCursorMode(code) }), { label: `steady ${modeLabel(requestedCursorMode(code))} reference` });
    }
    shapes.feed('\x1b[?25l').read('capture', 'control', state([1, 2], { ...scene, mode: requestedCursorMode(6), visible: false }), { label: 'hidden cursor reference' })
        .feed(cursorQualReset).read('state', 'restoration', state([0, 0]), { label: 'fixture reset' });
    groups.push(shapes.done());
    return groups;
}

/** The reverse-wrap groups: the pinned termless fixture, the repaired pinned app fixture, and the mode-off control. */
function reverseWrapGroups(cols) {
    const full = 'A'.repeat(cols), written = { 0: full, 1: 'B' }, text = { text: written };
    const wrapped = state([1, 1], text), reverted = state([0, cols - 1], text);
    const groups = [];
    groups.push(new GroupBuilder('termless', 'pinned-termless').feed(cursorQualReset)
        .read('state', 'control', state([0, 0]), { pinned: true, label: 'measured grid and initial auto-wrap' })
        .pin('\x1b[?7h').pin('\x1b[?45h').pin('\x1b[H')
        .read('state', 'control', state([0, 0]), { pinned: true, label: 'home-position control' })
        .pin(full + 'B')
        .read('state', 'control', wrapped, { pinned: true, label: 'second-row displacement' })
        .pin('\b\b')
        .read('state', 'measured', reverted, { pinned: true, label: 'two BS reverse the wrap to the last column of row 1' })
        .pin('\x1b[?45l').pin('\x1b[?7h').tail(2)
        .read('state', 'restoration', { cursorSameAs: 3, textSameAs: 3 }, { label: 'reverse-wrap off, auto-wrap restored, fixture state unchanged' }).done());
    groups.push(new GroupBuilder('app', 'pinned-app-repaired').feed(cursorQualReset)
        .read('state', 'control', state([0, 0]), { label: 'geometry before the callback' })
        .pin('\x1b[?45h').pin('\x1b[?7h').pin('\x1b[H')
        .read('cpr', 'control', state([0, 0]), { label: 'home' })
        .pin(full + 'B')
        .read('cpr', 'control', wrapped, { label: 'wrapped' })
        .pin('\b\b')
        .read('cpr', 'measured', reverted, { label: 'reverted' })
        .pin('\x1b[?45l').tail(1)
        .read('state', 'restoration', { cursorSameAs: 3, textSameAs: 3 }, { label: 'geometry after the callback and reverse-wrap off' }).done());
    groups.push(new GroupBuilder('mode-off', 'control').feed(cursorQualReset).feed('\x1b[?7h').feed('\x1b[H').feed(full + 'B')
        .read('cpr', 'control', wrapped, { label: 'the same fixture without ?45: wrapped' })
        .feed('\b\b')
        .read('cpr', 'control', state([1, 0], text), { label: 'without ?45 the second BS stops at column 1 of row 2' })
        .feed(cursorQualReset).read('state', 'restoration', state([0, 0]), { label: 'fixture reset' }).done());
    return groups;
}

/** The deciding pinned paths of each case: termless probes decide stored state; apps decide only reverse-wrap. */
const pinnedPaths = Object.freeze({
    'cursor.cup-scroll-region': Object.freeze({ termless: 'decides', app: 'inconclusive-by-design' }),
    'cursor.hide': Object.freeze({ termless: 'decides', app: 'inconclusive-by-design' }),
    'cursor.shape': Object.freeze({ termless: 'decides', app: 'inconclusive-by-design' }),
    'cursor.reverse-wrap': Object.freeze({ termless: 'decides', app: 'decides' }),
});

/** Build one frozen contract from its groups. */
function finish(rows, cols, { caseId, groups, pinnedCriterion, appCriterion, pixelChecks = [], fixture, measuredParts = [] }) {
    const plan = groups.flatMap(group => group.plan);
    const contract = { caseId, probeId: caseId, rows, cols, groups: Object.freeze(groups), plan: Object.freeze(plan), pinnedPaths: pinnedPaths[caseId], measuredParts: Object.freeze([...measuredParts]),
        pinnedCriterion, appCriterion, pixelChecks: Object.freeze(pixelChecks),
        terminalFixture: Object.freeze({ termless: groups.find(group => group.kind === 'pinned-termless').pinnedFeeds,
            app: groups.find(group => group.kind.startsWith('pinned-app')).pinnedFeeds, demand: fixture }),
        appearanceScope: 'stored state, physical cursor, retained CPR replies and controlled synthetic render phases of ASCII fixtures at the measured geometry; no desktop blink cadence, focus, Unicode, wide-cell, reflow or resize claim' };
    contract.grade = observation => gradeCursorQual(contract, observation);
    contract.observed = observation => briefCursorQual(contract, observation);
    return Object.freeze(contract);
}

/** The pixel checks each case needs (the grading module computes them from the retained PNGs; the contract only names them and their role). */
const hideChecks = [
    ['hide.control-cursor-visible', 'control', 'the control frame paints the cursor cell differently from the page background'],
    ['hide.target-cell-blank', 'measured', 'the hidden frame paints the cursor cell exactly as the page background'],
    ['hide.unrelated-pixels-unchanged', 'measured', 'every pixel outside the cursor cell is identical in the control and hidden frames'],
    ['hide.rerender-matches-control', 'control', 'the control scene re-rendered after the fixture restored is pixel-identical to the first control'],
];
const shapeChecks = [
    ['shape.reference-shapes', 'control', 'steady block, underline and bar references are distinct, show their shapes and differ from the hidden reference'],
    ['shape.default-visible-and-distinct', 'control', 'the host-default control paints a visible cursor that differs from the requested bar'],
    ['shape.target-is-bar', 'measured', 'the requested shape paints exactly the steady bar reference in the visible half-period'],
    ['shape.target-blink-hidden-phase', 'measured', 'the same requested shape paints the page background in the hidden half-period (blink is separate from shape) and leaves every other pixel as in the visible half-period'],
    ['shape.unrelated-pixels-unchanged', 'measured', 'every pixel outside the cursor cell is identical in the control and requested-shape frames'],
];

/** Build the pinned contracts for the measured geometry. */
export function makeCursorQualContracts(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < cursorQualMinimum.rows || rows > 128 || !Number.isSafeInteger(cols) || cols < cursorQualMinimum.cols || cols > 256) {
        throw new Error('Cursor contracts need at least 15 rows and 48 columns, and at most 128 rows and 256 columns (the observer limits).');
    }
    return Object.freeze([
        finish(rows, cols, { caseId: 'cursor.cup-scroll-region', groups: cupGroups(rows), measuredParts: ['margins', 'origin'],
            pinnedCriterion: 'DECSTBM 5;15 then DECOM then CUP 1;1 leaves the physical cursor at row 4, column 0 (0-based); the pinned app callback records only region-relative CPR replies',
            appCriterion: 'CUP 1;1 and CUP 2;1 land on physical rows 4 and 5 inside margins 5;15 and CUD stops at the bottom margin (physical row 14), with CPR region-relative',
            fixture: 'the physical cursor, actual margins and origin mode before and after, with the raw CPR retained separately' }),
        finish(rows, cols, { caseId: 'cursor.hide', groups: hideGroups(), pixelChecks: hideChecks,
            pinnedCriterion: 'stored cursor visibility changes shown to hidden and the initial state is restored',
            appCriterion: 'a steady block cursor at row 2 column 3 is painted and DECTCEM reset removes exactly that paint, leaving all other pixels, the text and the cursor position unchanged',
            fixture: 'stored visibility and controlled pixels of the visible control, the hidden target and a re-rendered control' }),
        finish(rows, cols, { caseId: 'cursor.shape', groups: shapeGroups(), pixelChecks: shapeChecks,
            pinnedCriterion: 'CSI 6 SP q stores a beam (bar) style',
            appCriterion: 'CSI 5 SP q stores a blinking bar and paints the bar shape (visible half-period) that a steady bar paints, distinct from the default and from block and underline, and the page background in the hidden half-period',
            fixture: 'stored shape, blink and host-default intent plus controlled pixels of the default, requested, steady-reference and hidden-phase cursor' }),
        finish(rows, cols, { caseId: 'cursor.reverse-wrap', groups: reverseWrapGroups(cols),
            pinnedCriterion: 'with ?7 and ?45 set, A x cols then B wraps to row 2 column 2 and two BS return to row 1, last column',
            appCriterion: 'the same movement observed physically and by CPR (1;1, 2;2, 1;cols), geometry proven before and after, with the ?45-off control staying on row 2',
            fixture: 'the physical cursor and CPR across the wrap boundary with and without the mode, and the declared geometry before and after the callback' }),
    ]);
}

/** Group the read screens (and CPR/pixel results) of one case by group and read. */
export function cursorQualObservation(contract, screens, extras = []) {
    const total = contract.groups.reduce((sum, group) => sum + group.reads.length, 0);
    if (!Array.isArray(screens) || screens.length !== total) throw new Error('Missing cursor read captures.');
    let offset = 0;
    const groups = contract.groups.map(group => {
        const reads = group.reads.map((read, index) => ({ ...read, view: cursorQualView(screens[offset + index]), report: extras[offset + index]?.report ?? null }));
        offset += group.reads.length;
        return { id: group.id, kind: group.kind, reads };
    });
    return { groups };
}

/** Independent grading: every control must hold, then every measured read and pixel check must equal its expectation. */
function gradeCursorQual(contract, observation) {
    const baseline = observation.baseline;
    if (!baseline || baseline.rows < cursorQualMinimum.rows || baseline.cols < cursorQualMinimum.cols || baseline.rows !== contract.rows || baseline.cols !== contract.cols) {
        return { outcome: 'inconclusive', note: `Measured geometry ${baseline?.rows}x${baseline?.cols} cannot fit the cursor fixtures` };
    }
    const failures = [];
    for (const group of observation.groups) {
        const prior = [];
        for (const read of group.reads) {
            prior.push(read.view);
            const mismatch = cursorQualMismatch(read.view, read.expect, baseline, prior);
            // A control that exercises the very state under test (margins and origin mode of the CUP case) is measured, not merely a precondition.
            const role = read.role === 'control' && contract.measuredParts.includes(mismatch?.part) ? 'measured' : read.role;
            if (mismatch) failures.push({ group: group.id, label: read.label, role, ...mismatch });
            // Two independent observables disagreeing voids the measurement: it is a failed control, never a measured mismatch.
            const report = read.kind === 'cpr' ? cursorReportMismatch(read.report, read.view) : null;
            if (report) failures.push({ group: group.id, label: read.label, role: 'control', ...report });
        }
    }
    for (const check of observation.pixelChecks ?? []) if (!check.passed) failures.push({ group: 'pixels', label: check.id, role: check.role, part: 'pixels', note: check.note });
    const blocking = failures.find(failure => failure.role !== 'measured');
    if (blocking) return { outcome: 'inconclusive', note: `Control ${blocking.group}/${blocking.label} was not established (${blocking.note}), so the operation cannot be graded` };
    const measured = failures[0];
    if (measured) return { outcome: 'unsupported', note: `${measured.group}/${measured.label}: ${measured.note}` };
    const layers = ['stored state', 'physical cursor'];
    if (contract.groups.some(group => group.reads.some(read => read.kind === 'cpr'))) layers.push('cursor position report');
    if (contract.pixelChecks.length) layers.push('controlled pixels');
    return { outcome: 'supported', layers };
}

/** The observed record: each read's compact state, whether it matched, and the raw CPR beside the physical cursor. */
function briefCursorQual(contract, observation) {
    const baseline = observation.baseline;
    const groups = observation.groups.map(group => {
        const prior = [];
        const reads = group.reads.map(read => {
            prior.push(read.view);
            const mismatch = cursorQualMismatch(read.view, read.expect, baseline, prior);
            const report = read.kind === 'cpr' ? cursorReportMismatch(read.report, read.view) : null;
            return { label: read.label, kind: read.kind, role: read.role, matchesExpectation: mismatch === null, ...(mismatch ? { differs: mismatch.part } : {}),
                ...(read.kind === 'cpr' ? { cprConsistentWithPhysicalCursor: report === null } : {}),
                physicalCursor: read.view.cursor, margins: [read.view.top, read.view.bottom], origin: read.view.origin, visible: read.view.visible,
                cursorMode: read.view.mode, ...(read.kind === 'cpr' ? { rawCpr: read.report?.raw ?? null } : {}) };
        });
        return { id: group.id, kind: group.kind, reads };
    });
    return { geometry: { rows: baseline.rows, cols: baseline.cols }, hostDefaultMode: { style: baseline.mode.style, blink: baseline.mode.blink },
        groups, pixelChecks: (observation.pixelChecks ?? []).map(({ id, role, passed, note }) => ({ id, role, passed, note })) };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeCursorQualCase(contract, observation, revision = cursorQualContractsVersion) {
    if (revision !== 1) throw new Error(`Unknown cursor contracts revision ${revision}.`);
    if (!observation || !Array.isArray(observation.groups) || observation.groups.length !== contract.groups.length) throw new Error(`Missing ${contract.caseId} read captures.`);
    return contract.grade(observation);
}

export { planFeeds as cursorQualFeeds, planReads as cursorQualReads } from './graded-plan.mjs';
export { at as cursorQualAt };
