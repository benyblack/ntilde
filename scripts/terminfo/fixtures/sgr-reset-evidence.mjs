// Synthetic, fully valid SGR selective-reset grading evidence for portable tests. Every
// frame, run-log entry and pinned result is produced through the same production
// validators used for real measurements; the generic screen and run builders are
// shared with the palette fixtures.
import { sgrResetContracts, sgrResetReset, sgrResetObservation, gradeSgrResetCase } from '../sgr-reset-contracts.mjs';
import { replaySgrResetPinned } from '../sgr-reset-grading.mjs';
import { sgrResetAdapterId } from '../sgr-reset-grading-collector.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell, syntheticSgrPaletteScreen as syntheticSgrResetScreen } from './sgr-palette-evidence.mjs';

const argbOf = ({ r, g, b }) => 0xff000000 + r * 65536 + g * 256 + b;
const requested = { r: 255, g: 0, b: 128 };
/** A styled cell: `attrs` boolean attributes plus an optional underline style and stored underline color. */
export const styled = (char, attrs, { style = 0, rgb = null } = {}) =>
    colorCell(char, { attrs, underlineStyle: style, underlineArgb: rgb ? argbOf(rgb) : null });

/** The cell specs of every read group of one synthetic case (a conclusive, supported run), by role. */
export function syntheticSgrResetReads(caseId) {
    if (caseId === 'sgr.selective-reset.underline') {
        const set = [plainCell('C'), styled('X', ['bold'], { style: 1 })];
        return { initial: [], pinned: [...set, styled('Y', ['bold'])],
            noop: [...set, styled('Z', ['bold'], { style: 1 })],
            full: [...set, plainCell('Y')],
            wrong: [...set, styled('Y', [], { style: 1 })],
            double: [plainCell('C'), styled('X', ['bold'], { style: 2 }), styled('Y', ['bold'])],
            color: [plainCell('C'), styled('X', ['bold'], { style: 1, rgb: requested }), styled('Y', ['bold']),
                styled('Z', ['bold'], { style: 1 })],
            sgr59: [plainCell('C'), styled('X', ['bold'], { style: 1, rgb: requested }), styled('Y', ['bold'], { style: 1 })] };
    }
    if (caseId === 'sgr.selective-reset.inverse') {
        const set = [plainCell('C'), styled('X', ['bold', 'inverse'])];
        const pinned = [...set, styled('Y', ['bold'])];
        return { initial: [], pinned, noop: [...set, styled('Z', ['bold', 'inverse'])],
            full: [...set, plainCell('Y')], wrong: [...set, styled('Y', ['bold', 'inverse'])], decscnm: pinned };
    }
    throw new Error(`Unknown synthetic case ${caseId}.`);
}

/** The binding of the DECSCNM-on control read. */
export const reverseBinding = binding => ({ ...binding, reverseVideo: true });

const resetSuite = Object.freeze({ noncePrefix: 'NTILDE_SR_', prefix: 'sgsr', contracts: sgrResetContracts,
    adapterId: sgrResetAdapterId, derivedFrom: 'ntilde-owned-sgr-underline-color-grading-v1', reset: sgrResetReset,
    screens: (caseId, binding, readsOf) => {
        const reads = readsOf?.(caseId) ?? syntheticSgrResetReads(caseId);
        const contract = sgrResetContracts.find(item => item.caseId === caseId);
        return contract.roles.map(role => syntheticSgrPaletteScreen(reads[role],
            role === 'decscnm' ? (reads.reverseBinding ?? reverseBinding(binding)) : binding));
    },
    replay: replaySgrResetPinned });

/** The screens of every read group of one synthetic case. */
export const syntheticSgrResetScreens = (caseId, binding = paletteBinding(), readsOf = null) =>
    resetSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticSgrResetEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(resetSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrResetGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = sgrResetContracts.find(item => item.caseId === caseId);
    return gradeSgrResetCase(contract, sgrResetObservation(contract, syntheticSgrResetScreens(caseId, binding, readsOf)));
}
