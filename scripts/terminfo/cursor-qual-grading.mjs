// Parent-side report builder for the cursor qualification. Re-derives every conclusion from the retained parent journal, the retained CPR
// exchanges, the retained controlled captures and the independent contract grader, refuses any tampering, and requires the pinned probes' own
// grading (the termless AND the app callbacks, plus the retained original failures and the geometry repair record) to agree: each pinned result
// is a pure function of the observed evidence of its own fixture. The contract may never be more permissive than a pinned probe that DECIDES; the
// pinned app callbacks that are inconclusive by construction (cup-scroll-region, hide, shape) cannot decide, and what the contract measures beyond
// them is recorded as an explicit divergence rather than hidden.
import { cursorQualCaseIds, cursorQualContractsVersion, cursorQualNoncePrefix, cursorQualSuiteId, makeCursorQualContracts, gradeCursorQualCase,
    cursorQualObservation, cursorQualView, validateCursorQualScreen, pinnedCursorNotes, hideClear, hideSeed, hideCleanup } from './cursor-qual-contracts.mjs';
import { cursorQualAdapterId, cursorGeometryRepairId, cursorRepairedProbeIds } from './cursor-qual-grading-collector.mjs';
import { cursorQualTermlessContext, cursorQualAppBackend, cursorQualGroupOffsets, runCursorQualPinned } from './cursor-qual-collector.mjs';
import { validateGradedJournal, validateExactFeeds, planFeeds, planReads, requirePinnedAgreement } from './graded-plan.mjs';
import { retainedCursorCapture, stablePixelContext, defaultCursorPolicy } from './pixels-capture.mjs';
import { regionPixels } from './pixels.mjs';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { validateCursorReport } from './cursor-report.mjs';

/** The adapter this extension derives from. */
export const cursorQualDerivedFrom = 'ntilde-owned-attr-rect-grading-v1';
/** SHA-256 of the verbatim callback sources the geometry repair keeps (terminfo.dev@4.0.0). */
export const pinnedCallbackDigests = Object.freeze({
    'cursor.reverse-wrap': Object.freeze({ termlessSha256: 'e826f4d412828757bab79786f121ae25e288febc77d8ef61219f27981f2439d8', termSha256: '9a064034a510d62a3b76b7b81852200ddb48b7bb769cb4760ea755b45b4f61f6' }),
    'cursor.shape': Object.freeze({ termlessSha256: '9ef2e561a52d7ee6839d06c036f3141d39e05d826f41b312ce66a55c2b8c3a98', termSha256: '4bae0ac61fc1c050563389ef88a61d5dc0fdf8932295b98fffd3b23687616870' }),
});
/** The exact collector error each ORIGINAL callback ends in, and the bytes it sent first. */
const undeclared = id => ({ name: 'UndeclaredTerminalGeometry', message: `App callback ${id} accessed undeclared or unavailable terminal geometry` });

/** The pinned parserStateResult, byte for byte in behavior. */
function parserStateResult(pass, expected, state, note) {
    const response = JSON.stringify(state);
    return {
        pass: pass === true,
        response,
        ...note && { note },
        observation: { outcome: pass === null ? 'inconclusive' : pass ? 'supported' : 'unsupported',
            ...pass === null && { reason: 'insufficient-evidence' }, evidence: 'parser-state', ...note && { note } },
        ...pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] },
    };
}
const headlessPosition = (ctx, row, col) => {
    const cursor = ctx.getCursor();
    return parserStateResult(cursor.y === row && cursor.x === col, `cursor row=${row}, col=${col} (0-based)`, cursor);
};

