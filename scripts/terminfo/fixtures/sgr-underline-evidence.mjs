// Synthetic, fully valid SGR-underline grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same production
// validators used for real measurements; the generic screen and run builders are
// shared with the palette fixtures.
import { sgrUnderlineContracts, sgrUnderlineReset, sgrUnderlineObservation, gradeSgrUnderlineCase,
    sgrUnderlineLadder } from '../sgr-underline-contracts.mjs';
import { replaySgrUnderlinePinned } from '../sgr-underline-grading.mjs';
import { sgrUnderlineAdapterId } from '../sgr-underline-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell, syntheticSgrPaletteScreen as syntheticSgrUnderlineScreen } from './sgr-palette-evidence.mjs';

/** Stored UnderlineStyle codes by name. */
export const underlineCodes = Object.freeze({ single: 1, double: 2, curly: 3, dotted: 4, dashed: 5 });
/** A cell storing one underline style (the underline flag follows the style). */
export const styled = (char, style, extra = {}) => colorCell(char, { underlineStyle: underlineCodes[style], ...extra });

/** The cell specs of the four reads of one synthetic case (a conclusive, supported run):
 * the ladder stores the style each spelling should store. */
export function syntheticSgrUnderlineReads(caseId) {
    const contract = sgrUnderlineContracts.find(item => item.caseId === caseId);
    if (!contract) throw new Error(`Unknown synthetic case ${caseId}.`);
    const target = styled('X', contract.style);
    return {
        baseline: [plainCell('C')],
        target: [target],
        trailing: [target, plainCell('Y')],
        ladder: [...sgrUnderlineLadder.map(entry => styled(entry.char, entry.style)), plainCell('N')],
    };
}

const underlineSuite = Object.freeze({ noncePrefix: 'NTILDE_SU_', prefix: 'sgus', contracts: sgrUnderlineContracts,
    adapterId: sgrUnderlineAdapterId, derivedFrom: 'ntilde-owned-sgr-truecolor-grading-v1', reset: sgrUnderlineReset,
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticSgrUnderlineReads(caseId);
        return [reads.baseline, reads.target, reads.trailing, reads.ladder].map(specs => syntheticSgrPaletteScreen(specs, binding));
    },
    replay: replaySgrUnderlinePinned });

/** The four screens (baseline, target, trailing, ladder) of one synthetic case. */
export const syntheticSgrUnderlineScreens = (caseId, binding = paletteBinding(), readsOf = null) =>
    underlineSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticSgrUnderlineEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(underlineSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrUnderlineGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = sgrUnderlineContracts.find(item => item.caseId === caseId);
    return gradeSgrUnderlineCase(contract, sgrUnderlineObservation(contract, syntheticSgrUnderlineScreens(caseId, binding, readsOf)));
}
