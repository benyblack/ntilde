// Worker-side driver for the saved-state / reset qualification (modes.alt-screen.exit, modes.altscreen-47,
// modes.altscreen-1047, reset.sgr, reset.decaln). Replays each contracted case's exact plan through the owned fence
// (shared graded-plan driver): the PINNED fixture bytes on a measured, bank-normalized main screen plus labeled
// controls and effect refinements. Every read is journaled by the parent observer; the pinned termless probe then
// runs over the independently observed screens of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { savedResetContracts, savedResetNoncePrefix, savedResetReset, projectSavedResetCells, validateSavedResetScreen } from './saved-reset-contracts.mjs';

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned fixture bytes (and nothing else; the pinned trailing write may also follow an
 * early return). Each pinned read is served from
 * the journaled screen that followed the pinned feed it came after (contract.pinnedReads maps fed count to read). */
export function savedResetRecordingContext(contract, screens) {
    let fed = 0;
    const projectors = new Map();
    const current = () => {
        const index = contract.pinnedReads[fed];
        if (index === undefined) throw new Error(`Pinned ${contract.caseId} read after feed ${fed} has no observed screen.`);
        return screens[index];
    };
    const cellOf = screen => {
        if (!projectors.has(screen)) projectors.set(screen, projectSavedResetCells(screen));
        return projectors.get(screen);
    };
    return {
        get cols() { return screens[0].cols; },
        get fedCount() { return fed; },
        feed(text) {
            if (fed >= contract.pinnedFeeds.length || (text !== contract.pinnedFeeds[fed] && text !== contract.pinnedTrailing)) {
                throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
            }
            fed++;
        },
        getText() {
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getScrollback() {
            const screen = screens[0];
            return { screenLines: screen.rows, totalLines: screen.rows, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            return cellOf(current())(row, col);
        },
        getMode(name) {
            if (name !== 'altScreen') throw new Error(`Pinned ${contract.caseId} read unexpected mode ${name}.`);
            return current().bufferState.alternateScreen;
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSavedResetGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSavedResetAdapter;
    const collected = await collectGradedPlans({ contracts: savedResetContracts, prefix: 'svrs',
        reset: savedResetReset, nonce: savedResetNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = savedResetContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateSavedResetScreen(screen);
        const context = savedResetRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunSavedResetTermless(contract.caseId, context);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinnedFed: context.fedCount, pinned });
    }
    return run;
}
