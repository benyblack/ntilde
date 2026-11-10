// Worker-side driver for the left/right-margin qualification (modes.left-right-margin). Replays the case's exact plan through the
// owned fence (shared graded-plan driver): the PINNED ESC[?69h and ESC[?69l on a measured reset screen, then the graded controls over a
// block seed. Every read is journaled by the parent observer with the App's stored margins; the pinned termless probe then runs over
// the mode the App reported after its own first feed.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { lrMarginsContracts, lrMarginsNoncePrefix, lrMarginsReset, validateLrMarginsScreen } from './lr-margins-contracts.mjs';

/** Serve the journaled mode to the pinned termless probe, cross-checking that the pinned code feeds exactly its two pinned writes and
 * reads only the left/right-margin mode, between them. */
export function lrMarginsRecordingContext(contract, screens) {
    let fed = 0;
    return {
        get fedCount() { return fed; },
        feed: text => {
            if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed]) throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
            fed++;
        },
        getMode: name => {
            if (name !== 'leftRightMargin' || fed !== 1) throw new Error(`Pinned ${contract.caseId} reads an uncontracted mode.`);
            return screens[contract.setRead].horizontalMargins.leftRightMarginMode;
        },
        getCell: () => { throw new Error(`Pinned ${contract.caseId} probe must not read cells.`); },
        getCursor: () => { throw new Error(`Pinned ${contract.caseId} probe must not read the cursor.`); },
    };
}

/** Run the contracted case serially with parent-journaled controls. */
export async function collectLrMarginsGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeLrMarginsAdapter;
    const collected = await collectGradedPlans({ contracts: lrMarginsContracts, prefix: 'lrmm',
        reset: lrMarginsReset, nonce: lrMarginsNoncePrefix + config.runId, fence, owner, planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = lrMarginsContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateLrMarginsScreen(screen);
        const context = lrMarginsRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunLrMarginsTermless(contract.caseId, context);
        if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
