// Worker-side driver for the SGR underline-style qualification (double,
// curly, dotted and dashed). Replays each contracted case's exact plan
// through the owned fence (shared graded-plan driver): the PINNED fixture bytes
// on a measured reset screen, then a restoration group and a labeled control ladder. Every read is
// journaled by the parent observer; the pinned termless probe then runs over the
// independently observed screen of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { sgrUnderlineContracts, sgrUnderlineNoncePrefix, sgrUnderlineReset,
    sgrUnderlineCellAt, validateSgrUnderlineScreen } from './sgr-underline-contracts.mjs';

/** Serve the pre-captured screen of the pinned fixture to a pinned termless
 * probe, cross-checking that the pinned code itself replays exactly the
 * contracted pinned fixture bytes (and nothing else). */
export function sgrUnderlineRecordingContext(contract, screens) {
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
            return sgrUnderlineCellAt(screens[contract.pinnedRead], row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSgrUnderlineGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSgrUnderlineAdapter;
    const collected = await collectGradedPlans({ contracts: sgrUnderlineContracts, prefix: 'sgus',
        reset: sgrUnderlineReset, nonce: sgrUnderlineNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = sgrUnderlineContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateSgrUnderlineScreen(screen);
        const pinned = gradingModule.ntildeRunSgrUnderlineTermless(contract.caseId,
            sgrUnderlineRecordingContext(contract, item.screens));
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