/** Ports of the pinned termless probe bodies, run over the same recording context the live pinned code receives. */
export const termlessPorts = Object.freeze({
    'cursor.cup-scroll-region'(ctx) {
        try {
            ctx.feed('\x1B[5;15r');
            ctx.feed('\x1B[?6h');
            ctx.feed('\x1B[1;1H');
            return headlessPosition(ctx, 4, 0);
        } finally {
            try { ctx.feed('\x1B[?6l'); } finally { ctx.feed('\x1B[r'); }
        }
    },
    'cursor.hide'(ctx) {
        const before = ctx.getCursor().visible;
        const expected = 'Cursor parser visibility changes from shown to hidden and its initial state is restored';
        if (before === null) return parserStateResult(null, expected, { before }, 'Initial cursor visibility readback is unavailable');
        let shown = null, hidden = null;
        try {
            ctx.feed('\x1B[?25h');
            shown = ctx.getCursor().visible;
            if (shown === true) {
                ctx.feed('\x1B[?25l');
                hidden = ctx.getCursor().visible;
            }
        } finally {
            ctx.feed(before ? '\x1B[?25h' : '\x1B[?25l');
        }
        const restored = ctx.getCursor().visible;
        const qualified = shown === true && hidden !== null && restored === before;
        return parserStateResult(qualified ? hidden === false : null, expected, { before, shown, hidden, restored },
            qualified ? pinnedCursorNotes.hideParser : pinnedCursorNotes.hideRestoration);
    },
    'cursor.shape'(ctx) {
        ctx.feed('\x1B[6 q');
        const style = ctx.getCursor().style;
        return parserStateResult(style === null ? null : style === 'beam', 'DECSCUSR reports a beam cursor after CSI 6 SP q', { style },
            style === null ? 'Cursor style readback is unavailable' : undefined);
    },
    'cursor.reverse-wrap'(ctx) {
        const cols = ctx.cols;
        const rows = ctx.getScrollback().screenLines;
        const expected = 'Backspace reverses a wrap across the measured row width';
        if (!Number.isSafeInteger(cols) || cols < 2 || !Number.isSafeInteger(rows) || rows < 2) {
            return parserStateResult(null, expected, { rows, cols }, 'Measured grid needs two rows and at least two columns');
        }
        const originallyAutoWrap = ctx.getMode('autoWrap');
        try {
            ctx.feed('\x1B[?7h');
            ctx.feed('\x1B[?45h');
            ctx.feed('\x1B[H');
            const start = ctx.getCursor();
            if (start.y !== 0 || start.x !== 0) return parserStateResult(null, expected, { cols, start }, 'Home-position control was not measured');
            ctx.feed('A'.repeat(cols) + 'B');
            const wrapped = ctx.getCursor();
            if (wrapped.y !== 1 || wrapped.x !== 1) return parserStateResult(null, expected, { cols, start, wrapped }, 'Second-row displacement was not measured');
            ctx.feed('\b\b');
            const cursor = ctx.getCursor();
            return parserStateResult(cursor.y === 0 && cursor.x === cols - 1, expected, { cols, start, wrapped, cursor });
        } finally {
            ctx.feed('\x1B[?45l');
            ctx.feed(originallyAutoWrap ? '\x1B[?7h' : '\x1B[?7l');
        }
    },
});

const inconclusiveNone = note => ({ pass: false, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note } });

/** Synchronous ports of the pinned app callbacks. The live callbacks are async only because the stock collector awaits its terminal; over retained
 * evidence every reply is already in hand, so these are the same logic with the awaits removed (the live async callbacks are replayed separately). */
