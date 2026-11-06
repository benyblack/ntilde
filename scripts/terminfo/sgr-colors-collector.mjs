// Worker-side driver for the SGR reset/standard-color qualification batch.
// Replays each contracted case's exact pinned fixture bytes through the owned
// fence (shared graded-plan driver), reads the resulting stored attributes
// and palette indices through the parent-observed readScreen journal, and
// then executes the pinned termless probe (from the derived SGR-colors
// grading extension) against those independently observed screens.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { sgrColorsContracts, sgrColorsNoncePrefix, sgrColorsReset,
    sgrColorsCellAt } from './sgr-colors-contracts.mjs';

/** Serve pre-captured screens to a pinned termless probe, cross-checking that
 * the pinned code itself replays exactly the contracted fixture bytes. */
export function sgrColorsRecordingContext(contract, screens) {
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
            return sgrColorsCellAt(serve(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSgrColorsGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSgrColorsAdapter;
    const collected = await collectGradedPlans({ contracts: sgrColorsContracts, prefix: 'sgcolors',
        reset: sgrColorsReset, nonce: sgrColorsNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = sgrColorsContracts.find(entry => entry.caseId === item.caseId);
        const pinned = gradingModule.ntildeRunSgrColorsTermless(contract.caseId,
            sgrColorsRecordingContext(contract, item.screens));
        const { screens, ...recorded } = item;
        void screens;
        run.cases.push({ ...recorded, pinned });
    }
    return run;
}
