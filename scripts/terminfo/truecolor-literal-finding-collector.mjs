// Worker-side driver for the truecolor-literal finding suite. Replays each case's
// exact plan through the owned fence (shared graded-plan driver): the fixture on a
// measured reset screen, the OSC 4 mutation of the aliased entry, then its restore,
// with every read journaled by the parent observer. There is no pinned probe and no
// collector extension: this is an Ntilde finding, not a terminfo.dev case.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { truecolorFindingContracts, truecolorFindingNoncePrefix, truecolorFindingReset, truecolorFindingSuiteId,
    truecolorFindingCaseIds, truecolorFindingContractsVersion, sgrColorCellAtScreenCheck } from './truecolor-literal-finding-contracts.mjs';

/** Run every contracted case serially with parent-journaled controls. */
export async function collectTruecolorFindingGrading({ config, fence, owner }) {
    const collected = await collectGradedPlans({ contracts: truecolorFindingContracts, prefix: 'sgtf',
        reset: truecolorFindingReset, nonce: truecolorFindingNoncePrefix + config.runId, fence, owner, planFeeds, planReads });
    const run = { schemaVersion: 1, suiteId: truecolorFindingSuiteId, contractsVersion: truecolorFindingContractsVersion,
        caseIds: [...truecolorFindingCaseIds], cases: [] };
    for (const item of collected) {
        for (const screen of item.screens) sgrColorCellAtScreenCheck(screen);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads });
    }
    return run;
}
