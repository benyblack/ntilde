// Synthetic, fully valid SGR closure grading evidence for portable tests. Every frame, run-log entry and pinned result
// is produced through the same production validators used for real measurements; the generic screen and run builders
// are shared with the palette fixtures.
import { sgrClosureContracts, sgrClosureReset, sgrClosureObservation, gradeSgrClosureCase } from '../sgr-closure-contracts.mjs';
import { replaySgrClosurePinned } from '../sgr-closure-grading.mjs';
import { sgrClosureAdapterId } from '../sgr-closure-grading-collector.mjs';
import { syntheticSgrUnderlineColorReads } from './sgr-underline-color-evidence.mjs';
import { paletteBinding, colorCell, plainCell, syntheticColorEvidence, syntheticSgrPaletteScreen } from './sgr-palette-evidence.mjs';

export { paletteBinding, colorCell, plainCell, syntheticSgrPaletteScreen as syntheticSgrClosureScreen } from './sgr-palette-evidence.mjs';

const argbOf = ({ r, g, b }) => 0xff000000 + r * 65536 + g * 256 + b;
/** A styled cell: boolean `attrs`, an optional underline style and stored underline color. */
export const styled = (char, attrs = [], { style = 0, rgb = null } = {}) =>
    colorCell(char, { attrs, underlineStyle: style, underlineArgb: rgb ? argbOf(rgb) : null });

/** The cell specs of every read group of one synthetic case (a conclusive, supported run). */
export function syntheticSgrClosureReads(caseId) {
    if (caseId === 'sgr.blink') {
        return { groups: [[plainCell('C')], [styled('X', ['blink'])], [styled('X', ['blink']), plainCell('Y')],
            [styled('W', ['bold', 'dim', 'italic', 'inverse', 'hidden', 'strikethrough', 'overline'], { style: 1 })]] };
    }
    if (caseId === 'sgr.overline') {
        const all = ['bold', 'dim', 'italic', 'blink', 'inverse', 'hidden', 'strikethrough'];
        return { groups: [[plainCell('C')], [styled('X', ['overline'])], [styled('X', ['overline']), plainCell('Y')],
            [styled('W', all, { style: 1, rgb: { r: 255, g: 0, b: 128 } })],
            [styled('V', ['overline'], { style: 1 }), styled('U', ['overline']), plainCell('T')]] };
    }
    if (caseId === 'sgr.underline.color') {
        const reads = syntheticSgrUnderlineColorReads('sgr.underline-color-rgb');
        return { groups: [reads.fixture, reads.control] };
    }
    throw new Error(`Unknown synthetic case ${caseId}.`);
}

const closureSuite = Object.freeze({ noncePrefix: 'NTILDE_CL_', prefix: 'sgcl', contracts: sgrClosureContracts,
    adapterId: sgrClosureAdapterId, derivedFrom: 'ntilde-owned-sgr-selective-reset-grading-v1', reset: sgrClosureReset,
    screens: (caseId, binding, readsOf) => (readsOf?.(caseId) ?? syntheticSgrClosureReads(caseId)).groups
        .map(specs => syntheticSgrPaletteScreen(specs, binding)),
    replay: replaySgrClosurePinned });

/** The screens of every read group of one synthetic case. */
export const syntheticSgrClosureScreens = (caseId, binding = paletteBinding(), readsOf = null) => closureSuite.screens(caseId, binding, readsOf);

/** A complete, valid synthetic run: frames, worker run log and adapter facts. */
export const syntheticSgrClosureEvidence = (runId = 'a'.repeat(32), bundleSha256 = 'f'.repeat(64),
    binding = paletteBinding(), readsOf = null) => syntheticColorEvidence(closureSuite, runId, bundleSha256, binding, readsOf);

/** The independent contract verdict for one synthetic case. */
export function syntheticSgrClosureGrade(caseId, binding = paletteBinding(), readsOf = null) {
    const contract = sgrClosureContracts.find(item => item.caseId === caseId);
    return gradeSgrClosureCase(contract, sgrClosureObservation(contract, syntheticSgrClosureScreens(caseId, binding, readsOf), binding));
}
