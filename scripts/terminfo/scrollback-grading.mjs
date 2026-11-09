// Parent-side report builder for the scrolling/history qualification. Re-derives every conclusion from the retained
// parent journal and the independent contract grader, refuses any tampering, and requires the pinned probes' own grading
// to agree: each pinned result is a pure function of the observed states of its own fixture. The contract may never be
// more permissive than a pinned probe that DECIDES; the three pinned probes that are null by construction (set-region,
// alt-screen, decstbm-reset) cannot decide, and the contract's measurement beyond them is recorded as an explicit
// divergence (contract-measures-beyond-pinned) rather than hidden.
import { scrollbackCaseIds, scrollbackContractsVersion, scrollbackNoncePrefix, scrollbackSuiteId, makeScrollbackContracts,
    gradeScrollbackCase, scrollbackObservation, validateScrollbackScreen, pinnedScrollbackNotes } from './scrollback-contracts.mjs';
import { colorScreenOf, validateColorEvidence, buildColorReport, reconcile } from './sgr-color-grading-core.mjs';
import { scrollbackAdapterId } from './scrollback-grading-collector.mjs';
import { scrollbackRecordingContext } from './scrollback-collector.mjs';
import { screenSnapshot } from './agent-observer.mjs';

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
/** The pinned isBlank and size guard. */
const isBlank = char => char === '' || char === ' ';
const validSize = (value, minimum) => Number.isSafeInteger(value) && value >= minimum;
const inconclusive = { pass: false, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none' } };

/** Ports of the pinned termless probe bodies, run over the same recording context the live pinned code receives. */
const pinnedPorts = Object.freeze({
    'scrollback.accumulate'(ctx) {
        const baseline = ctx.getScrollback();
        const rows = baseline.screenLines;
        if (!validSize(rows, 1) || !validSize(ctx.cols, `line ${rows + 9}`.length)) return inconclusive;
        for (let i = 0; i < rows + 10; i++) ctx.feed(`line ${i}\r\n`);
        const scroll = ctx.getScrollback();
        return parserStateResult(scroll.totalLines > baseline.totalLines, 'Written lines increase measured scrollback history', { baseline, scroll });
    },
    'scrollback.total-lines'(ctx) {
        const baseline = ctx.getScrollback();
        const rows = baseline.screenLines;
        if (!validSize(rows, 1) || !validSize(ctx.cols, `line ${rows + 9}`.length)) return inconclusive;
        const lineCount = rows + 10;
        for (let i = 0; i < lineCount; i++) ctx.feed(`line ${i}\r\n`);
        const scroll = ctx.getScrollback();
        return parserStateResult(scroll.totalLines > baseline.totalLines, 'Measured total lines increase after writing beyond the screen', { baseline, lineCount, scroll });
    },
    'scrollback.scroll-up'(ctx) {
        const initial = ctx.getScrollback();
        const rows = initial.screenLines;
        if (!validSize(rows, 5) || !validSize(ctx.cols, 3) || initial.totalLines !== rows) return inconclusive;
        const expected = 'Interior-region SU shifts the measured middle and bottom rows up, clearing the region bottom';
        ctx.feed('\x1B[2J');
        for (const [row, marker] of ['A', 'B', 'C', 'D', 'E'].entries()) ctx.feed(`\x1B[${row + 1};1H${marker}`);
        try {
            ctx.feed('\x1B[2;4r');
            const seed = Array.from({ length: 5 }, (_, row) => ctx.getCell(row, 0).char);
            const beforeScroll = ctx.getScrollback();
            if (seed.join('') !== 'ABCDE' || beforeScroll.totalLines !== rows) {
                return parserStateResult(null, expected, { seed, beforeScroll }, 'Interior-region SU seed or history was not established');
            }
            ctx.feed('\x1B[S');
            const after = Array.from({ length: 5 }, (_, row) => ctx.getCell(row, 0).char);
            const afterScroll = ctx.getScrollback();
            const controlsValid = after[0] === 'A' && after[4] === 'E' && afterScroll.totalLines === rows;
            const shifted = after[1] === 'C' && after[2] === 'D' && isBlank(after[3] ?? '\x00');
            const unchanged = after.join('') === 'ABCDE';
            return parserStateResult(controlsValid ? shifted ? true : unchanged ? false : null : null, expected, { seed, beforeScroll, after, afterScroll },
                controlsValid && (shifted || unchanged) ? undefined : 'Interior-region SU controls or output were not measured');
        } finally {
            ctx.feed('\x1B[r');
        }
    },
    'scrollback.reverse-index'(ctx) {
        ctx.feed('A\r\nB\r\nC');
        const before = ctx.getCell(0, 0);
        ctx.feed('\x1B[H\x1BM');
        const after = ctx.getCell(0, 0);
        return parserStateResult(before.char === 'A' ? isBlank(after.char) : null, 'RI inserts a blank row above the measured A marker', { before, after },
            before.char === 'A' ? undefined : 'A marker was not measured before reverse index');
    },
    'scrollback.scroll-down'(ctx) {
        ctx.feed('LINE1\r\nLINE2\r\nLINE3');
        const before = ctx.getCell(0, 0);
        ctx.feed('\x1B[T');
        const after = ctx.getCell(0, 0);
        return parserStateResult(before.char === 'L' ? isBlank(after.char) : null, 'SD inserts a blank row above the measured LINE marker', { before, after },
            before.char === 'L' ? undefined : 'LINE marker was not measured before scroll down');
    },
    'scrollback.set-region'(ctx) {
        ctx.feed('\x1B[5;10r');
        const cursor = ctx.getCursor();
        ctx.feed('\x1B[r');
        return parserStateResult(null, 'DECSTBM constrains scrolling to the requested region', { cursor }, pinnedScrollbackNotes.region);
    },
    'scrollback.alt-screen'(ctx) {
        ctx.feed('NORMAL');
        ctx.feed('\x1B[?1049h');
        return parserStateResult(null, 'Alt screen preserves normal scrollback', { mode: ctx.getMode('altScreen') }, pinnedScrollbackNotes.alt);
    },
    'scrollback.decstbm'(ctx) {
        const rows = ctx.getScrollback().screenLines;
        if (!validSize(rows, 10) || !validSize(ctx.cols, 9)) return inconclusive;
        ctx.feed('FIXED_TOP\r\n');
        const before = ctx.getCell(0, 0);
        ctx.feed('\x1B[3;10r');
        try {
            ctx.feed('\x1B[3;1HINNER');
            const innerBefore = ctx.getCell(2, 0);
            ctx.feed('\x1B[10;1HZ\r\n');
            const innerAfter = ctx.getCell(2, 0);
            const topAfter = ctx.getCell(0, 0);
            const state = { before, innerBefore, innerAfter, topAfter };
            const expected = 'DECSTBM scrolls the inner marker while preserving FIXED_TOP outside the region';
            if (before.char !== 'F' || innerBefore.char !== 'I' || innerAfter.char === 'I') {
                return parserStateResult(null, expected, state, 'Top marker or inner scrolling control was not established');
            }
            return parserStateResult(topAfter.char === 'F', expected, state);
        } finally {
            ctx.feed('\x1B[r');
        }
    },
    'scrollback.decstbm-reset'(ctx) {
        const baseline = ctx.getScrollback();
        const rows = baseline.screenLines;
        if (!validSize(rows, 10) || !validSize(ctx.cols, `line-${rows + 9}`.length)) return inconclusive;
        ctx.feed('\x1B[5;10r');
        ctx.feed('\x1B[r');
        ctx.feed('\x1B[H');
        for (let i = 0; i < rows + 10; i++) ctx.feed(`line-${i}\r\n`);
        const scroll = ctx.getScrollback();
        return parserStateResult(null, 'Reset DECSTBM permits full-screen scrolling into history', { baseline, scroll }, pinnedScrollbackNotes.reset);
    },
});

/** Re-derive the complete pinned result of one case over the observed states of its own fixture. */
export function replayScrollbackPinned(contract, screens) {
    const context = scrollbackRecordingContext(contract, screens);
    const result = pinnedPorts[contract.caseId](context);
    if (!context.complete) throw new Error(`Pinned ${contract.caseId} port did not replay its complete fixture.`);
    return result;
}

/** Compare the pinned and contract verdicts. A deciding pinned probe follows the shared rule (the contract may be stricter, never
 * more permissive). A pinned probe that is null by construction must be inconclusive with its own note; whatever the contract measures
 * beyond it is recorded as an explicit divergence. */
export function reconcileScrollback(contract, pinnedOutcome, contractResult) {
    if (contract.decides) return reconcile(contract, pinnedOutcome, contractResult);
    if (pinnedOutcome !== 'inconclusive') throw new Error(`Pinned ${contract.caseId} was expected to be inconclusive by construction.`);
    return { pinnedOutcome, contractOutcome: contractResult.outcome, kind: 'contract-measures-beyond-pinned' };
}

/** The geometry-dependent report spec: the contracts follow the measured geometry of the retained start frame. */
function specFor(frames) {
    const start = frames?.[0]?.screen;
    const contracts = makeScrollbackContracts(start?.rows, start?.cols);
    return Object.freeze({ label: 'Scrollback', prefix: 'scrl', noncePrefix: scrollbackNoncePrefix,
        contracts, caseIds: scrollbackCaseIds, suiteId: scrollbackSuiteId, version: scrollbackContractsVersion,
        adapterId: scrollbackAdapterId, derivedFrom: 'ntilde-owned-erase-display-grading-v1',
        screenOf: value => {
            const screen = screenSnapshot(value, true, true, false, true, true, true);
            validateScrollbackScreen(screen);
            return screen;
        },
        pinnedEvidence: ['parser-state', 'none'],
        observation: scrollbackObservation, grade: gradeScrollbackCase, evidence: 'owned-stored-cells-and-actual-scrollback',
        replayPinned: replayScrollbackPinned, reconcile: reconcileScrollback,
        recordingContext: scrollbackRecordingContext, replayFn: 'ntildeRunScrollbackTermless',
        scope: 'Pinned terminfo.dev@4.0.0 scrolling/history termless criteria (scrollback.accumulate, total-lines, scroll-up, reverse-index, scroll-down, '
            + 'set-region, alt-screen, decstbm, decstbm-reset) replayed byte-exactly on a disposable App main screen and graded from parent-journaled readScreen '
            + 'viewport text, actual readScrollback lines and count, margins, active screen, origin/insert/auto-wrap state, cursor and stored attributes, with '
            + 'controls for the empty-history baseline, the pre-overflow state, region-confined scrolling with every row outside the region, wrong direction, '
            + 'no-op scrolling, whole-screen scrolling, alternate-screen isolation and restoration, and unreset-region accumulation. Three pinned probes are null '
            + 'by construction; the contract measures what their notes say is missing and the divergence is recorded. ASCII fixtures only; no Unicode, wrapped-line, '
            + 'reflow, resize or pixel claim; no stock grades.' });
}

/** Validate journal, run identity, binding stability and restoration. */
export const validateScrollbackEvidence = (frames, run, runId, adapter) => validateColorEvidence(specFor(frames), frames, run, runId, adapter);

/** Re-derive and cross-check every case; throw on any disagreement. */
export const buildScrollbackGradingReport = (frames, run, runId, adapter, options) => buildColorReport(specFor(frames), frames, run, runId, adapter, options);
