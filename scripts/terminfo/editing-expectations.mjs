export function compressExpected(state) {
    const runs = [];
    for (const { text, ...values } of state.grid.flat()) {
        const key = JSON.stringify(values), last = runs.at(-1);
        if (last && key === JSON.stringify(last.values)) last.count++;
        else runs.push({ count: 1, values });
    }
    const history = state.history ?? [];
    return { lines: state.grid.map(row => row.map(value => value.text).join('').trimEnd()),
        cursorRow: state.row, cursorCol: state.col, cursorVisible: true,
        bufferState: { scrollTop: state.top, scrollBottom: state.bottom, originMode: state.originMode ?? false, autoWrap: true,
            insertMode: false, pendingWrap: false, alternateScreen: state.alternate, scrollbackLines: history.length },
        history: { startLine: 0, totalLines: history.length, lines: [...history] }, expectedAttributeRuns: runs };
}
function matchesFlags(flags, mask, wanted) {
    if (mask === undefined) return true;
    const maskedFlags = Number(flags) & Number(mask);
    return maskedFlags === wanted;
}
export function matchesExpected(actual, expected, attributes) {
    for (const key of ['lines', 'cursorRow', 'cursorCol', 'cursorVisible', 'bufferState', 'history']) {
        if (JSON.stringify(actual[key]) !== JSON.stringify(expected[key])) return false;
    }
    let index = 0;
    for (const run of expected.expectedAttributeRuns) {
        const { flagsMask, flagsValue, ...values } = run.values;
        for (let count = 0; count < run.count; count++) {
            const cell = attributes[index++];
            if (!cell || !Object.keys(values).every(key => cell[key] === values[key])
                || !matchesFlags(cell.flags, flagsMask, flagsValue)) return false;
        }
    }
    return index === attributes.length;
}
