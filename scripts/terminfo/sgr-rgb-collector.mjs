// Worker-side driver for the SGR standard-color RGB qualification. Replays
// each contracted case's exact pinned fixture bytes through the owned fence
// (shared graded-plan driver), reads the resulting stored attributes AND
// App-resolved RGB through the parent-observed readScreen journal, and then
// executes the pinned termless probe (from the derived SGR-RGB grading
// extension) against those independently observed, RGB-carrying cells.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { sgrRgbContracts, sgrRgbNoncePrefix, sgrRgbReset,
    sgrRgbCellAt, validateSgrRgbScreen } from './sgr-rgb-contracts.mjs';

/** Serve pre-captured RGB screens to a pinned termless probe, cross-checking
 * that the pinned code itself replays exactly the contracted fixture bytes. */
export function sgrRgbRecordingContext(contract, screens) {
    const pinnedFeeds = planFeeds(contract).slice(1);
    let feedIndex = 0, readIndex = 0, served;
    const serve = () => {
        if (readIndex >= screens.length) throw new Error(`Pinned ${contract.caseId} read order differs from the contract.`);
        if (!served) served = screens[readIndex];
        return served;
    };
    return {
        feed(text) {
            if (text !== pinnedFeeds[feedIndex]) {
                throw new Error(`Pinned ${contract.caseId} feed ${feedIndex} differs from the contract.`);
            }
            feedIndex++;
            served = undefined;
            readIndex = feedIndex - 1;
        },
        getText() {
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getCell(row, col) {
            return sgrRgbCellAt(serve(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSgrRgbGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSgrRgbAdapter;
    const collected = await collectGradedPlans({ contracts: sgrRgbContracts, prefix: 'sgrgb',
        reset: sgrRgbReset, nonce: sgrRgbNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = sgrRgbContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateSgrRgbScreen(screen);
        const pinned = gradingModule.ntildeRunSgrRgbTermless(contract.caseId,
            sgrRgbRecordingContext(contract, item.screens));
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
