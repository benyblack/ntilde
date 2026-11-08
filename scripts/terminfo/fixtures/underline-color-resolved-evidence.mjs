// Synthetic, fully valid App-observed underline-resolution evidence for portable tests. Every frame and run-log
// entry goes through the same production validators as a real measurement. The synthetic "App" derives each
// cell's reported underline from the cell's stored spec and the frame's recorded palette; `ulOf` may override
// it to build wrong-App, substituted-parent or tampered evidence, and `bindingOf` may override the recorded palette.
import { snapshotHash } from '../agent-observer.mjs';
import { expandResolvedColors } from '../resolved-colors.mjs';
import { underlineResolvedContracts, underlineResolvedFeeds, underlineResolvedNoncePrefix, underlineResolvedReset,
    underlineResolvedSuiteId, underlineResolvedContractsVersion, underlineResolvedCaseIds, gradeUnderlineResolvedCase,
    underlineResolvedObservation, underlineResolvedMutation } from '../sgr-underline-color-resolved-contracts.mjs';
import { canonicalPalette, paletteBinding, colorCell, plainCell, paletteChannel, literalChannel, defaultChannel,
    syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

const paneId = '0f0e0d0c-0b0a-4009-8008-700600500400';
const deviceAttribute = '\x1b[?62;4;22c';
export const phases = Object.freeze(['before', 'mutated', 'restored']);
const note = 'Underline color exactly as the draw path resolves it at the theme-resolution stage (synthetic test fixture note).';

/** The recorded palette of one phase: the OSC 4 mutation shows as two overridden entries. */
export function underlineBinding(phase, base = {}) {
    if (phase !== 'mutated') return paletteBinding(base);
    const palette = canonicalPalette();
    for (const [index, argb] of Object.entries(underlineResolvedMutation)) palette[Number(index)] = argb;
    return { ...paletteBinding({ ...base, palette }), paletteOverrideIndices: Object.keys(underlineResolvedMutation).map(Number) };
}

const channelOf = kind => kind.kind === 'default' ? defaultChannel : kind.kind === 'palette' ? paletteChannel(kind.index)
    : literalChannel(kind.rgb.r, kind.rgb.g, kind.rgb.b);
const argbOf = ({ r, g, b }) => 0xff000000 + r * 65536 + g * 256 + b;
/** The cell specs of one contract's setup, as the shared synthetic screen builder takes them. */
export const underlineCells = contract => contract.setup.map(want => colorCell(want.char, {
    fg: channelOf(want.fg), bg: channelOf(want.bg), attrs: want.inverse ? ['inverse'] : [],
    underlineStyle: want.ul.kind === 'none' ? 0 : 1,
    underlineColorIndex: want.ul.kind === 'palette' ? want.ul.index : -1,
    underlineArgb: want.ul.kind === 'literal' ? argbOf(want.ul.rgb) : null }));

/** What an honest App reports for one spec under a binding. */
export function honestUnderline(spec, resolvedCell, binding) {
    if (!spec.underlineStyle) return { source: 'none', index: -1 };
    if (spec.underlineColorIndex >= 0) return { source: 'palette', index: spec.underlineColorIndex, argb: binding.ansiPaletteArgb[spec.underlineColorIndex] };
    if (spec.underlineArgb !== null) return { source: 'literal', index: -1, argb: spec.underlineArgb };
    const inverse = spec.attrs.includes('inverse');
    return { source: 'inherit', index: -1, inheritsFrom: inverse ? 'background' : 'foreground', argb: (inverse ? resolvedCell.bg : resolvedCell.fg).argb };
}

const runKey = run => JSON.stringify(run, (key, value) => key === 'count' ? undefined : value);
function coalesce(runs) {
    const out = [];
    for (const run of runs) {
        const previous = out.at(-1);
        if (previous && runKey(previous) === runKey(run)) previous.count += run.count;
        else out.push({ ...run });
    }
    return out;
}

/** A complete valid underline-stage screen: the shared synthetic screen plus the opt-in resolved underline. */
export function syntheticUnderlineScreen(specs, binding = paletteBinding(), ulOf = null) {
    const base = syntheticSgrPaletteScreen(specs, binding);
    const resolved = expandResolvedColors(base.resolvedColors, base.rows * base.cols, base.colorBinding);
    const withStage = { ...base.colorBinding, underlineStage: 'underline-resolution', underlineStageNote: note };
    const plain = colorCell(' ');
    const perCell = resolved.map((cell, position) => {
        const spec = specs[position] ?? plain;
        const honest = honestUnderline(spec, cell, binding);
        return { count: 1, fg: cell.fg, bg: cell.bg, ul: ulOf?.(spec, position, honest, binding) ?? honest };
    });
    return { ...base, resolvedColors: coalesce(perCell), colorBinding: withStage };
}

function frame(index, kind, screen, barrier) {
    return { index, kind, paneId, barrier, screen, sha256: snapshotHash(screen) };
}

/** A complete synthetic run: frames and worker run log. */
export function syntheticUnderlineResolvedEvidence(runId = 'a'.repeat(32), { cellsOf = null, bindingOf = null, ulOf = null } = {}) {
    const nonce = underlineResolvedNoncePrefix + runId;
    const blank = syntheticUnderlineScreen([], paletteBinding());
    const bindScreen = { ...syntheticUnderlineScreen([], paletteBinding()), lines: [nonce, '', '', '', '', ''], cursorCol: nonce.length };
    const frames = [frame(0, 'sgur-start', blank, null), frame(1, 'sgur-bind', bindScreen, deviceAttribute)];
    const cases = underlineResolvedContracts.map(contract => {
        const feeds = underlineResolvedFeeds(contract);
        const screens = phases.map(phase => syntheticUnderlineScreen(cellsOf?.(contract, phase) ?? underlineCells(contract),
            bindingOf?.(contract, phase) ?? underlineBinding(phase), ulOf ? (spec, position, honest, binding) => ulOf(contract, phase, spec, position, honest, binding) : null));
        for (const screen of screens) frames.push(frame(frames.length, 'sgur-read', screen, deviceAttribute));
        return { caseId: contract.caseId, feeds, barriers: feeds.map(() => deviceAttribute),
            reads: screens.map(screen => ({ kind: 'cells', cursor: [screen.cursorRow, screen.cursorCol] })) };
    });
    frames.push(frame(frames.length, 'sgur-finish', blank, deviceAttribute));
    const run = { schemaVersion: 1, suiteId: underlineResolvedSuiteId, contractsVersion: underlineResolvedContractsVersion,
        caseIds: [...underlineResolvedCaseIds], cases };
    return { frames, run, runId, reset: underlineResolvedReset };
}

/** The independent contract verdict for one synthetic case. */
export function syntheticUnderlineResolvedGrade(caseId, { cellsOf = null, bindingOf = null, baselineOf = null, ulOf = null } = {}) {
    const contract = underlineResolvedContracts.find(item => item.caseId === caseId);
    const screens = phases.map(phase => syntheticUnderlineScreen(cellsOf?.(contract, phase) ?? underlineCells(contract),
        bindingOf?.(contract, phase) ?? underlineBinding(phase), ulOf ? (spec, position, honest, binding) => ulOf(contract, phase, spec, position, honest, binding) : null));
    const baseline = baselineOf?.(contract) ?? bindingOf?.(contract, 'before') ?? underlineBinding('before');
    const stagedBaseline = { ...baseline, underlineStage: 'underline-resolution', underlineStageNote: note };
    return gradeUnderlineResolvedCase(contract, underlineResolvedObservation(contract, screens, stagedBaseline));
}
