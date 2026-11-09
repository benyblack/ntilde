// Worker-side driver for the selective-erasure qualification (erase.selective and editing.decsera). Replays each
// contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED seed, erase and
// finalizer bytes on a measured reset screen plus discriminating controls. Every read is journaled by the parent observer
// (stored cells, attributes, buffer state and per-cell protection); the pinned termless probe then runs over the
// independently observed screens of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { selectiveEraseContracts, selectiveEraseNoncePrefix, selectiveEraseReset, selectiveEraseCellAt, validateSelectiveEraseScreen,
    pinnedColumnsNote, pinnedSeedNote } from './selective-erase-contracts.mjs';

/** The feeds the pinned probe makes for each of its outcomes: it needs six columns before it feeds anything, feeds the seed and
 * (when the seed reads back as "PABCDZ") the erase, and always feeds the finalizer from a `finally` once it has started. */
export function expectedPinnedFeeds(contract, pinned) {
    if (pinned.note === pinnedColumnsNote) return [];
    if (pinned.note === pinnedSeedNote) return [contract.pinnedFeeds[0], contract.pinnedFeeds[2]];
    return [...contract.pinnedFeeds];
}

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned code itself
 * replays exactly the contracted pinned bytes in an allowed order (seed first; then the erase or the finalizer; the finalizer last)
 * and nothing else. Cells come from the observed screen of that moment: the seed screen after the seed, the erased screen after
 * the erase, the finalized screen after the finalizer. */
export function selectiveEraseRecordingContext(contract, screens) {
    const fed = [];
    const [seed, sequence, finalizer] = contract.pinnedFeeds;
    const current = () => {
        const last = fed.at(-1);
        if (last === seed) return screens[contract.setupRead];
        if (last === sequence) return screens[contract.pinnedRead];
        if (last === finalizer) return screens[contract.finalRead];
        throw new Error(`Pinned ${contract.caseId} read a cell before its fixture.`);
    };
    return {
        get cols() { return screens[contract.setupRead].cols; },
        get fedCount() { return fed.length; },
        get feeds() { return [...fed]; },
        feed(text) {
            const allowed = fed.length === 0 ? [seed] : fed.at(-1) === seed ? [sequence, finalizer] : fed.at(-1) === sequence ? [finalizer] : [];
            if (!allowed.includes(text)) throw new Error(`Pinned ${contract.caseId} feed ${fed.length} differs from the contract.`);
            fed.push(text);
        },
        getText() {
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getScrollback() {
            throw new Error(`Pinned ${contract.caseId} probe must not read the scrollback.`);
        },
        getCursor() {
            throw new Error(`Pinned ${contract.caseId} probe must not read the cursor.`);
        },
        getCell(row, col) {
            return selectiveEraseCellAt(current(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSelectiveEraseGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSelectiveEraseAdapter;
    const collected = await collectGradedPlans({ contracts: selectiveEraseContracts, prefix: 'sels',
        reset: selectiveEraseReset, nonce: selectiveEraseNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = selectiveEraseContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateSelectiveEraseScreen(screen);
        const context = selectiveEraseRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunSelectiveEraseTermless(contract.caseId, context);
        if (JSON.stringify(context.feeds) !== JSON.stringify(expectedPinnedFeeds(contract, pinned))) {
            throw new Error(`Pinned ${contract.caseId} did not replay exactly its contracted fixture.`);
        }
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinnedFed: context.feeds, pinned });
    }
    return run;
}
