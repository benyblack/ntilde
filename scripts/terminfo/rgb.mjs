import { collectCells, matchesCells, validateFrames } from './cells.mjs';
import { expandCellDetails } from './cell-details.mjs';

export const rgbSuiteId = 'ntilde-owned-rgb-underline-v1';
const sgr = codes => `\x1b[${codes}m`;
const normal = () => ({ flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
    underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null });
const detail = changes => ({ ...normal(), ...changes });
const view = (first, details) => ({ first, row: 0, col: first.length, details });
const fixture = (id, steps) => ({ id, seed: '', cursor: [0, 0], steps });
const literal = (channel, argb) => detail({ flags: channel === 'fg' ? 512 : 256,
    [channel === 'fg' ? 'foregroundArgb' : 'backgroundArgb']: argb });
const underline = (style, changes = {}) => detail({ flags: 776, underlineStyle: style, ...changes });
const combined = detail({ flags: 8, foregroundArgb: 0xff0180ff, backgroundArgb: 0xfffe0200,
    underlineStyle: 3, underlineArgb: 0xff0c2238 });
const combinedSequence = sgr('38;2;1;128;255;48;2;254;2;0;4:3;58;2;12;34;56') + 'A';

function colors(channel, syntax) {
    const samples = [[1, 128, 255], [254, 2, 0], [0, 0, 0], [255, 255, 255]];
    const prefix = channel === 'fg' ? 38 : 48;
    const sequence = samples.map((rgb, index) => {
        const code = syntax === 'semicolon' ? `${prefix};2;${rgb.join(';')}`
            : `${prefix}:2:${syntax === 'colon-colorspace' ? ':' : ''}${rgb.join(':')}`;
        return sgr(code) + String.fromCodePoint(65 + index);
    }).join('');
    const details = samples.map(([r, g, b]) => literal(channel, 0xff000000 + r * 65536 + g * 256 + b));
    return fixture(`rgb.${channel}.${syntax}`, [[sequence, view('ABCD', details)]]);
}

export const rgbCases = Object.freeze([
    ...['fg', 'bg'].flatMap(channel => ['semicolon', 'colon', 'colon-colorspace'].map(syntax => colors(channel, syntax))),
    ...[['fg', 39, 264, 'foregroundArgb'], ['bg', 49, 520, 'backgroundArgb']].map(([channel, reset, flags, field]) =>
        fixture(`rgb.${channel}.reset`, [[combinedSequence, view('A', [combined])],
            [sgr(reset) + 'B', view('AB', [combined, { ...combined, flags, [field]: null }])]])),
    ...[1, 2, 3, 4, 5].map(style => fixture(`underline.style.${style}`, [[sgr(`4:${style}`) + 'A', view('A', [underline(style)])]])),
    fixture('underline.double.sgr21', [[sgr(21) + 'A', view('A', [underline(2)])]]),
    fixture('underline.color.rgb-semicolon', [[sgr('4;58;2;12;34;56') + 'A', view('A', [underline(1, { underlineArgb: 0xff0c2238 })])]]),
    fixture('underline.color.rgb-colon', [[sgr('4:3;58:2::12:34:56') + 'A', view('A', [underline(3, { underlineArgb: 0xff0c2238 })])]]),
    fixture('underline.color.indexed', [[sgr('4:5;58;5;123') + 'A', view('A', [underline(5, { underlineColorIndex: 123 })])]]),
    fixture('underline.color.reset', [[combinedSequence, view('A', [combined])],
        [sgr(59) + 'B', view('AB', [combined, { ...combined, underlineArgb: null }])]]),
    fixture('underline.disable', [[combinedSequence, view('A', [combined])],
        [sgr(24) + 'B', view('AB', [combined, { ...combined, flags: 0, underlineStyle: 0, underlineArgb: null }])]]),
    fixture('rgb.underline.reset', [[combinedSequence, view('A', [combined])], [sgr(0) + 'B', view('AB', [combined, normal()])]]),
]);

export function matchesRgb(screen, expected) {
    if (!matchesCells(screen, expected)) return false;
    const actual = expandCellDetails(screen.cellDetails, screen.rows * screen.cols);
    return actual.every((cell, index) => {
        const wanted = expected.details[index] ?? normal();
        return Object.keys(wanted).every(key => cell[key] === wanted[key]);
    });
}

export function collectRgb(options) {
    return collectCells({ ...options, config: { ...options.config, cellNonce: `NTILDE_RG_${options.config.runId}` },
        fixtures: rgbCases, prefix: 'rgb' });
}

export function buildRgbReport(snapshots, runId) {
    validateFrames(snapshots, runId, { fixtures: rgbCases, prefix: 'rgb', noncePrefix: 'NTILDE_RG_', includeCellDetails: true });
    let offset = 2;
    const observations = rgbCases.map(item => {
        const before = snapshots[offset++];
        if (before.kind !== 'rgb-read') throw new Error('Missing RGB/underline control.');
        const assertions = item.steps.map(([sequence, expected]) => {
            const target = snapshots[offset++];
            if (target.kind !== 'rgb-read') throw new Error('Invalid RGB/underline experiment order.');
            return { sequence, expected, snapshot: target.index, passed: matchesRgb(target.screen, expected) };
        });
        const ready = matchesRgb(before.screen, view('', []));
        return { featureId: item.id, before: before.index, assertions,
            result: ready ? (assertions.every(value => value.passed) ? 'passed' : 'failed') : 'inconclusive' };
    });
    return { schemaVersion: 1, suiteId: rgbSuiteId, runId, complete: true,
        scope: 'Live App stored ARGB, default/palette markers and underline metadata; complete physical-cell runs. No resolved theme colors, pixels or stock grades.',
        initialSnapshot: 0, restoredSnapshot: snapshots.length - 1, snapshots, observations };
}
