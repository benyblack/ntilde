// Worker-side driver for the line-editing qualification (editing.insert-lines, editing.delete-lines).
// Replays each contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED
// fixture bytes (four seed rows, the cursor move, then the edit) on a measured reset screen plus discriminating
// controls. Every read is journaled by the parent observer; the pinned termless probe then runs over the
// independently observed screens of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { lineEditContracts, lineEditNoncePrefix, lineEditReset, lineEditCellAt, validateLineEditScreen } from './line-edit-contracts.mjs';

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned fixture bytes (and nothing else): six feeds (four seed rows, the
 * cursor move and the edit), the cells read after the fourth and sixth and the cursor after the fifth. */
export function lineEditRecordingContext(contract, screens) {
    let fed = 0;
    const current = () => {
        if (fed < 4) throw new Error(`Pinned ${contract.caseId} read before its fixture.`);
        return screens[fed === 4 ? contract.beforeRead : fed === 5 ? contract.setupRead : contract.pinnedRead];
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
            const screen = screens[contract.setupRead];
            return { screenLines: screen.rows, totalLines: screen.rows, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            return lineEditCellAt(current(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectLineEditGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeLineEditAdapter;
    const collected = await collectGradedPlans({ contracts: lineEditContracts, prefix: 'ledt',
        reset: lineEditReset, nonce: lineEditNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = lineEditContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateLineEditScreen(screen);
        const context = lineEditRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunLineEditTermless(contract.caseId, context);
        if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
