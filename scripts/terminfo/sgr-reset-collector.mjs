// Worker-side driver for the SGR selective-reset qualification (SGR 24 and
// SGR 27). Replays each contracted case's exact plan through the owned fence
// (shared graded-plan driver): the PINNED fixture bytes on a measured reset
// screen plus discriminating controls. Every read is journaled by the parent
// observer; the pinned termless probe then runs over the independently
// observed screen of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { sgrResetContracts, sgrResetNoncePrefix, sgrResetReset, sgrResetCellAt, validateSgrResetScreen } from './sgr-reset-contracts.mjs';

/** Serve the pre-captured screen of the pinned fixture to a pinned termless
 * probe, cross-checking that the pinned code itself replays exactly the
 * contracted pinned fixture bytes (and nothing else). */
export function sgrResetRecordingContext(contract, screens) {
    let fed = 0;
    return {
        feed(text) {
            if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed]) {
                throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
            }
            fed++;
        },
        getText() {
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getCell(row, col) {
            if (fed !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} read before its fixture.`);
            return sgrResetCellAt(screens[contract.pinnedRead], row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSgrResetGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSgrResetAdapter;
    const collected = await collectGradedPlans({ contracts: sgrResetContracts, prefix: 'sgsr',
        reset: sgrResetReset, nonce: sgrResetNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = sgrResetContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateSgrResetScreen(screen);
        const pinned = gradingModule.ntildeRunSgrResetTermless(contract.caseId,
            sgrResetRecordingContext(contract, item.screens));
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
