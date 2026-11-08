// Worker-side driver for the SGR closure qualification (sgr.blink, sgr.overline, sgr.underline.color). Replays each
// contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED fixture bytes on a
// measured reset screen plus controls. Every read is journaled by the parent observer; the pinned termless probe then
// runs over the independently observed screen of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { sgrClosureContracts, sgrClosureNoncePrefix, sgrClosureReset, sgrClosureCellAt, sgrClosureUnderlineCellAt,
    validateSgrClosureScreen } from './sgr-closure-contracts.mjs';

/** Serve the pre-captured screen of the pinned fixture to a pinned termless probe, cross-checking that the pinned code
 * itself replays exactly the contracted pinned fixture bytes (and nothing else). The underline-color case serves its
 * richer stored projection; blink and overline serve the attribute projection. */
export function sgrClosureRecordingContext(contract, screens) {
    let fed = 0;
    const cellAt = contract.kind === 'underline-color' ? sgrClosureUnderlineCellAt : sgrClosureCellAt;
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
            return cellAt(screens[contract.pinnedRead], row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSgrClosureGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSgrClosureAdapter;
    const collected = await collectGradedPlans({ contracts: sgrClosureContracts, prefix: 'sgcl',
        reset: sgrClosureReset, nonce: sgrClosureNoncePrefix + config.runId, fence, owner, planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = sgrClosureContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateSgrClosureScreen(screen);
        const pinned = gradingModule.ntildeRunSgrClosureTermless(contract.caseId, sgrClosureRecordingContext(contract, item.screens));
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
