// Synthetic, fully valid SGR-truecolor grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same production
// validators used for real measurements; the generic screen and run builders are
// shared with the palette fixtures.
import { sgrTruecolorContracts, sgrTruecolorReset, sgrTruecolorObservation, gradeSgrTruecolorCase } from '../sgr-truecolor-contracts.mjs';
import { replaySgrTruecolorPinned } from '../sgr-truecolor-grading.mjs';
import { sgrTruecolorAdapterId } from '../sgr-truecolor-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, paletteChannel, literalChannel, syntheticColorEvidence, syntheticColorScreens } from './sgr-palette-evidence.mjs';

export { canonicalPalette, paletteBinding, defaultChannel, paletteChannel, literalChannel, colorCell, plainCell,
    syntheticSgrPaletteScreen as syntheticSgrTruecolorScreen } from './sgr-palette-evidence.mjs';

/** The cell specs of the two reads of one synthetic case (a conclusive, supported run). */
export function syntheticSgrTruecolorReads(caseId) {
    const channel = caseId.includes('.fg.') ? 'fg' : 'bg';
    const other = channel === 'fg' ? 'bg' : 'fg';
    const own = spec => ({ [channel]: spec });
    const first = channel === 'fg' ? literalChannel(255, 128, 0) : literalChannel(0, 255, 128);
    const second = literalChannel(17, 97, 201);
    return {
        fixture: [plainCell('C'), colorCell('A', own(paletteChannel(1))), colorCell('B', own(paletteChannel(4))),
            colorCell('X', own(first)), colorCell('Y', own(second))],
        control: [colorCell('P', own(literalChannel(95, 135, 175))), colorCell('Q', own(paletteChannel(67))),
            colorCell('L', own(literalChannel(0, 0, 0))), colorCell('H', own(literalChannel(255, 255, 255))),
            colorCell('U', { ...own(second), [other]: paletteChannel(other === 'fg' ? 1 : 2) }), plainCell('R')],
    };
}

const truecolorSuite = Object.freeze({ noncePrefix: 'NTILDE_ST_', prefix: 'sgtc', contracts: sgrTruecolorContracts,
    adapterId: sgrTruecolorAdapterId, derivedFrom: 'ntilde-owned-sgr-palette-grading-v1', reset: sgrTruecolorReset,
    screens: (caseId, binding, readsOf) => syntheticColorScreens(readsOf?.(caseId) ?? syntheticSgrTruecolorReads(caseId), binding),
    replay: replaySgrTruecolorPinned });

/** Screens (fixture, control) of one synthetic case. */
export const syntheticSgrTruecolorScreens = (caseId, binding = paletteBinding(), readsOf = null) =>
    truecolorSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticSgrTruecolorEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(truecolorSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrTruecolorGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = sgrTruecolorContracts.find(item => item.caseId === caseId);
    return gradeSgrTruecolorCase(contract, sgrTruecolorObservation(contract, syntheticSgrTruecolorScreens(caseId, binding, readsOf)));
}