export const appPorts = Object.freeze({
    'cursor.cup-scroll-region'(ctx) {
        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 15 || ctx.cols < 1) {
            return { pass: false, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note: 'cursor.cup-scroll-region fixture needs at least 15x1 measured cells' } };
        }
        const rows = ctx.rows, measured = [];
        const ask = step => {
            const pos = ctx.queryCursorPosition();
            measured.push({ step, report: pos });
            return pos;
        };
        try {
            ctx.write('\x1B[5;15r');
            ctx.write('\x1B[?6h');
            ctx.write('\x1B[1;1H');
            const top = ask('region-top');
            ctx.write('\x1B[2;1H');
            const second = ask('region-row-2');
            const target = Math.max(999, rows + 1);
            ctx.write('\x1B[' + target + 'B');
            const bottom = ask('region-bottom');
            if (!top || !second || !bottom) {
                return { pass: false, response: JSON.stringify({ rows, measured }), observation: { outcome: 'inconclusive', reason: 'no-response', evidence: 'query' } };
            }
            const relative = [top, second, bottom].map(sample => sample.row + ';' + sample.col).join(', ');
            return { pass: false, response: JSON.stringify({ rows, measured }),
                observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query',
                    note: 'DECOM makes CPR region-relative (VT100: region top is 1, bottom is 11); measured ' + relative + ' — the physical row 5 and the margins still need an independent observable' } };
        } finally {
            try { ctx.write('\x1B[?6l'); } finally { ctx.write('\x1B[r'); }
        }
    },
    'cursor.hide'(ctx) {
        const rows = ctx.rows, cols = ctx.cols, capture = ctx.capture;
        if (!capture || !Number.isSafeInteger(rows) || !Number.isSafeInteger(cols) || rows < 3 || cols < 6) {
            return inconclusiveNone(`Cursor pixel fixture needs capture and measured 3x6 geometry; measured ${rows}x${cols}`);
        }
        try {
            ctx.write(hideClear);
            ctx.write(hideSeed);
            const control = capture({ role: 'control', label: 'Shown steady block cursor requested at row 2 column 3' });
            ctx.write('\x1B[?25l');
            const target = capture({ role: 'target', label: 'Hidden cursor requested at the same cell' });
            const observed = JSON.stringify({ rows, cols, cursor: { row: 2, col: 3 }, control, target });
            return { pass: false, response: observed,
                observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'pixels', screenshotRef: target.ref, frames: [control, target], note: pinnedCursorNotes.hidePixels },
                assertions: [{ kind: 'positive', expected: 'Control visibly shows a cursor at row 2 column 3 between L and R; target removes that cursor while other seeded content is unchanged',
                    observed, note: 'capture-only assertion; cursor visibility and the control must be independently reviewed' }] };
        } finally {
            ctx.write(hideCleanup);
        }
    },
    'cursor.shape'(ctx) {
        const control = ctx.capture({ role: 'control', label: 'Default cursor shape' });
        try {
            ctx.write('\x1B[5 q');
            const target = ctx.capture({ role: 'target', label: 'Bar cursor shape' });
            return { pass: false,
                response: JSON.stringify({ rows: ctx.rows, cols: ctx.cols, cursorCell: null,
                    detail: 'DECSCUSR shape fixture does not establish the cursor cell; only measured geometry and roles', control: control.label, target: target.label }),
                observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'pixels', screenshotRef: target.ref, frames: [control, target], note: pinnedCursorNotes.shapePixels } };
        } finally {
            ctx.write('\x1B[0 q');
        }
    },
    'cursor.reverse-wrap'(ctx) {
        const expected = 'Backspace from column 1 of row 2 reverses the wrap to the last column of row 1';
        if (!Number.isSafeInteger(ctx.rows) || !Number.isSafeInteger(ctx.cols) || ctx.rows < 2 || ctx.cols < 2) {
            return { pass: false, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note: 'reverse-wrap fixture needs two measured rows and at least two columns' } };
        }
        const cols = ctx.cols, measured = [];
        const ask = step => {
            const pos = ctx.queryCursorPosition();
            measured.push({ step, report: pos });
            return pos;
        };
        const inconclusive = note => ({ pass: false, response: JSON.stringify({ measured }),
            observation: { outcome: 'inconclusive', ...note ? { reason: 'insufficient-evidence', note } : { reason: 'no-response' }, evidence: 'query' } });
        ctx.write('\x1B[?45h');
        try {
            ctx.write('\x1B[?7h');
            ctx.write('\x1B[H');
            const home = ask('home');
            if (!home) return inconclusive();
            if (home.row !== 1 || home.col !== 1) return inconclusive('Home position was not reported at 1;1');
            ctx.write('A'.repeat(cols) + 'B');
            const wrapped = ask('wrapped');
            if (!wrapped) return inconclusive();
            if (wrapped.row !== 2 || wrapped.col !== 2) return inconclusive('Deferred wrap was not reported at 2;2');
            ctx.write('\b\b');
            const reverted = ask('reverted');
            if (!reverted) return inconclusive();
            const response = JSON.stringify({ measured });
            if (reverted.row === 1 && reverted.col === cols) {
                return { pass: true, response, observation: { outcome: 'supported', evidence: 'query' }, assertions: [{ kind: 'positive', expected, observed: response }] };
            }
            return { pass: false, response, observation: { outcome: 'unsupported', evidence: 'query', note: 'Measured wrap was not reversed to row 1 column ' + cols },
                assertions: [{ kind: 'negative', expected, observed: response }] };
        } finally {
            ctx.write('\x1B[?45l');
        }
    },
});

