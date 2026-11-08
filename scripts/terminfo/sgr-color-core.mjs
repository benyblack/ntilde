// Shared building blocks of the layered SGR color qualifications (palette,
// truecolor): the stored/resolved cell projection, the stored-state predicates
// (default marker, palette index, literal bytes), plan/case builders and the
// observation and grading entry points. Each suite keeps its own pinned fixtures,
// controls and verdict logic; only the mechanics shared byte-for-byte live here.
import { planFeed, planRead } from './graded-plan.mjs';
import { sgrAttrsCellAt, sgrAttrsReset, sgrAttrsClear } from './sgr-attrs-contracts.mjs';
import { expandResolvedColors } from './resolved-colors.mjs';

/** One projection of one cell: the attribute suite's, which already carries flags,
 * stored details (indices, literal ARGB, underline color, overline) and resolved RGB. */
export function sgrColorCellAt(screen, row, col) {
    const cell = sgrAttrsCellAt(screen, row, col);
    // The stored indices come from the cell details, the flags from the attribute runs and the resolved
    // channels from the resolved runs: all three must name the same palette index for this one cell.
    const position = row * screen.cols + col;
    let offset = 0, run;
    for (const candidate of screen.attributeRuns ?? []) {
        if (position < offset + candidate.count) { run = candidate; break; }
        offset += candidate.count;
    }
    const resolved = expandResolvedColors(screen.resolvedColors, screen.rows * screen.cols, screen.colorBinding)[position];
    for (const [channel, field] of [['fg', 'fg'], ['bg', 'bg']]) {
        const resolvedIndex = resolved[channel].source === 'palette' ? resolved[channel].index : -1;
        if (run?.[field] !== cell[channel + 'Index'] || resolvedIndex !== cell[channel + 'Index']) {
            throw new Error(`Contradictory stored and resolved ${channel} palette indices at ${row},${col}.`);
        }
    }
    return cell;
}
const attributeNames = Object.freeze(['bold', 'dim', 'italic', 'underline', 'inverse', 'hidden', 'strikethrough', 'blink', 'overline']);
const literalField = channel => (channel === 'fg' ? 'foregroundArgb' : 'backgroundArgb');

/** No style attribute (other than `except`), flag bit, underline style or underline color is set. */
export const attributesClear = (cell, except = []) => attributeNames.every(name => except.includes(name)
        || (name === 'underline' ? cell.underline === false : cell[name] === false))
    && (except.length > 0 || (cell.flags & 0xff) === 0)
    && cell.underlineStyle === 0 && cell.underlineColorIndex === -1 && cell.underlineArgb === null;
/** The channel is on the default marker: default source, no index, no literal bytes. */
export const isDefault = (cell, channel) => cell[channel + 'Source'] === 'default' && cell[channel + 'Index'] === -1
    && cell[literalField(channel)] === null;
/** The channel stores exactly this palette index and no literal bytes. */
export const isPalette = (cell, channel, index) => cell[channel + 'Source'] === 'palette' && cell[channel + 'Index'] === index
    && cell[literalField(channel)] === null;
/** The channel stores exactly these literal RGB bytes (alpha-agnostic) and no palette index. */
export const isLiteral = (cell, channel, rgb) => cell[channel + 'Source'] === 'literal' && cell[channel + 'Index'] === -1
    && cell[literalField(channel)] !== null
    && (cell[literalField(channel)] & 0xffffff) === ((rgb.r << 16) | (rgb.g << 8) | rgb.b);
/** A cell with no attribute and both channels on default markers. */
export const neutral = cell => attributesClear(cell) && isDefault(cell, 'fg') && isDefault(cell, 'bg');

export const sameRgb = (a, b) => a.r === b.r && a.g === b.g && a.b === b.b;
export const sameCellColor = (a, b) => {
    if (a === undefined || b === undefined) return false;
    if (a === null || b === null) return a === b;
    return sameRgb(a, b);
};
/** Every verdict carries both layer states; layers a case never reached are `not-evaluated`. */
export const inconclusive = (note, layers = { stored: 'not-evaluated', resolved: 'not-evaluated' }) => ({ outcome: 'inconclusive', note, layers });
export const argbRgb = argb => ({ r: (argb >> 16) & 255, g: (argb >> 8) & 255, b: argb & 255 });

/** Case builder: pinned fixture, then a control group on a cleared screen. */
export const case_ = (fields) => Object.freeze({
    pinnedPath: 'termless', pinnedRead: 0, fixtureCells: fields.fixtureCells,
    pinnedFeeds: Object.freeze([fields.fixture]),
    plan: Object.freeze([planFeed(sgrAttrsReset), planFeed(fields.fixture), planRead('cells'),
        planFeed(sgrAttrsClear), planFeed(fields.control), planRead('cells')]),
    ...fields,
});


/** A compact observed record: stored text, flags and the named fields per cell. */
export function briefObservation(observation, fields) {
    const brief = cell => ({ char: cell.char, flags: cell.flags, ...Object.fromEntries(fields.map(name => [name, cell[name]])) });
    return { fixture: observation.fixture.map(brief), control: observation.control.map(brief),
        reverseVideo: observation.reverseVideo, defaults: observation.defaults };
}

/** Project the two read groups of one case into the grader's observation: the
 * pinned fixture cells, the control cells, DECSCNM state across BOTH reads and the
 * recorded theme defaults the setup is compared with. */
export function colorObservation(contract, screens, label) {
    if (!Array.isArray(screens) || screens.length !== 2) throw new Error(`Missing ${label} read captures.`);
    const binding = screens.map(screen => screen.colorBinding.reverseVideo);
    const theme = screens[0].colorBinding;
    return { fixture: Array.from({ length: contract.fixtureCells }, (_, col) => sgrColorCellAt(screens[0], 0, col)),
        control: Array.from({ length: contract.controlCells ?? 3 }, (_, col) => sgrColorCellAt(screens[1], 0, col)),
        reverseVideo: binding.every(value => value === false) ? false : binding.some(value => value === true) ? true : null,
        defaults: { fg: argbRgb(theme.foregroundArgb), bg: argbRgb(theme.backgroundArgb) } };
}

/** Independent contract grader entry: mirrors the pinned criteria without the bundle. */
export function gradeColorCase(contract, observation, revision, label) {
    if (revision !== 1) throw new Error(`Unknown ${label} contracts revision ${revision}.`);
    if (!Array.isArray(observation?.fixture) || observation.fixture.length !== contract.fixtureCells
        || !Array.isArray(observation.control) || observation.control.length !== (contract.controlCells ?? 3) || !observation.defaults) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(observation);
}
