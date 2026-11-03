// Supplemental writes only to the proven disposable PTY. Runtime colors are session-wide.
export const colorSuiteId = 'ntilde-owned-runtime-colors-v1';
export const clearColors = '\x1b]104\x1b\\\x1b]110\x1b\\\x1b]111\x1b\\\x1b]112\x1b\\\x1b]117\x1b\\\x1b]119\x1b\\';
const at = (row, col) => `\x1b[${row + 1};${col + 1}H`;
const reset = '\x1b[!p\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H\x1b[?25l';
export const colorSelection = Object.freeze({ startRow: 2, startCol: 4, endRow: 4, endCol: 10 });
export const overrideBlue = 0xff175bad, overrideGreen = 0xff20e060;
const osc = (code, value = '') => `\x1b]${code}${value}\x1b\\`;
const set = (code, index, rgb = '#175bad') => osc(code, `;${index === undefined ? '' : index + ';'}${rgb}`);
function input(index, cursor) {
    const target = index === undefined ? '\x1b[39;48;2;16;16;16mHMWqg_|' : `\x1b[48;5;${index}m       `;
    const blank = index === undefined ? '\x1b[0m       ' : `\x1b[38;5;${index};48;2;16;16;16m███████`;
    return clearColors + reset + at(2, 4) + target + at(4, 4) + blank
        + at(6, 4) + '\x1b[38;2;240;240;240;48;2;16;16;16mHMWqg_|\x1b[0m' + at(8, 8)
        + (cursor ? '\x1b[2 q\x1b[?25h' : '');
}
const experiment = (id, queries, override, resetSequence, selection = null, index, cursor = false) => ({ id, queries, selection,
    stages: [{ id: 'baseline', sequence: input(index, cursor) }, { id: 'override', sequence: override }, { id: 'reset', sequence: resetSequence }] });
export const colorCases = Object.freeze([
    experiment('palette.single-base', [{ code: 4, index: 1 }], set(4, 1), osc(104, ';1'), null, 1),
    experiment('palette.single-extended', [{ code: 4, index: 200 }], set(4, 200), osc(104, ';200'), null, 200),
    experiment('palette.all', [{ code: 4, index: 1 }, { code: 4, index: 200 }], set(4, 1) + set(4, 200, '#20e060'), osc(104), null, 1),
    ...[[10, 110, 'foreground'], [11, 111, 'background'], [12, 112, 'cursor'], [17, 117, 'selection-background'], [19, 119, 'selection-foreground']]
        .map(([code, resetCode, id]) => experiment(id, [{ code }], set(code), osc(resetCode), code >= 17 ? colorSelection : null, undefined, code === 12)),
    ...[117, 119].map(resetCode => ({ id: `selection.independent-${resetCode}`, queries: [{ code: 17 }, { code: 19 }], selection: colorSelection,
        stages: [{ id: 'baseline', sequence: input() }, { id: 'override', sequence: set(17) + set(19, undefined, '#20e060') },
            { id: 'partial-reset', sequence: osc(resetCode) }, { id: 'reset', sequence: osc(resetCode === 117 ? 119 : 117) }] })),
]);
export const colorPlan = Object.freeze(colorCases.flatMap(item => item.stages.map(stage => ({ caseId: item.id, stageId: stage.id,
    selection: item.selection, queries: item.queries }))));
export async function collectColors({ config, fence, owner }) {
    let initial;
    try { initial = await owner('color-start'); }
    catch (error) { if (error.code === 'screenDetailsUnavailable') return { status: 'unavailable', reason: error.code }; throw error; }
    const { pristineCursorBaseline } = await import('./cursor.mjs');
    if (initial.screen.rows < 10 || !pristineCursorBaseline(initial.screen)) throw new Error('Colors require a pristine owned baseline.');
    try {
        await owner('color-bind', await fence.write('\x1b[?1049h' + reset + `NTILDE_CL_${config.runId}`));
        for (const item of colorCases) for (const stage of item.stages) {
            const barrier = await fence.write(stage.sequence);
            const queries = [];
            for (const spec of item.queries) queries.push({ spec, ...await fence.query(spec) });
            await owner('color-read', barrier, { caseId: item.id, stageId: stage.id, queries });
        }
    } finally {
        await owner('color-finish', await fence.write(clearColors + '\x1b[?1049l\x1b[!p'));
    }
}