/** Re-derive the pinned termless and app results of one case over its observed evidence, without the bundle. */
export function replayCursorQualPinned(contract, screens, reads) {
    const geometry = { rows: screens[0].rows, cols: screens[0].cols };
    const result = {};
    for (const { group, offset } of cursorQualGroupOffsets(contract)) {
        const groupScreens = screens.slice(offset, offset + group.reads.length), groupReads = reads.slice(offset, offset + group.reads.length);
        if (group.kind === 'pinned-termless') {
            const context = cursorQualTermlessContext(contract, group, groupScreens);
            result.termless = termlessPorts[contract.caseId](context);
            if (!context.complete) throw new Error(`Pinned ${contract.caseId} termless port did not replay its complete fixture.`);
        } else if (group.kind.startsWith('pinned-app')) {
            const backend = cursorQualAppBackend(contract, group, groupScreens, groupReads), writes = [];
            const ctx = { rows: geometry.rows, cols: geometry.cols, write: text => { writes.push(text); backend.write(text); },
                queryCursorPosition: () => backend.queryCursorPosition(), capture: checkpoint => backend.capture(checkpoint) };
            const replayed = appPorts[contract.caseId](ctx);
            if (!backend.complete) throw new Error(`Pinned ${contract.caseId} app port did not replay its complete fixture.`);
            result.app = { ...replayed, writes };
        }
    }
    return result;
}

/** Compare the pinned termless result to the contract. A deciding pinned probe may never be less strict than the contract. */
export function reconcileCursorQual(contract, pinned, contractResult) {
    const termless = pinned.termless.observation.outcome, app = pinned.app.observation.outcome;
    if (contractResult.outcome === 'supported') {
        if (termless !== 'supported') throw new Error(`Contract ${contract.caseId} grading is more permissive than the pinned termless probe.`);
        if (contract.pinnedPaths.app === 'decides' && app !== 'supported') throw new Error(`Contract ${contract.caseId} grading is more permissive than the pinned app callback.`);
    }
    if (contract.pinnedPaths.app === 'inconclusive-by-design' && app !== 'inconclusive') throw new Error(`Pinned ${contract.caseId} app callback was expected to be inconclusive by construction.`);
    const divergences = [];
    if (termless !== contractResult.outcome) {
        divergences.push({ against: 'pinned-termless', kind: termless === 'inconclusive' ? 'contract-measured-mismatch' : termless === 'supported' ? 'contract-stricter-than-pinned' : 'contract-more-cautious-than-pinned',
            pinnedOutcome: termless, contractOutcome: contractResult.outcome });
    }
    if (app !== contractResult.outcome) {
        divergences.push({ against: 'pinned-app', kind: contract.pinnedPaths.app === 'inconclusive-by-design' ? 'contract-measures-beyond-pinned' : app === 'supported' ? 'contract-stricter-than-pinned' : 'contract-more-cautious-than-pinned',
            pinnedOutcome: app, contractOutcome: contractResult.outcome });
    }
    return divergences;
}

/** The geometry-dependent report spec: the contracts follow the measured geometry of the retained start frame. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, false);
    validateCursorQualScreen(screen);
    return screen;
};

/** Total read groups across a suite's contracts, plus start, bind and finish frames. */
const frameCountOf = contracts => 3 + contracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. */
export function validateCursorQualEvidence(frames, run, runId, adapter) {
    const start = frames?.[0]?.screen;
    const contracts = makeCursorQualContracts(start?.rows, start?.cols);
    validateGradedJournal(frames, { frameCount: frameCountOf(contracts), prefix: 'curq', noncePrefix: cursorQualNoncePrefix, runId, label: 'Cursor', screenOf });
    if (!adapter || adapter.id !== cursorQualAdapterId || run.adapterId !== cursorQualAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== cursorQualDerivedFrom || run.geometryRepairId !== cursorGeometryRepairId) {
        throw new Error('Invalid Cursor adapter identity.');
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...cursorQualCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== contracts.length) {
        throw new Error('Cursor run does not cover the contracted cases.');
    }
    return contracts;
}

