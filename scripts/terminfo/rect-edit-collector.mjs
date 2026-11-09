// Worker-side driver for the rectangle-editing qualification (editing.decfra, editing.decera, editing.deccra).
// Replays each contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED
// fixture bytes (the seed, the cursor move, then the edit) on a measured reset screen plus discriminating
// controls. Every read is journaled by the parent observer; the pinned termless probe then runs over the
// independently observed screens of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { rectEditContracts, rectEditNoncePrefix, rectEditReset, rectEditCellAt, validateRectEditScreen, pinnedNotes } from './rect-edit-contracts.mjs';

/** The pinned code may only feed the contracted pinned fixture bytes, in order. */
function requirePinnedFeed(contract, fed, text) {
    if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed]) {
        throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
    }
}

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned fixture bytes (and nothing else): three feeds (the seed, the
 * cursor move and the edit), the cells read after the first and third and the cursor after the second. */
export function rectEditRecordingContext(contract, screens) {
    let fed = 0;
    const readByFeed = [contract.beforeRead, contract.setupRead, contract.pinnedRead];
    const setupScreen = () => screens[contract.setupRead];
    const current = () => {
        if (fed < 1) throw new Error(`Pinned ${contract.caseId} read before its fixture.`);
        return screens[readByFeed[Math.min(fed, readByFeed.length) - 1]];
    };
    return {
        get cols() { return setupScreen().cols; },
        get fedCount() { return fed; },
        feed: text => { requirePinnedFeed(contract, fed, text); fed++; },
        getText: () => { throw new Error(`Pinned ${contract.caseId} probe must not read text.`); },
        getScrollback: () => ({ screenLines: setupScreen().rows, totalLines: setupScreen().rows, viewportY: 0 }),
        getCursor: () => ({ x: current().cursorCol, y: current().cursorRow, visible: current().cursorVisible }),
        getCell: (row, col) => rectEditCellAt(current(), row, col),
    };
}

/** How many pinned feeds a complete or validly cut-short pinned replay consumes: the geometry guard runs before any feed, an unread seed
 * stops after the seed feed, a wrong setup cursor after the cursor feed, and every other result replays all three. */
export function expectedFedCount(contract, pinned) {
    const notes = pinnedNotes[contract.mode];
    if (pinned.observation?.outcome !== 'inconclusive') return contract.pinnedFeeds.length;
    if (pinned.note === notes.geometry) return 0;
    if (pinned.note === notes.seed) return 1;
    if (pinned.note === notes.cursor) return 2;
    return contract.pinnedFeeds.length;
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectRectEditGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeRectEditAdapter;
    const collected = await collectGradedPlans({ contracts: rectEditContracts, prefix: 'rect',
        reset: rectEditReset, nonce: rectEditNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = rectEditContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateRectEditScreen(screen);
        const context = rectEditRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunRectEditTermless(contract.caseId, context);
        if (context.fedCount !== expectedFedCount(contract, pinned)) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
