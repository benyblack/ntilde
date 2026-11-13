// Synthetic examples only (a transliteration of the pinned probe over a served declaration, never collected child bytes).
import { inputPlanV3 } from '../input-plan.mjs';
import { modifyOtherKeysAdapterId } from '../modify-other-keys-grading-collector.mjs';
import { collectModifyOtherKeysPinned } from '../modify-other-keys-collector.mjs';
import { inputModeReplyHex } from '../input-state.mjs';
import { inputRunId, syntheticInputJournal } from './input-journal.mjs';
import { modifyOtherKeysCaseIds } from '../modify-other-keys-contracts.mjs';
import { buildInputReport } from '../input-state.mjs';

/** A stand-in for the derived bundle: the pinned code's exact calls (see the seams) over the supplied context. */
export const fakeModifyOtherKeysGradingModule = Object.freeze({
    ntildeModifyOtherKeysAdapter: Object.freeze({ id: modifyOtherKeysAdapterId, probeIds: Object.freeze([...modifyOtherKeysCaseIds]) }),
    ntildeRunModifyOtherKeysTermless(probeId, ctx) {
        const note = 'No generated input event and encoded report were validated';
        return { probeId, pass: false, response: JSON.stringify({ declaredModifyOtherKeys: ctx.capabilities.extensions.has('modifyOtherKeys') }), note,
            observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note } };
    },
});

export const syntheticModifyOtherKeysAdapter = Object.freeze({ id: modifyOtherKeysAdapterId, bundleSha256: 'd'.repeat(64), upstreamBundleSha256: 'c'.repeat(64),
    derivedFromAdapterId: 'ntilde-owned-left-right-margins-grading-v1' });

const v3ControlHex = item => (item.controlPrefixHex ?? '') + inputModeReplyHex(item.modes);
const v3FinalHex = () => inputModeReplyHex({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0 });

/** Journal of the v3 plan plus the worker's pinned-run log. `actual(item)` substitutes child bytes for a read; `mutate` edits the journal. */
export function syntheticModifyOtherKeysEvidence({ runId = inputRunId, actual = item => item.expectedHex, mutate = () => {} } = {}) {
    const journal = syntheticInputJournal(runId, { plan: inputPlanV3, actual, controlHex: v3ControlHex, finalHex: v3FinalHex });
    mutate(journal);
    const controls = {};
    for (const frame of journal.frames) if (frame.kind === 'input-control') controls[frame.caseId] = frame.capture.hex;
    const geometry = { rows: journal.frames[0].screen.rows, cols: journal.frames[0].screen.cols };
    const run = collectModifyOtherKeysPinned({ geometry, controls, gradingModule: fakeModifyOtherKeysGradingModule });
    return { ...journal, run, runId, adapter: { ...syntheticModifyOtherKeysAdapter },
        inputReport: () => buildInputReport(journal.frames, journal.actions, journal.seal, runId, inputPlanV3) };
}
