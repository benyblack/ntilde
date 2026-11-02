import { collectCells, validateFrames } from './cells.mjs';
import { matchesRgb } from './rgb.mjs';

export const renditionSuiteId = 'ntilde-owned-rendition-charsets-v1';
const normal = changes => ({ flags: 768, fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null,
    underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null, ...changes });
const view = (first, col, details = []) => ({ first, row: 0, col, details });
const fixture = (id, steps) => ({ id, seed: '', cursor: [0, 0], steps });
const matches = (screen, wanted) => screen.cursorVisible === true && matchesRgb(screen, wanted);
const styles = [['faint', 2, 22, 64], ['hidden', 8, 28, 128], ['strikethrough', 9, 29, 16], ['blink', 5, 25, 32]];
const combined = normal({ flags: 255, foregroundArgb: 0xff112233, backgroundArgb: 0xff445566,
    underlineStyle: 3, underlineArgb: 0xff778899 });
const combinedWrite = '\x1b[1;2;3;4:3;5;7;8;9;38;2;17;34;51;48;2;68;85;102;58;2;119;136;153mA';

export const renditionCases = Object.freeze([
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
    fixture('charsets.dec-line-drawing', [['\x1b(0lqk\x1b(B', view('┌─┐', 3)]]),
    fixture('charsets.dec-special', [['\x1b(0ABfg\x1b(B', view('AB°±', 4)]]),
    fixture('charsets.g0-g1-switching', [['\x1b(B\x1b)0A\x0eqx\x0fB\x1b)B', view('A─│B', 4)]]),
]);

/** Bind a separate owned alternate screen; every charset case restores ASCII designation/shift. */
export async function collectRendition(options) {
    try { await options.owner('rendition-start'); }
    catch (error) {
        if (error.code === 'screenDetailsUnavailable') return { status: 'unavailable', reason: error.code };
        throw error;
    }
    return collectCells({ ...options, config: { ...options.config, cellNonce: `NTILDE_RD_${options.config.runId}` },
        fixtures: renditionCases, prefix: 'rendition', started: true });
}

/** Derive only observed state; appearance, overline and blink timing remain separate. */
export function buildRenditionReport(snapshots, runId) {
    validateFrames(snapshots, runId, { fixtures: renditionCases, prefix: 'rendition', noncePrefix: 'NTILDE_RD_', includeCellDetails: true });
    let offset = 2;
    const observations = renditionCases.map(item => {
        const before = snapshots[offset++];
        if (before.kind !== 'rendition-read') throw new Error('Missing rendition control.');
        const assertions = item.steps.map(([sequence, expected]) => {
            const target = snapshots[offset++];
            if (target.kind !== 'rendition-read') throw new Error('Invalid rendition experiment order.');
            return { sequence, expected, snapshot: target.index, passed: matches(target.screen, expected) };
        });
        const ready = matches(before.screen, view('', 0));
        const qualified = assertions.every(value => value.passed) ? 'passed' : 'failed';
        return { featureId: item.id, before: before.index, assertions, result: ready ? qualified : 'inconclusive' };
    });
    return { schemaVersion: 1, suiteId: renditionSuiteId, runId, complete: true,
        scope: 'Stored faint/hidden/strikethrough/blink flags and decoded Unicode/DEC charset cells. No overline, pixels, blink timing or stock grades.',
        initialSnapshot: 0, restoredSnapshot: snapshots.length - 1, snapshots, observations };
}
