// Synthetic, fully valid SGR-underline-color grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same production
// validators used for real measurements; the generic screen and run builders are
// shared with the palette fixtures.
import { sgrUnderlineColorContracts, sgrUnderlineColorReset, sgrUnderlineColorObservation, gradeSgrUnderlineColorCase,
    equalLiteral, requestedUnderlineColor, mutation } from '../sgr-underline-color-contracts.mjs';
import { replaySgrUnderlineColorPinned } from '../sgr-underline-color-grading.mjs';
import { sgrUnderlineColorAdapterId } from '../sgr-underline-color-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, paletteChannel, literalChannel, syntheticColorEvidence,
    syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell, paletteChannel, literalChannel,
    syntheticSgrPaletteScreen as syntheticSgrUnderlineColorScreen } from './sgr-palette-evidence.mjs';

const argbOf = ({ r, g, b }) => 0xff000000 + r * 65536 + g * 256 + b;
/** An underlined cell with an explicit underline color: { index } for a palette index, { rgb } for literal bytes, none for default. */
export const ulCell = (char, { style = 1, index = -1, rgb = null, ...rest } = {}) =>
    colorCell(char, { underlineStyle: style, underlineColorIndex: index, underlineArgb: rgb ? argbOf(rgb) : null, ...rest });

/** The cell specs of every read group of one synthetic case (a conclusive, supported run). */
export function syntheticSgrUnderlineColorReads(caseId) {
    if (caseId === 'sgr.underline-color-indexed') {
        const fixture = [ulCell('A', { fg: paletteChannel(4) }), ulCell('X', { fg: paletteChannel(4), index: 5 }),
            ulCell('B', { fg: paletteChannel(5) }), ulCell('Y', { fg: paletteChannel(5), index: 4 })];
        const control = [ulCell('P', { rgb: equalLiteral }), ulCell('Q', { index: 67 }), ulCell('R', { fg: paletteChannel(5), index: 5 }),
            plainCell('N'), ulCell('S', { index: 5 })];
        return { fixture, control, mutated: [...fixture, ...control], restored: [...fixture, ...control] };
    }
    if (caseId === 'sgr.underline-color-rgb') {
        return {
            fixture: [ulCell('A', { fg: literalChannel(0, 0, 255) }), ulCell('B', { fg: literalChannel(0, 255, 0) }),
                ulCell('X', { fg: literalChannel(0, 255, 0), rgb: requestedUnderlineColor })],
            control: [ulCell('P', { rgb: equalLiteral }), ulCell('Q', { index: 67 }), ulCell('R', { fg: literalChannel(255, 0, 128) }),
                plainCell('N'), ulCell('C', { rgb: requestedUnderlineColor }), ulCell('D', { rgb: requestedUnderlineColor })],
        };
    }
    if (caseId === 'sgr.underline-color-reset') {
        return {
            fixture: [ulCell('C'), ulCell('X', { rgb: requestedUnderlineColor }), ulCell('Y')],
            control: [ulCell('S', { style: 3, index: 5, fg: paletteChannel(2), bg: paletteChannel(4) }),
                ulCell('T', { style: 3, fg: paletteChannel(2), bg: paletteChannel(4) }), ulCell('U', { rgb: { r: 1, g: 2, b: 3 } }),
                ulCell('V'), plainCell('N')],
        };
    }
    throw new Error(`Unknown synthetic case ${caseId}.`);
}

/** The binding of the mutated read: the contracted entries changed and flagged as overrides. */
export const mutatedBinding = binding => ({ ...binding,
    ansiPaletteArgb: binding.ansiPaletteArgb.map((value, index) => mutation[index] ?? value),
    paletteOverrideIndices: [...new Set([...binding.paletteOverrideIndices, ...Object.keys(mutation).map(Number)])].sort((a, b) => a - b) });

const underlineColorSuite = Object.freeze({ noncePrefix: 'NTILDE_UC_', prefix: 'sguc', contracts: sgrUnderlineColorContracts,
    adapterId: sgrUnderlineColorAdapterId, derivedFrom: 'ntilde-owned-sgr-underline-grading-v1', reset: sgrUnderlineColorReset,
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticSgrUnderlineColorReads(caseId);
        const groups = [[reads.fixture, binding], [reads.control, binding]];
        if (reads.mutated) groups.push([reads.mutated, reads.mutatedBinding ?? mutatedBinding(binding)], [reads.restored, reads.restoredBinding ?? binding]);
        return groups.map(([specs, groupBinding]) => syntheticSgrPaletteScreen(specs, groupBinding));
    },
    replay: replaySgrUnderlineColorPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticSgrUnderlineColorScreens = (caseId, binding = paletteBinding(), readsOf = null) =>
    underlineColorSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticSgrUnderlineColorEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(underlineColorSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrUnderlineColorGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = sgrUnderlineColorContracts.find(item => item.caseId === caseId);
    return gradeSgrUnderlineColorCase(contract, sgrUnderlineColorObservation(contract,
        syntheticSgrUnderlineColorScreens(caseId, binding, readsOf), binding));
}
