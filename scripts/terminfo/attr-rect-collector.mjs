// Worker-side driver for the attribute-rectangle qualification (editing.deccara, editing.decrara, editing.decsace). Replays each
// contracted case's exact plan through the owned fence (shared graded-plan driver): the PINNED fixture bytes on a measured reset screen
// plus labeled controls. Every read is journaled by the parent observer; the pinned termless probe then runs over the independently
// observed screens of its own fixture, and the operation cases run it a second, separately labeled time over the same bytes taken with
// the rectangle extent selected first.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { attrRectContracts, attrRectNoncePrefix, attrRectReset, attrRectScreenText, validateAttrRectScreen } from './attr-rect-contracts.mjs';
import { projectSavedResetCells } from './saved-reset-contracts.mjs';

/** Serve the journaled screens of the pinned fixture to a pinned termless probe, cross-checking that the pinned code itself replays
 * exactly the contracted pinned feeds and nothing else. After pinned feed N the probe reads the screen `reads[N - 1]` names;
 * `reads` defaults to the contract's pinned reads and is the rectangle-experiment reads for the labeled second run. */
export function attrRectRecordingContext(contract, screens, reads = contract.pinnedReads) {
    let fed = 0;
    const projectors = new Map();
    const current = () => {
        if (fed === 0) throw new Error(`Pinned ${contract.caseId} read before its fixture.`);
        const index = reads[fed - 1];
        if (index === undefined) throw new Error(`Pinned ${contract.caseId} read after feed ${fed} has no observed screen.`);
        return screens[index];
    };
    const cellOf = screen => {
        if (!projectors.has(screen)) projectors.set(screen, projectSavedResetCells(screen));
        return projectors.get(screen);
    };
    return {
        get cols() { return screens[reads[0]].cols; },
        get fedCount() { return fed; },
        feed(text) {
            if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed]) throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
            fed++;
        },
        getText() {
            return attrRectScreenText(current());
        },
        getScrollback() {
            const screen = screens[reads[0]];
            return { screenLines: screen.rows, totalLines: screen.rows, viewportY: 0 };
        },
        getCursor() {
            const screen = current();
            return { x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible };
        },
        getCell(row, col) {
            return cellOf(current())(row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectAttrRectGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeAttrRectAdapter;
    const collected = await collectGradedPlans({ contracts: attrRectContracts, prefix: 'atrc',
        reset: attrRectReset, nonce: attrRectNoncePrefix + config.runId, fence, owner, planFeeds, planReads });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = attrRectContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateAttrRectScreen(screen);
        const context = attrRectRecordingContext(contract, item.screens);
        const pinned = gradingModule.ntildeRunAttrRectTermless(contract.caseId, context);
        // A pinned probe returns early (inconclusive) when its seed, cursor or setup was not measured, so only a graded result must
        // have replayed the complete fixture; every fed byte string was already checked against the contract.
        if (pinned.observation?.outcome !== 'inconclusive' && context.fedCount !== contract.pinnedFeeds.length) {
            throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        }
        const { caseId, feeds, barriers, reads } = item;
        const record = { caseId, feeds, barriers, reads, pinnedFed: context.fedCount, pinned };
        if (contract.rectanglePinnedReads) {
            const rectangleContext = attrRectRecordingContext(contract, item.screens, contract.rectanglePinnedReads);
            record.pinnedRectangle = gradingModule.ntildeRunAttrRectTermless(contract.caseId, rectangleContext);
            record.pinnedRectangleFed = rectangleContext.fedCount;
        }
        run.cases.push(record);
    }
    return run;
}