/** The barrier a read must carry: that of the preceding feed, or of its own CPR exchange. */
function crossCheckCursorCapture(contract, recorded, readFrames) {
    validateExactFeeds(contract, recorded.feeds, 'contract');
    const pattern = /^\x1b\[\?[\d;]+c$/;
    if (!Array.isArray(recorded.barriers) || recorded.barriers.length !== planFeeds(contract).length || recorded.barriers.some(barrier => !pattern.test(barrier ?? ''))
        || !Array.isArray(recorded.reads) || recorded.reads.length !== planReads(contract)) throw new Error(`Invalid ${contract.caseId} cursor capture record.`);
    const planned = contract.groups.flatMap(group => group.reads);
    let feedSteps = 0, readSteps = 0, last;
    for (const step of contract.plan) {
        if (step.feed) { last = recorded.barriers[feedSteps++]; continue; }
        const read = recorded.reads[readSteps], frame = readFrames[readSteps], wanted = planned[readSteps];
        if (read.kind !== step.read || read.kind !== wanted.kind) throw new Error(`Wrong ${contract.caseId} read kind.`);
        let barrier = last;
        if (read.kind === 'cpr') {
            const report = validateCursorReport(read.cpr);
            barrier = report.barrier;
            if (JSON.stringify(report) !== JSON.stringify(validateCursorReport(frame.cpr))) throw new Error(`Cursor ${contract.caseId} report differs from its read frame.`);
        } else if (frame.cpr !== undefined || read.cpr !== undefined) throw new Error(`Unexpected ${contract.caseId} cursor report.`);
        if (read.kind === 'capture') {
            if (read.timeMs !== wanted.timeMs || frame.pixel?.presentationTimeMs !== wanted.timeMs || frame.pixel.sha256 !== read.pixelSha256) throw new Error(`Cursor ${contract.caseId} capture differs from its read frame.`);
        } else if (frame.pixel !== undefined || read.pixelSha256 !== undefined || read.timeMs !== undefined) throw new Error(`Unexpected ${contract.caseId} cursor capture.`);
        if (frame.barrier !== barrier || read.barrier !== barrier) throw new Error(`Cursor ${contract.caseId} barrier differs from its read frame.`);
        if (JSON.stringify(read.cursor) !== JSON.stringify([frame.screen.cursorRow, frame.screen.cursorCol])) throw new Error(`Cursor ${contract.caseId} cursor differs from its read frame.`);
        last = barrier;
        readSteps++;
    }
}

const rgba = argb => [(argb >>> 16) & 255, (argb >>> 8) & 255, argb & 255, argb >>> 24];
/** The pixels of one cell of a decoded capture. */
const cellOf = (capture, row, col) => regionPixels(capture.image, capture.details.grid, { row, col, rows: 1, cols: 1 });
/** Whether every pixel of a buffer is exactly the page background. */
function solidBackground(bytes, background) {
    for (let offset = 0; offset < bytes.length; offset += 4) {
        if (!background.every((value, channel) => bytes[offset + channel] === value)) return false;
    }
    return true;
}
/** The ink (non-background) bounding box and count of one cell. */
function inkOf(bytes, width, height, background) {
    let minX = width, maxX = -1, minY = height, maxY = -1, count = 0;
    for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) {
            const offset = (y * width + x) * 4;
            if (background.every((value, channel) => bytes[offset + channel] === value)) continue;
            count++; minX = Math.min(minX, x); maxX = Math.max(maxX, x); minY = Math.min(minY, y); maxY = Math.max(maxY, y);
        }
    }
    return { count, minX, maxX, minY, maxY };
}
/** Classify the cursor painted in one cell: block, underline, bar, none or other. */
export function cursorShapeOf(bytes, grid, background) {
    const { cellWidthPx: width, cellHeightPx: height } = grid;
    const ink = inkOf(bytes, width, height, background);
    if (ink.count === 0) return 'none';
    const boxWidth = ink.maxX - ink.minX + 1, boxHeight = ink.maxY - ink.minY + 1, solid = ink.count >= boxWidth * boxHeight * 0.9;
    if (!solid) return 'other';
    if (boxWidth >= width * 0.9 && boxHeight >= height * 0.9) return 'block';
    if (boxWidth >= width * 0.9 && boxHeight <= height * 0.3 && ink.maxY >= height * 0.75) return 'underline';
    if (boxHeight >= height * 0.9 && boxWidth <= width * 0.3 && ink.minX <= width * 0.25) return 'bar';
    return 'other';
}
/** Every pixel outside one cell, as one buffer (the cell itself zeroed), so two frames can be compared without the cursor. */
function outsideCell(capture, row, col) {
    const { image, details: { grid } } = capture;
    const copy = Buffer.from(image.pixels);
    const x0 = grid.originXPx + col * grid.cellWidthPx, y0 = grid.originYPx + row * grid.cellHeightPx;
    for (let y = y0; y < y0 + grid.cellHeightPx; y++) copy.fill(0, (y * image.width + x0) * 4, (y * image.width + x0 + grid.cellWidthPx) * 4);
    return copy;
}
const check = (id, role, passed, note) => ({ id, role, passed: Boolean(passed), note });

