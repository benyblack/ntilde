// Worker-side driver for the erase-character / saved-lines qualification (erase.character, erase.screen.scrollback).
// Replays each contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED fixture bytes on a measured
// reset main screen plus discriminating controls. Every read is journaled by the parent observer with the complete viewport, margins,
// active screen, stored cell details and the actual local scrollback lines; the pinned termless probe then runs over the independently
// observed states of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { makeEraseCharsHistoryContracts, eraseCharsHistoryNoncePrefix, eraseCharsHistoryReset, eraseCharsHistoryCellAt,
    validateEraseCharsHistoryScreen } from './erase-chars-history-contracts.mjs';
import { requirePristineScrollbackStart } from './scrollback-collector.mjs';

/** Serve the pre-captured states of the pinned fixture to a pinned termless probe, cross-checking that the pinned code itself
 * replays exactly the contracted pinned fixture bytes (and nothing else; an early return may stop sooner). A read is served at
 * the feed count where the contract journaled a state: before any feed the initial state, after feed N the state read after the
 * N-th pinned feed. */
export function eraseCharsHistoryRecordingContext(contract, screens) {
    let fed = 0;
    const current = () => {
        const index = contract.pinnedReadAt[fed];
        if (index === undefined) throw new Error(`Pinned ${contract.caseId} read at feed ${fed} without an observed state.`);
        return screens[index];
    };
    return {
        get cols() { return screens[contract.pinnedReadAt[0]].cols; },
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
        // The pinned totalLines is the screen's rows plus its retained history lines.
        getScrollback() {
            const screen = current();
            return { screenLines: screen.rows, totalLines: screen.rows + screen.bufferState.scrollbackLines, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            return eraseCharsHistoryCellAt(current(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectEraseCharsHistoryGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeEraseCharsHistoryAdapter;
    let contracts;
    const nonce = eraseCharsHistoryNoncePrefix + config.runId;
    const collected = await collectGradedPlans({ prefix: 'echh', reset: eraseCharsHistoryReset, bind: eraseCharsHistoryReset + nonce,
        finish: eraseCharsHistoryReset, nonce, fence, owner, planFeeds, planReads,
        contractsFor: screen => {
            requirePristineScrollbackStart(screen);
            contracts = makeEraseCharsHistoryContracts(screen.rows, screen.cols);
            return contracts;
        } });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = contracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateEraseCharsHistoryScreen(screen);
        const context = eraseCharsHistoryRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunEraseCharsHistoryTermless(contract.caseId, context);
        // A pinned probe returns early (inconclusive) when its geometry, seed or history was not measured, so only a graded result
        // must have replayed the complete fixture; every fed byte string was already checked against the contract.
        if (pinned.observation?.outcome !== 'inconclusive' && context.fedCount !== contract.pinnedFeeds.length) {
            throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        }
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
