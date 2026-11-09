// Worker-side driver for the scrolling/history qualification (the nine pinned scrollback.* cases). Replays each
// contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED fixture bytes on a
// measured reset main screen plus discriminating controls. Every read is journaled by the parent observer with the
// complete viewport, margins, active screen and the actual local scrollback lines; the pinned termless probe then
// runs over the independently observed states of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { makeScrollbackContracts, scrollbackNoncePrefix, scrollbackReset, scrollbackCellAt, validateScrollbackScreen } from './scrollback-contracts.mjs';

/** The suite starts from the pristine private main screen: blank viewport, empty history, default margins and modes. */
export function requirePristineScrollbackStart(screen) {
    const state = screen.bufferState;
    if (!state || !screen.history || screen.lines.some(line => line !== '') || screen.cursorRow !== 0 || screen.cursorCol !== 0
        || state.alternateScreen || state.scrollTop !== 0 || state.scrollBottom !== screen.rows - 1 || state.originMode
        || state.insertMode || state.pendingWrap || state.scrollbackLines !== 0 || screen.history.lines.length !== 0) {
        throw new Error('Scrollback fixtures require a pristine private main screen with empty history.');
    }
}

/** Serve the pre-captured states of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned fixture bytes (and nothing else; an early return may stop sooner, feeding only the declared cleanup). A read is served only at a
 * feed count where the contract journaled a state. */
export function scrollbackRecordingContext(contract, screens) {
    let fed = 0, cleaned = false;
    const current = () => {
        const index = contract.pinnedReadAt[fed];
        if (index === undefined) throw new Error(`Pinned ${contract.caseId} read at feed ${fed} without an observed state.`);
        return screens[index];
    };
    return {
        get cols() { return screens[0].cols; },
        get fedCount() { return fed; },
        /** Whether the pinned code replayed its whole fixture, refused before feeding anything (its geometry or empty-history guard), or stopped
         * early and fed only its declared cleanup (a `finally` reset). */
        get complete() { return fed === contract.pinnedFeeds.length || (fed === 0 && !cleaned) || (cleaned && fed < contract.pinnedFeeds.length); },
        feed(text) {
            if (fed < contract.pinnedFeeds.length && text === contract.pinnedFeeds[fed]) { fed++; return; }
            // A pinned probe that finds its controls invalid returns early; its `finally` still feeds the cleanup reset.
            if (contract.cleanupFeed !== undefined && text === contract.cleanupFeed && !cleaned) { cleaned = true; return; }
            throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
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
            return scrollbackCellAt(current(), row, col);
        },
        getMode(name) {
            if (name !== 'altScreen') throw new Error(`Pinned ${contract.caseId} read unmodeled mode ${name}.`);
            return current().bufferState.alternateScreen;
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectScrollbackGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeScrollbackAdapter;
    let contracts;
    const nonce = scrollbackNoncePrefix + config.runId;
    const collected = await collectGradedPlans({ prefix: 'scrl', reset: scrollbackReset, bind: scrollbackReset + nonce,
        finish: scrollbackReset, nonce, fence, owner, planFeeds, planReads,
        contractsFor: screen => {
            requirePristineScrollbackStart(screen);
            contracts = makeScrollbackContracts(screen.rows, screen.cols);
            return contracts;
        } });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = contracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateScrollbackScreen(screen);
        const context = scrollbackRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunScrollbackTermless(contract.caseId, context);
        if (!context.complete) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
