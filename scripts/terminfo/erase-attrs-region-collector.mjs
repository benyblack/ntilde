// Worker-side driver for the erase-with-attributes / erase-with-margins qualification (erase.el-with-attrs, erase.ed-scroll-region).
// Replays each contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED fixture bytes (setup, the
// omitted-parameter erase, then the pinned `finally` feed) on a measured reset screen plus discriminating controls. Every read is
// journaled by the parent observer; the pinned termless probe then runs over the independently observed screens of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { eraseAttrsRegionContracts, eraseAttrsRegionNoncePrefix, eraseAttrsRegionReset, eraseAttrsRegionCellAt, eraseAttrsRegionScrollback,
    validateEraseAttrsRegionScreen } from './erase-attrs-region-contracts.mjs';

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned code itself replays
 * exactly the contracted pinned fixture bytes (and nothing else): every feed must equal the next pinned feed, and every read is served from
 * the screen observed after the feeds issued so far (the initial read before any feed, the setup read after the first, the erased read
 * after the second, the restored read after the `finally` feed). Cells, the cursor and the scrollback counts all come from that screen. */
export function eraseAttrsRegionRecordingContext(contract, screens) {
    let fed = 0;
    // Every feed must continue one of the pinned probe's possible feed paths: the full fixture, or the early return that still runs the
    // `finally` feed (an invalid setup), or no feed at all (the geometry gate).
    let candidates = contract.pinnedFeedPaths;
    const current = () => screens[contract.readsAtFeed[Math.min(fed, contract.readsAtFeed.length - 1)]];
    return {
        get cols() { return screens[contract.setupRead].cols; },
        get fedCount() { return fed; },
        /** Whether the pinned code has issued every feed of one of its possible paths. */
        get complete() { return candidates.some(path => path.length === fed); },
        feed(text) {
            const next = candidates.filter(path => path[fed] === text);
            if (next.length === 0) throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
            candidates = next;
            fed++;
        },
        getText() {
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getScrollback() {
            return eraseAttrsRegionScrollback(current());
        },
        getCursor() {
            if (fed === 0) throw new Error(`Pinned ${contract.caseId} read the cursor before its fixture.`);
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            if (fed === 0) throw new Error(`Pinned ${contract.caseId} read a cell before its fixture.`);
            return eraseAttrsRegionCellAt(current(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectEraseAttrsRegionGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeEraseAttrsRegionAdapter;
    const collected = await collectGradedPlans({ contracts: eraseAttrsRegionContracts, prefix: 'erar',
        reset: eraseAttrsRegionReset, nonce: eraseAttrsRegionNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = eraseAttrsRegionContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateEraseAttrsRegionScreen(screen);
        const context = eraseAttrsRegionRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunEraseAttrsRegionTermless(contract.caseId, context);
        if (!context.complete) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
