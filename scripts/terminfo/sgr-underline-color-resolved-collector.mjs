// Worker-side driver for the App-observed underline-color resolution suite. Replays each case's exact plan
// through the owned fence (shared graded-plan driver): the fixture on a measured reset screen, the OSC 4
// mutation of entries 4 and 5, then their restoration, with every read journaled by the parent observer.
// There is no pinned probe and no collector extension: these are Ntilde extension controls.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { underlineResolvedContracts, underlineResolvedNoncePrefix, underlineResolvedReset, underlineResolvedSuiteId,
    underlineResolvedCaseIds, underlineResolvedContractsVersion, validateUnderlineResolvedScreen } from './sgr-underline-color-resolved-contracts.mjs';

/** Run every contracted case serially with parent-journaled controls. */
export async function collectUnderlineResolvedGrading({ config, fence, owner }) {
    const collected = await collectGradedPlans({ contracts: underlineResolvedContracts, prefix: 'sgur',
        reset: underlineResolvedReset, nonce: underlineResolvedNoncePrefix + config.runId, fence, owner, planFeeds, planReads });
    const run = { schemaVersion: 1, suiteId: underlineResolvedSuiteId, contractsVersion: underlineResolvedContractsVersion,
        caseIds: [...underlineResolvedCaseIds], cases: [] };
    for (const item of collected) {
        for (const screen of item.screens) validateUnderlineResolvedScreen(screen);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads });
    }
    return run;
}
