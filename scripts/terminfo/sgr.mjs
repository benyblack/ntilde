import { collectCells, matchesCells, validateFrames } from './cells.mjs';
import { expandAttributeRuns } from './attributes.mjs';

export const sgrSuiteId = 'ntilde-owned-sgr-indices-v1';
const sgr = codes => `\x1b[${codes}m`;
const attribute = (style = 0, fg = -1, bg = -1) => ({
    flags: style | (fg < 0 ? 0x0100 : 0x4000) | (bg < 0 ? 0x0200 : 0x8000), fg, bg,
});
const defaultAttribute = attribute();
const view = (first, attributes) => ({ first, row: 0, col: first.length, attributes });
const fixture = (id, steps) => ({ id, seed: '', cursor: [0, 0], steps });
const styles = [['bold', 1, 22, 1], ['italic', 3, 23, 4], ['underline', 4, 24, 8], ['inverse', 7, 27, 2]];
const combined = attribute(15, 1, 4);
const colored = sgr('1;3;4;7;31;44') + 'A';

function colors(channel, family, indices) {
    const attributes = indices.map(index => channel === 'fg' ? attribute(0, index) : attribute(0, -1, index));
    const text = indices.map((_, index) => String.fromCharCode(65 + index)).join('');
    const sequence = indices.map((index, position) => {
        const base = channel === 'fg' ? 30 : 40;
        let code;
        if (family === '256') code = `${base + 8};5;${index}`;
        else code = String(base + index + (family === 'bright' ? 52 : 0));
        return sgr(code) + text[position];
    }).join('');
    return fixture(`sgr.${channel}.${family}`, [[sequence, view(text, attributes)]]);
}

export const sgrCases = Object.freeze([
    ...styles.map(([name, on, , flag]) => fixture(`sgr.${name}`, [[sgr(on) + 'A', view('A', [attribute(flag)])]])),
    ...styles.map(([name, , off, flag]) => fixture(`sgr.selective-reset.${name}`, [
        [colored, view('A', [combined])], [sgr(off) + 'B', view('AB', [combined, attribute(15 & ~flag, 1, 4)])],
    ])),
    fixture('sgr.reset', [[colored, view('A', [combined])], [sgr(0) + 'B', view('AB', [combined, defaultAttribute])]]),
    ...['fg', 'bg'].flatMap(channel => [
        colors(channel, 'standard', [0, 1, 2, 3, 4, 5, 6, 7]),
        colors(channel, 'bright', [8, 9, 10, 11, 12, 13, 14, 15]),
        colors(channel, '256', [0, 1, 15, 16, 231, 232, 255]),
    ]),
    fixture('sgr.fg.default', [[colored, view('A', [combined])], [sgr(39) + 'B', view('AB', [combined, attribute(15, -1, 4)])]]),
    fixture('sgr.bg.default', [[colored, view('A', [combined])], [sgr(49) + 'B', view('AB', [combined, attribute(15, 1, -1)])]]),
]);

export function matchesSgr(screen, expected) {
    if (!matchesCells(screen, expected)) return false;
    const actual = expandAttributeRuns(screen.attributeRuns, screen.rows * screen.cols);
    return actual.every((cell, index) => {
        const wanted = expected.attributes[index] ?? defaultAttribute;
        return cell.flags === wanted.flags && cell.fg === wanted.fg && cell.bg === wanted.bg;
    });
}

export function collectSgr(options) {
    return collectCells({ ...options, config: { ...options.config, cellNonce: `NTILDE_SG_${options.config.runId}` },
        fixtures: sgrCases, prefix: 'sgr' });
}

export function buildSgrReport(snapshots, runId) {
    validateFrames(snapshots, runId, { fixtures: sgrCases, prefix: 'sgr', noncePrefix: 'NTILDE_SG_', includeAttributes: true });
    let offset = 2;
    const observations = sgrCases.map(item => {
        const before = snapshots[offset++];
        if (before.kind !== 'sgr-read') throw new Error('Missing SGR control.');
        const assertions = item.steps.map(([sequence, expected]) => {
            const target = snapshots[offset++];
            if (target.kind !== 'sgr-read') throw new Error('Invalid SGR experiment order.');
            return { sequence, expected, snapshot: target.index, passed: matchesSgr(target.screen, expected) };
        });
        const ready = matchesSgr(before.screen, view('', []));
        const qualified = assertions.every(item => item.passed) ? 'passed' : 'failed';
        return { featureId: item.id, before: before.index, assertions, result: ready ? qualified : 'inconclusive' };
    });
    return { schemaVersion: 1, suiteId: sgrSuiteId, runId, complete: true,
        scope: 'Live App ASCII cell flags and palette indices; lossless row-major attribute runs. No RGB, underline metadata, pixels or stock grades.',
        initialSnapshot: 0, restoredSnapshot: snapshots.length - 1, snapshots, observations };
}
