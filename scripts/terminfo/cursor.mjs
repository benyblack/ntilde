import { collectCells, validateFrames } from './cells.mjs';
import { matchesRgb } from './rgb.mjs';
import { expandCellDetails } from './cell-details.mjs';

export const cursorSuiteId = 'ntilde-owned-cursor-modes-v1';
const mode = (style = 0, blink = false, isDefault = false) => ({ style, blink, isDefault });
const expected = (cursorMode = mode(), visible = true) => ({ first: 'CURSOR', row: 0, col: 2, details: [], cursorMode, visible });
const fixture = (id, steps) => ({ id, seed: 'CURSOR', cursor: [0, 2], setup: '\x1b[2 q\x1b[?25h', steps });

export const cursorCases = Object.freeze([
    fixture('cursor.visibility', [['\x1b[?25l', expected(mode(), false)], ['\x1b[?25h', expected()]]),
    ...[[0, 0, true], [1, 0, true], [2, 0, false], [3, 2, true], [4, 2, false], [5, 1, true], [6, 1, false]]
        .map(([parameter, style, blink]) => fixture(`cursor.style.${parameter}`, [[`\x1b[${parameter} q`, expected(mode(style, blink))]])),
    fixture('cursor.style.invalid', [['\x1b[7 q', expected()]]),
]);

/** DECSTR cleanup is allowed only for a pristine private main screen with stored default modes. */
export function pristineCursorBaseline(screen) {
    if (!screen.lines.every(line => line === '') || screen.cursorRow !== 0 || screen.cursorCol !== 0 || !screen.cursorVisible
        || screen.cursorMode?.style !== 2 || screen.cursorMode.blink !== true || screen.cursorMode.isDefault !== true) return false;
    return expandCellDetails(screen.cellDetails, screen.rows * screen.cols).every(cell => cell.flags === 768
        && cell.fg === -1 && cell.bg === -1 && cell.foregroundArgb === null && cell.backgroundArgb === null
        && cell.underlineStyle === 0 && cell.underlineColorIndex === -1 && cell.underlineArgb === null);
}

/** Verify a pristine baseline before writes, then restore defaults with DECSTR inside the owned pane. */
export async function collectCursor(options) {
    let initial;
    try { initial = await options.owner('cursor-start'); }
    catch (error) {
        if (error.code === 'screenDetailsUnavailable') return { status: 'unavailable', reason: error.code };
        throw error;
    }
    if (!pristineCursorBaseline(initial.screen)) throw new Error('Cursor fixture requires a pristine owned baseline.');
    return collectCells({ ...options, config: { ...options.config, cellNonce: `NTILDE_CS_${options.config.runId}` },
        fixtures: cursorCases, prefix: 'cursor', started: true, restore: '\x1b[?1049l\x1b[!p' });
}

/** Compare stored cursor modes plus complete cells/text, without claiming rendered blink/shape. */
export function matchesCursor(screen, wanted) {
    return matchesRgb(screen, wanted) && screen.cursorVisible === wanted.visible
        && Object.keys(wanted.cursorMode).every(key => screen.cursorMode?.[key] === wanted.cursorMode[key]);
}

/** Retain controlled mode observations and exact pristine-baseline restoration. */
export function buildCursorReport(snapshots, runId) {
    validateFrames(snapshots, runId, { fixtures: cursorCases, prefix: 'cursor', noncePrefix: 'NTILDE_CS_',
        includeCellDetails: true, includeCursorMode: true });
    if (!pristineCursorBaseline(snapshots[0].screen)) throw new Error('Cursor report has no pristine reset baseline.');
    let offset = 2;
    const observations = cursorCases.map(item => {
        const before = snapshots[offset++];
        if (before.kind !== 'cursor-read') throw new Error('Missing cursor control.');
        const assertions = item.steps.map(([sequence, expected]) => {
            const target = snapshots[offset++];
            if (target.kind !== 'cursor-read') throw new Error('Invalid cursor experiment order.');
            return { sequence, expected, snapshot: target.index, passed: matchesCursor(target.screen, expected) };
        });
        const ready = matchesCursor(before.screen, expected());
        const qualified = assertions.every(value => value.passed) ? 'passed' : 'failed';
        return { featureId: item.id, before: before.index, assertions, result: ready ? qualified : 'inconclusive' };
    });
    return { schemaVersion: 1, suiteId: cursorSuiteId, runId, complete: true,
        scope: 'Stored cursor visibility/style/blink/default modes in a pristine owned fixture; no resolved host appearance, pixels, blink phase, scrollback or stock grades.',
        initialSnapshot: 0, restoredSnapshot: snapshots.length - 1, snapshots, observations };
}