/** The controlled-pixel checks of one case, from the decoded captures of its capture reads (in group order). */
function pixelChecksFor(contract, captures) {
    if (contract.pixelChecks.length === 0) return [];
    const byGroup = Object.fromEntries(cursorQualGroupOffsets(contract).map(({ group, offset }) => [group.id,
        group.reads.flatMap((read, index) => (read.kind === 'capture' ? [{ ...read, capture: captures.get(offset + index) }] : []))]));
    const grid = byGroup.app[0].capture.details.grid;
    const background = rgba(byGroup.app[0].capture.details.theme.backgroundArgb);
    const cursor = [1, 2];
    const cell = capture => cellOf(capture, cursor[0], cursor[1]);
    const roleOf = new Map(contract.pixelChecks.map(([id, role]) => [id, role]));
    const results = [];
    const add = (id, passed, note) => results.push(check(id, roleOf.get(id), passed, note));
    if (contract.caseId === 'cursor.hide') {
        const [control, target, rerender] = byGroup.app.map(item => item.capture);
        const controlCell = cell(control), targetCell = cell(target);
        const ink = inkOf(controlCell, grid.cellWidthPx, grid.cellHeightPx, background);
        add('hide.control-cursor-visible', ink.count >= grid.cellWidthPx * grid.cellHeightPx * 0.5 && !solidBackground(controlCell, background), `${ink.count} cursor pixels in the control cell`);
        add('hide.target-cell-blank', solidBackground(targetCell, background), 'the hidden frame paints the cursor cell as the background');
        add('hide.unrelated-pixels-unchanged', outsideCell(control, ...cursor).equals(outsideCell(target, ...cursor)), 'pixels outside the cursor cell in the control and hidden frames');
        add('hide.rerender-matches-control', control.image.pixels.equals(rerender.image.pixels), 'the re-rendered control scene against the first control');
    } else {
        const [defaultFrame, target, hiddenPhase] = byGroup.app.map(item => item.capture);
        const [block, underline, bar, hidden] = byGroup.shapes.map(item => cell(item.capture));
        const shapes = [cursorShapeOf(block, grid, background), cursorShapeOf(underline, grid, background), cursorShapeOf(bar, grid, background)];
        const distinct = new Set([block, underline, bar].map(bytes => bytes.toString('hex'))).size === 3;
        add('shape.reference-shapes', shapes.join() === 'block,underline,bar' && distinct && solidBackground(hidden, background) && [block, underline, bar].every(bytes => !solidBackground(bytes, background)),
            `steady references paint ${shapes.join(', ')}`);
        const defaultCell = cell(defaultFrame), targetCell = cell(target);
        add('shape.default-visible-and-distinct', !solidBackground(defaultCell, background) && !defaultCell.equals(targetCell), 'the host-default control against the requested shape');
        add('shape.target-is-bar', targetCell.equals(bar) && cursorShapeOf(targetCell, grid, background) === 'bar', `the requested shape paints ${cursorShapeOf(targetCell, grid, background)}`);
        add('shape.target-blink-hidden-phase', solidBackground(cell(hiddenPhase), background) && outsideCell(target, ...cursor).equals(outsideCell(hiddenPhase, ...cursor)),
            'the requested shape in the hidden half-period against the background, with every other pixel as in the visible half-period');
        add('shape.unrelated-pixels-unchanged', outsideCell(defaultFrame, ...cursor).equals(outsideCell(target, ...cursor)), 'pixels outside the cursor cell in the control and requested-shape frames');
    }
    return results;
}

