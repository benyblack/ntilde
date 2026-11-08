// Worker-side driver for the SGR attribute qualification (underline, inverse,
// faint, hidden, strikethrough). Replays each contracted case's exact plan
// through the owned fence (shared graded-plan driver): a neutral control cell,
// a clear, the PINNED fixture bytes, and a trailing reset control. Every read
// is journaled by the parent observer; the pinned termless probe then runs
// over the independently observed screen of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { sgrAttrsContracts, sgrAttrsNoncePrefix, sgrAttrsReset,
    sgrAttrsCellAt, validateSgrAttrsScreen } from './sgr-attrs-contracts.mjs';

/** Serve the pre-captured screen of the pinned fixture to a pinned termless
 * probe, cross-checking that the pinned code itself replays exactly the
 * contracted pinned fixture bytes (and nothing else). */
export function sgrAttrsRecordingContext(contract, screens) {
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
            return sgrAttrsCellAt(screens[contract.pinnedRead], row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSgrAttrsGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSgrAttrsAdapter;
    const collected = await collectGradedPlans({ contracts: sgrAttrsContracts, prefix: 'sgattr',
        reset: sgrAttrsReset, nonce: sgrAttrsNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = sgrAttrsContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateSgrAttrsScreen(screen);
        const pinned = gradingModule.ntildeRunSgrAttrsTermless(contract.caseId,
            sgrAttrsRecordingContext(contract, item.screens));
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
