// Worker-side driver for the erase-in-line qualification (erase.line.right, erase.line.left, erase.line.all).
// Replays each contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED
// fixture bytes (setup, then the erase sequence) on a measured reset screen plus discriminating controls. Every
// read is journaled by the parent observer; the pinned termless probe then runs over the independently observed
// screens of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { eraseLineContracts, eraseLineNoncePrefix, eraseLineReset, eraseLineCellAt, validateEraseLineScreen } from './erase-line-contracts.mjs';

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned fixture bytes (and nothing else): the first feed is the setup
 * (read from the setup screen), the second the erase sequence (read from the erased screen). */
export function eraseLineRecordingContext(contract, screens) {
    let fed = 0;
    const current = () => {
        if (fed === 0) throw new Error(`Pinned ${contract.caseId} read before its fixture.`);
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
            const screen = screens[contract.setupRead];
            return { screenLines: screen.rows, totalLines: screen.rows, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            return eraseLineCellAt(current(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectEraseLineGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeEraseLineAdapter;
    const collected = await collectGradedPlans({ contracts: eraseLineContracts, prefix: 'erln',
        reset: eraseLineReset, nonce: eraseLineNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = eraseLineContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateEraseLineScreen(screen);
        const context = eraseLineRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunEraseLineTermless(contract.caseId, context);
        if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