/** The expected geometry-repair evidence of one repaired probe. */
function requireRepairEvidence(contract, recorded, geometry) {
    const { pinned } = recorded;
    const id = contract.caseId, digests = pinnedCallbackDigests[id];
    if (!cursorRepairedProbeIds.includes(id)) {
        if (pinned.original !== undefined || pinned.repair !== undefined || pinned.app.repaired !== false) throw new Error(`Unexpected ${id} geometry repair evidence.`);
        return null;
    }
    const wantedWrites = id === 'cursor.reverse-wrap' ? [] : [...contract.groups.find(group => group.kind === 'pinned-app-repaired').pinnedFeeds];
    const { original, repair, app } = pinned;
    if (app?.repaired !== true || JSON.stringify(app.geometry) !== JSON.stringify(geometry) || JSON.stringify(original?.geometry) !== JSON.stringify(geometry)
        || original.probeId !== id || original.threw?.name !== undeclared(id).name || original.threw.message !== undeclared(id).message
        || JSON.stringify(original.writes) !== JSON.stringify(wantedWrites) || original.pass !== undefined || original.observation !== undefined) {
        throw new Error(`The original ${id} failure was not retained.`);
    }
    const expected = { derivationId: cursorGeometryRepairId, probeId: id, originalKeys: ['id', 'termless', 'term'], repairedKeys: ['id', 'termless', 'term', 'termNeedsGeometry'],
        originalTermNeedsGeometry: null, repairedTermNeedsGeometry: true, sameTermless: true, sameTerm: true, termlessSha256: digests.termlessSha256, termSha256: digests.termSha256,
        metadataChange: 'termNeedsGeometry: true added to the cursor.reverse-wrap probe record; both callbacks are the original function objects' };
    if (JSON.stringify(repair) !== JSON.stringify(expected)) throw new Error(`The ${id} geometry repair record changed.`);
    return { derivationId: cursorGeometryRepairId, original: { threw: original.threw, writes: original.writes }, repair: expected };
}

/** A retained capture is only evidence when the observer's own preconditions still hold: the screen read after the render is the screen read before it, and one render
 * context and default-cursor policy served every capture of the suite. The live observer enforces both; replay must not accept what it would have refused. */
