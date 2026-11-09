// Worker-side driver for the character-editing qualification (editing.insert-chars, editing.delete-chars,
// editing.repeat-char). Replays each contracted case's exact plan through the owned fence (shared graded-plan
// driver): the PINNED fixture bytes on a measured reset screen plus discriminating controls. Every read is
// journaled by the parent observer; the pinned termless probe then runs over the independently observed screens
// of its own fixture.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { charEditContracts, charEditNoncePrefix, charEditReset, charEditCellAt, validateCharEditScreen } from './char-edit-contracts.mjs';

/** Serve the pre-captured screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned
 * code itself replays exactly the contracted pinned feeds (and nothing else): after feed N the probe reads the screen
 * the contract recorded for that feed (`pinnedReads[N - 1]`). */
export function charEditRecordingContext(contract, screens) {
    let fed = 0;
    const current = () => {
        if (fed === 0) throw new Error(`Pinned ${contract.caseId} read before its fixture.`);
        return screens[contract.pinnedReads[fed - 1]];
    };
    return {
        get cols() { return screens[contract.pinnedReads[0]].cols; },
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
            const screen = screens[contract.pinnedReads[0]];
            return { screenLines: screen.rows, totalLines: screen.rows, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            return charEditCellAt(current(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectCharEditGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeCharEditAdapter;
    const collected = await collectGradedPlans({ contracts: charEditContracts, prefix: 'ched',
        reset: charEditReset, nonce: charEditNoncePrefix + config.runId, fence, owner,
        planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = charEditContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateCharEditScreen(screen);
        const context = charEditRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunCharEditTermless(contract.caseId, context);
        // A pinned probe returns early (inconclusive) when its seed, cursor or setup was not measured, so only a graded
        // result must have replayed the complete fixture; every fed byte string was already checked against the contract.
        if (pinned.observation?.outcome !== 'inconclusive' && context.fedCount !== contract.pinnedFeeds.length) {
            throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        }
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
