// Independent ASCII hard-line fixtures. Unwritten row tails are never seeded as authored spaces.
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const reset = '\x1b[?1049l\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[3J\x1b[H';
const cell = (text = ' ') => ({ text, overline: false, flags: 768, fg: -1, bg: -1,
    foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null });

/** Lay out known hard lines independently; all meaningful content is at or before the cursor. */
function expected(lines, cursorLine, cursorOffset, rows, cols, anchoredHistory) {
    const physical = lines.flatMap(line => Array.from({ length: Math.max(1, Math.ceil(line.length / cols)) },
        (_, index) => line.slice(index * cols, (index + 1) * cols)));
    const prior = lines.slice(0, cursorLine).reduce((count, line) => count + Math.max(1, Math.ceil(line.length / cols)), 0);
    const historyCount = anchoredHistory ?? Math.max(0, physical.length - rows), history = physical.slice(0, historyCount);
    const viewport = physical.slice(historyCount);
    while (viewport.length < rows) viewport.push('');
    return { grid: viewport.map(line => Array.from({ length: cols }, (_, col) => cell(line[col] ?? ' '))),
        row: prior + Math.floor(cursorOffset / cols) - historyCount, col: cursorOffset % cols,
        top: 0, bottom: rows - 1, alternate: false, originMode: false, history };
}

/** Map the source-known first visible hard-line offset into a restored width, preserving Ntilde's top anchor. */
function restoredHistoryAnchor(lines, smallerCols, smallerHistory, restoredCols) {
    let remaining = smallerHistory, restoredHistory = 0;
    for (const line of lines) {
        const smallerRows = Math.max(1, Math.ceil(line.length / smallerCols));
        if (remaining < smallerRows) return restoredHistory + Math.floor(remaining * smallerCols / restoredCols);
        remaining -= smallerRows;
        restoredHistory += Math.max(1, Math.ceil(line.length / restoredCols));
    }
    throw new Error('Resize anchor exceeded the known hard-line plan.');
}

/** Three explicit shrink/restore plans; complete history and cell details must fit their read budgets. */
export function makeResizeCases(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < 12 || rows > 52 || !Number.isSafeInteger(cols)
        || cols < 55 || cols > 256 || rows * cols > 8192) throw Object.assign(new Error('Unsupported bounded resize grid.'), { code: 'resizeGeometryUnavailable' });
    const short = Array.from({ length: rows }, (_, index) => `RROW${String(index).padStart(3, '0')}-ABCDEFGH`);
    const long = index => `LONG${String(index).padStart(3, '0')}-`
        + 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.repeat(Math.ceil(cols / 26)).slice(0, cols - 10);
    const specs = [
        { id: 'resize.width-roundtrip', lines: [long(0)], cursorLine: 0, cols: cols - 7, rows },
        { id: 'resize.height-history-roundtrip', lines: short, cursorLine: rows - 1, cols, rows: rows - 4 },
        { id: 'resize.combined-reflow-history-roundtrip', lines: Array.from({ length: rows }, (_, index) => long(index)),
            cursorLine: rows - 1, cols: cols - 7, rows: rows - 4 },
    ];
    return specs.map(spec => {
        const offset = spec.lines[spec.cursorLine].length;
        const before = expected(spec.lines, spec.cursorLine, offset, rows, cols);
        const after = expected(spec.lines, spec.cursorLine, offset, spec.rows, spec.cols);
        if (after.history.length > 64) throw Object.assign(new Error('Resize plan exceeds the complete history budget.'), { code: 'resizeGeometryUnavailable' });
        const setup = reset + spec.lines.map((line, index) => at(index, 0) + line).join('') + at(spec.cursorLine, offset);
        const anchor = restoredHistoryAnchor(spec.lines, spec.cols, after.history.length, cols);
        const restored = expected(spec.lines, spec.cursorLine, offset, rows, cols, anchor);
        return { id: spec.id, setup, target: { rows: spec.rows, cols: spec.cols }, before, after, restored };
    });
}
export const resizeSuiteId = 'ntilde-owned-resize-reflow-v1';
export const ownedResizePolicy = 'parent-selected App resize in proved private pane; independent kernel geometry and -opost; original grid restored before stock';

const finalRestore = reset + '\x1b[?25h';
/** Execute only the fixed private plan; a failed collection restores geometry/output without recording completion. */
export async function collectResize({ config, fence, owner }) {
    let initial;
    try { initial = await owner('resize-start'); }
    catch (error) {
        if (['screenDetailsUnavailable', 'resizeGeometryUnavailable'].includes(error.code)) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const { pristineEditing } = await import('./editing-capture.mjs');
    if (!pristineEditing(initial.screen)) throw new Error('Resize fixture requires a pristine private main screen.');
    const cases = makeResizeCases(initial.screen.rows, initial.screen.cols);
    let bound = false;
    try {
        await owner('resize-bind', await fence.write('\x1b[?1049h\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[HNTILDE_RZ_' + config.runId));
        bound = true;
        await cases.reduce((previous, item) => previous.then(async () => {
            await owner('resize-read', await fence.write(item.setup), { caseId: item.id, stageId: 'control' });
            await ['after', 'restored'].reduce((prior, stageId) => prior.then(async () => {
                await owner('resize-apply', null, { caseId: item.id, stageId });
                // The parent chose and verified the geometry. Fence later output before reading its state.
                await owner('resize-read', await fence.write(''), { caseId: item.id, stageId });
            }), Promise.resolve());
        }), Promise.resolve());
        await owner('resize-finish', await fence.write(finalRestore));
    } catch (error) {
        return restoreAfterFailure(error, bound, owner, fence);
    }
}

/** Attempt geometry and output cleanup independently, preserving every failure. */
async function restoreAfterFailure(error, bound, owner, fence) {
    const failures = [error];
    if (bound) {
        try { await owner('resize-abort'); }
        catch (error_) { failures.push(error_); }
    }
    try {
        if (fence.failed) await fence.writeCleanup(finalRestore);
        else await fence.write(finalRestore);
    } catch (error_) { failures.push(error_); }
    if (failures.length > 1) throw new AggregateError(failures, 'Resize collection and cleanup failed.');
    throw error;
}
