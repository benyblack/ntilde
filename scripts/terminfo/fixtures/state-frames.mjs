import { snapshotHash } from '../agent-observer.mjs';

export const runId = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
export const normal = [768, -1, -1, null, null, 0, -1, null];

/** Independent fixture values are supplied by tests, never an emulator or the suite generator. */
export function stateScreen(first = '', details = [], { col = first.length, control = 'KEEP!', visible = true, mode, includeOverline = false } = {}) {
    const runs = [];
    for (let index = 0; index < 180; index++) {
        const [flags, fg, bg, foregroundArgb, backgroundArgb, underlineStyle, underlineColorIndex, underlineArgb, overline = false] = details[index] ?? normal;
        const value = { flags, fg, bg, foregroundArgb, backgroundArgb, underlineStyle, underlineColorIndex, underlineArgb, ...(includeOverline ? { overline } : {}) };
        const last = runs.at(-1);
        if (last && Object.keys(value).every(key => value[key] === last[key])) last.count++;
        else runs.push({ count: 1, ...value });
    }
    return { rows: 3, cols: 60, lines: [first, control, ''], cursorRow: 0, cursorCol: col,
        cursorVisible: visible, cellDetails: runs, ...(mode ? { cursorMode: { ...mode } } : {}) };
}

export function stateFrames(prefix, nonce, initial, samples, control, binding) {
    const values = [initial, binding(nonce + runId), ...samples.flatMap(([, steps]) => [control(),
        ...steps.map(([, screen]) => screen)]), structuredClone(initial)];
    /** Keep lifecycle ordering explicit in independent test frames. */
    const kind = index => {
        if (index === 0) return prefix + '-start';
        if (index === 1) return prefix + '-bind';
        if (index === values.length - 1) return prefix + '-finish';
        return prefix + '-read';
    };
    return structuredClone(values).map((screen, index) => ({ index, paneId, screen, sha256: snapshotHash(screen),
        kind: kind(index), barrier: index === 0 ? null : '\x1b[?62;4;22c' }));
}

export function rehash(frames) { for (const frame of frames) frame.sha256 = snapshotHash(frame.screen); }
