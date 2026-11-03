import { collectCells, validateFrames } from './cells.mjs';
import { matchesRgb } from './rgb.mjs';

export const renditionSuiteId = 'ntilde-owned-rendition-charsets-v3';
const previousSuiteId = 'ntilde-owned-rendition-charsets-v2';
const legacySuiteId = 'ntilde-owned-rendition-charsets-v1';
const normal = changes => ({ flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
    underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null, ...changes });
const view = (first, col, details = []) => ({ first, row: 0, col, details });
const fixture = (id, steps) => ({ id, seed: '', cursor: [0, 0], steps });
const matches = (screen, wanted, overline) => screen.cursorVisible === true && matchesRgb(screen, wanted, overline);
const styles = [['faint', 2, 22, 64], ['hidden', 8, 28, 128], ['strikethrough', 9, 29, 16], ['blink', 5, 25, 32]];
const combined = normal({ flags: 255, foregroundArgb: 0xff112233, backgroundArgb: 0xff445566,
    underlineStyle: 3, underlineArgb: 0xff778899 });
const combinedWrite = '\x1b[1;2;3;4:3;5;7;8;9;38;2;17;34;51;48;2;68;85;102;58;2;119;136;153mA';

const previousCases = Object.freeze([
    ...styles.map(([name, on, , flag]) => fixture(`sgr.${name}`, [[`\x1b[${on}mA`, view('A', 1, [normal({ flags: 768 | flag })])]])),
    ...styles.map(([name, , off, flag]) => fixture(`sgr.selective-reset.${name}`, [
        [combinedWrite, view('A', 1, [combined])],
        [`\x1b[${off}mB`, view('AB', 2, [combined, { ...combined, flags: 255 & ~(flag | (off === 22 ? 1 : 0)) }])],
    ])),
    fixture('sgr.remaining.reset', [[combinedWrite, view('A', 1, [combined])], ['\x1b[0mB', view('AB', 2, [combined, normal()])]]),
    fixture('charsets.utf8.bmp', [['éΩ', view('éΩ', 2)]]),
    fixture('charsets.utf8.combining', [['e\u0301', view('e\u0301', 1, [normal({ flags: 8960 })])]]),
    fixture('charsets.utf8.wide', [['中', view('中', 2, [normal({ flags: 2816 }), normal({ flags: 4864 })])]]),
    fixture('charsets.utf8.supplementary', [['😀', view('😀', 2, [normal({ flags: 11008 }), normal({ flags: 4864 })])]]),
    fixture('charsets.dec-line-drawing', [['\x1b(0lqk\x1b(Bqx', view('┌─┐qx', 5)]]),
    fixture('charsets.dec-special', [['\x1b(0ABfg\x1b(Bqx', view('AB°±qx', 6)]]),
    fixture('charsets.g0-g1-switching', [['\x1b(B\x1b)0A\x0eqx\x0fB\x1b)B\x0eqx\x0f', view('A─│Bqx', 6)]]),
]);

/** Preserve the first retained mapping measurement without inventing later ASCII probes. */
const legacyCases = previousCases.map(item => {
    if (item.id === 'charsets.dec-line-drawing') return fixture(item.id, [['\x1b(0lqk\x1b(B', view('┌─┐', 3)]]);
    if (item.id === 'charsets.dec-special') return fixture(item.id, [['\x1b(0ABfg\x1b(B', view('AB°±', 4)]]);
    if (item.id === 'charsets.g0-g1-switching') return fixture(item.id, [['\x1b(B\x1b)0A\x0eqx\x0fB\x1b)B', view('A─│B', 4)]]);
    return item;
});

export const renditionCases = Object.freeze([...previousCases,
    fixture('sgr.overline', [['\x1b[53mA', view('A', 1, [normal({ overline: true })])],
        ['\x1b[55mB', view('AB', 2, [normal({ overline: true }), normal()])]]),
    fixture('sgr.overline.underline-independent', [['\x1b[53;4:3;58;5;9mA', view('A', 1, [normal({ flags: 776, underlineStyle: 3, underlineColorIndex: 9, overline: true })])],
        ['\x1b[24mB', view('AB', 2, [normal({ flags: 776, underlineStyle: 3, underlineColorIndex: 9, overline: true }), normal({ overline: true })])],
        ['\x1b[4:2mC\x1b[55mD', view('ABCD', 4, [normal({ flags: 776, underlineStyle: 3, underlineColorIndex: 9, overline: true }), normal({ overline: true }),
            normal({ flags: 776, underlineStyle: 2, underlineColorIndex: 9, overline: true }), normal({ flags: 776, underlineStyle: 2, underlineColorIndex: 9 })])]]),
    fixture('sgr.overline.reset', [['\x1b[53mA', view('A', 1, [normal({ overline: true })])],
        ['\x1b[0mB', view('AB', 2, [normal({ overline: true }), normal()])]]),
]);

function casesFor(suiteId) {
    if (suiteId === renditionSuiteId) return renditionCases;
    if (suiteId === previousSuiteId) return previousCases;
    if (suiteId === legacySuiteId) return legacyCases;
    throw new Error('Unknown rendition evidence revision.');
}

/** Bind a separate owned alternate screen; every charset case restores ASCII designation/shift. */
export async function collectRendition(options) {
    const fixtures = casesFor(options.suiteId ?? renditionSuiteId);
    try { await options.owner('rendition-start'); }
    catch (error) {
        if (error.code === 'screenDetailsUnavailable') return { status: 'unavailable', reason: error.code };
        throw error;
    }
    return collectCells({ ...options, config: { ...options.config, cellNonce: `NTILDE_RD_${options.config.runId}` },
        fixtures, prefix: 'rendition', started: true });
}

/** Derive stored state; painted appearance and blink timing remain separate. */
export function buildRenditionReport(snapshots, runId, suiteId = renditionSuiteId) {
    const fixtures = casesFor(suiteId);
    const overline = suiteId === renditionSuiteId;
    validateFrames(snapshots, runId, { fixtures, prefix: 'rendition', noncePrefix: 'NTILDE_RD_', includeCellDetails: true, includeOverline: overline });
    let offset = 2;
    const observations = fixtures.map(item => {
        const before = snapshots[offset++];
        if (before.kind !== 'rendition-read') throw new Error('Missing rendition control.');
        const assertions = item.steps.map(([sequence, expected]) => {
            const target = snapshots[offset++];
            if (target.kind !== 'rendition-read') throw new Error('Invalid rendition experiment order.');
            return { sequence, expected, snapshot: target.index, passed: matches(target.screen, expected, overline) };
        });
        const ready = matches(before.screen, view('', 0), overline);
        const qualified = assertions.every(value => value.passed) ? 'passed' : 'failed';
        return { featureId: item.id, before: before.index, assertions, result: ready ? qualified : 'inconclusive' };
    });
    return { schemaVersion: 1, suiteId, runId, complete: true,
        scope: overline ? 'Stored rendition flags, overline, and decoded Unicode/DEC charset cells. No pixels, blink timing or stock grades.'
            : 'Stored faint/hidden/strikethrough/blink flags and decoded Unicode/DEC charset cells. No overline, pixels, blink timing or stock grades.',
        initialSnapshot: 0, restoredSnapshot: snapshots.length - 1, snapshots, observations };
}
