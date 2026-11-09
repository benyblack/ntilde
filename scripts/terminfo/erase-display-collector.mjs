// Worker-side driver for the erase-in-display qualification (erase.screen.below, erase.screen.above, erase.screen.all).
// Replays each contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED
// fixture bytes (setup, then the erase sequence) on a measured reset screen plus discriminating controls. Every
// read is journaled by the parent observer; the pinned termless probe then runs over the independently observed
// screens of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { eraseDisplayContracts, eraseDisplayNoncePrefix, eraseDisplayReset, eraseDisplayCellAt, eraseDisplayScrollback,
    validateEraseDisplayScreen } from './erase-display-contracts.mjs';

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned fixture bytes (and nothing else): the initial scrollback read
 * precedes every feed (served from the initial blank screen), the first feed is the setup (read from the setup screen)
 * and the second the erase sequence (read from the erased screen). Cells, the cursor and the scrollback counts all come
 * from the same observed screen of that moment. */
export function eraseDisplayRecordingContext(contract, screens) {
    let fed = 0;
    const current = () => {
        if (fed === 0) return screens[contract.initialRead];
        return screens[fed === 1 ? contract.setupRead : contract.pinnedRead];
    };
    return {
        get cols() { return screens[contract.setupRead].cols; },
        get fedCount() { return fed; },
        feed(text) {
            if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed]) {
                throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
            }
            fed++;
        },
        getText() {
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getScrollback() {
            return eraseDisplayScrollback(current());
        },
        getCursor() {
            if (fed === 0) throw new Error(`Pinned ${contract.caseId} read the cursor before its fixture.`);
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            if (fed === 0) throw new Error(`Pinned ${contract.caseId} read a cell before its fixture.`);
            return eraseDisplayCellAt(current(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectEraseDisplayGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeEraseDisplayAdapter;
    const collected = await collectGradedPlans({ contracts: eraseDisplayContracts, prefix: 'erdi',
        reset: eraseDisplayReset, nonce: eraseDisplayNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = eraseDisplayContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateEraseDisplayScreen(screen);
        const context = eraseDisplayRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunEraseDisplayTermless(contract.caseId, context);
        if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