function requireCaptureBracket(frame, capture, baseline, shared) {
    const refuse = () => { throw new Error('Invalid or changed controlled pixel capture.'); };
    if (!frame.afterScreen || frame.afterSha256 !== frame.sha256 || snapshotHash(frame.afterScreen) !== frame.sha256) refuse();
    screenOf(frame.afterScreen);
    if (frame.screen.rows !== baseline.rows || frame.screen.cols !== baseline.cols) refuse();
    const context = stablePixelContext(capture.details), policy = defaultCursorPolicy(frame.screen, capture.details);
    if ((shared.context !== undefined && shared.context !== context) || (shared.policy && policy && shared.policy !== policy)) refuse();
    shared.context ??= context;
    shared.policy ??= policy;
    return capture;
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildCursorQualGradingReport(frames, run, runId, adapter, { contractsVersion = cursorQualContractsVersion, output, imageOf = frame => retainedCursorCapture(output, frame) } = {}) {
    const contracts = validateCursorQualEvidence(frames, run, runId, adapter);
    const baseline = cursorQualView(frames[0].screen);
    let frameIndex = 2;
    const cases = [], shared = {};
    for (const [position, contract] of contracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('Cursor case order differs from the contracts.');
        const readFrames = Array.from({ length: planReads(contract) }, () => frames[frameIndex++]);
        crossCheckCursorCapture(contract, recorded, readFrames);
        const screens = readFrames.map(frame => frame.screen);
        const captures = new Map();
        readFrames.forEach((frame, index) => { if (frame.pixel) captures.set(index, requireCaptureBracket(frame, imageOf(frame), baseline, shared)); });
        const extras = readFrames.map(frame => ({ report: frame.cpr?.reply ?? null }));
        const observation = { ...cursorQualObservation(contract, screens, extras), baseline, pixelChecks: pixelChecksFor(contract, captures) };
        const contractResult = gradeCursorQualCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.termless?.probeId !== contract.caseId || pinned.termless.observation?.evidence !== 'parser-state'
            || pinned.app?.probeId !== contract.caseId || !['query', 'pixels', 'none'].includes(pinned.app.observation?.evidence)) throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        const replayed = replayCursorQualPinned(contract, screens, recorded.reads);
        requirePinnedAgreement(contract, pinned.termless, replayed.termless, 'termless');
        requirePinnedAgreement(contract, pinned.app, replayed.app, 'app');
        if (JSON.stringify(pinned.app.writes) !== JSON.stringify(replayed.app.writes)) throw new Error(`Pinned ${contract.caseId} app writes do not match the replay.`);
        const geometry = { rows: baseline.rows, cols: baseline.cols };
        const repair = requireRepairEvidence(contract, recorded, geometry);
        const divergence = reconcileCursorQual(contract, pinned, contractResult);
        cases.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, appCriterion: contract.appCriterion, pinnedFeeds: contract.terminalFixture.termless,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => ({ kind: read.kind, cursor: read.cursor, ...(read.cpr ? { rawCpr: read.cpr.reply?.raw ?? null } : {}),
                ...(read.pixelSha256 ? { pixelSha256: read.pixelSha256, presentationTimeMs: read.timeMs } : {}) })),
            observed: contract.observed(observation),
            pinned: { pass: pinned.termless.pass, response: pinned.termless.response, note: pinned.termless.note ?? null, observation: pinned.termless.observation, assertions: pinned.termless.assertions ?? null },
            pinnedApp: { repaired: pinned.app.repaired, geometry: pinned.app.geometry, writes: pinned.app.writes, pass: pinned.app.pass, response: pinned.app.response ?? null,
                note: pinned.app.note ?? null, observation: pinned.app.observation, assertions: pinned.app.assertions ?? null },
            ...(repair ? { geometryRepair: { ...repair, geometry, geometryBefore: { rows: screens[0].rows, cols: screens[0].cols }, geometryAfter: { rows: screens.at(-1).rows, cols: screens.at(-1).cols } } } : {}),
            // `divergence` is against the pinned termless probe (the verdict the inventory consumes as the pinned one); the pinned app callback's own
            // divergence is `appDivergence`, and `divergences` lists both.
            contractResult, ...(divergence.find(item => item.against === 'pinned-termless') ? { divergence: divergence.find(item => item.against === 'pinned-termless') } : {}),
            ...(divergence.find(item => item.against === 'pinned-app') ? { appDivergence: divergence.find(item => item.against === 'pinned-app') } : {}),
            ...(divergence.length ? { divergences: divergence } : {}),
            conclusion: { outcome: contractResult.outcome,
                evidence: contract.pixelChecks.length ? 'owned-stored-cursor-and-controlled-pixels' : 'owned-stored-physical-cursor-and-cpr',
                grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(contractResult.layers ? { layers: contractResult.layers } : {}), ...(contractResult.note ? { note: contractResult.note } : {}), scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: cursorQualSuiteId, runId, contractsVersion, adapter,
        scope: 'Pinned terminfo.dev@4.0.0 cursor criteria (cursor.cup-scroll-region, cursor.hide, cursor.shape, cursor.reverse-wrap): the termless callbacks, the app callbacks '
            + '(replayed over retained CPR replies and controlled captures, with the geometry-repaired derivation for cursor.reverse-wrap and cursor.shape and the original '
            + 'UndeclaredTerminalGeometry failures retained), graded from parent-journaled readScreen viewport text, physical cursor, margins, origin and wrap state, stored cursor '
            + 'mode and visibility, raw CPR exchanges and controlled synthetic-phase render captures, with controls for DECOM off/on, region boundaries, restoration, visible and '
            + 'hidden references, steady shape references, blink phase and the mode-off reverse-wrap fixture. ASCII fixtures only; no desktop blink cadence, focus, Unicode, '
            + 'reflow or resize claim; no stock grades.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}

/** Re-execute the LIVE pinned callbacks of the retained run in the derived extension and require them to reproduce the retained results. */
export async function verifyCursorQualLiveReplay(frames, run, runId, adapter, replayModule) {
    const contracts = validateCursorQualEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    for (const [position, contract] of contracts.entries()) {
        const recorded = run.cases[position];
        const readFrames = Array.from({ length: planReads(contract) }, () => frames[frameIndex++]);
        const live = await runCursorQualPinned(contract, readFrames.map(frame => frame.screen), recorded.reads, replayModule);
        for (const path of ['termless', 'app', 'original', 'repair']) {
            if (JSON.stringify(live[path] ?? null) !== JSON.stringify(recorded.pinned[path] ?? null)) throw new Error(`Live pinned ${contract.caseId} ${path} replay does not reproduce the retained result.`);
        }
    }
}
