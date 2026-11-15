// Worker-side driver for the selection-color qualification (extensions.osc17-highlight-bg, osc19-highlight-fg, osc117-reset-highlight-bg
// and osc119-reset-highlight-fg). Replays each case's exact plan through the owned fence: every experiment's scene, then the graded
// writes and the exact query bytes (the pinned BEL spelling where the pinned code issues it, the ST spelling elsewhere). A query step is
// one fenced exchange that retains the raw OSC reply (or its explicit absence) before the DA barrier; every read is journaled by the
// parent observer with the replies asked since the previous read. The pinned termless probe then runs over its own experiment's served
// replies (set cases) or served cell (reset cases). Selection colors are runtime session state and survive both the alternate-screen
// exit and DECSTR, so the finish write clears them explicitly; after a fatal input error only a bounded best-effort write is attempted,
// which can neither revive the fence nor establish restoration.
import { selectionColorsContracts, selectionColorsNoncePrefix, selectionColorsBind, selectionColorsFinish, selectionColorsFeeds, selectionColorsReads, validateSelectionColorsScreen } from './selection-colors-contracts.mjs';

/** The pinned getCell projection of one observed cell: the collector and the grading replay share this single projection, so the
 * pinned result's response is reproducible from the retained journal alone. The reset cases' pinned code reads only the character. */
export const pinnedCellAt = (screen, row, col) => ({ char: (screen.lines[row] ?? ' ')[col] ?? ' ' });

/** The pinned experiment's screen, by role, from an ordered screen list (the worker's per-case screens or the parent's read frames' screens). */
export function pinnedScreen(contract, screens) {
    return screens[contract.roles.indexOf(contract.pinnedRoles[0])];
}

/** The replies of the pinned experiment's reads, in order: the one default-state query of the set cases' pinned code. */
export function pinnedReplies(contract, reads) {
    return [reads[contract.roles.indexOf(contract.pinnedRoles[0])].queries[0].reply];
}

/** Serve the journaled replies and screen of the pinned experiment to the pinned termless probe, cross-checking that the pinned code
 * itself issues exactly the contracted bytes, each as the contracted kind (a capture or a plain feed), in order, and nothing else,
 * and that it reads exactly the contracted cells. */
export function selectionColorsRecordingContext(contract, replies, screen) {
    let fed = 0, captured = 0, cells = 0;
    const expect = (text, kind) => {
        if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed] || kind !== contract.pinnedKinds[fed]) {
            throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
        }
        fed++;
    };
    return {
        get fedCount() { return fed; },
        feed: text => expect(text, 'feed'),
        feedCapture: text => {
            expect(text, 'capture');
            return replies[captured++]?.raw ?? '';
        },
        getCell: (row, col) => {
            if (row !== 0 || col !== 0 || !screen) throw new Error(`Pinned ${contract.caseId} must read cell (0,0) of its own read.`);
            cells++;
            return pinnedCellAt(screen, row, col);
        },
        get cellsRead() { return cells; },
        getCursor: () => { throw new Error(`Pinned ${contract.caseId} probe must not read the cursor.`); },
        getText: () => { throw new Error(`Pinned ${contract.caseId} probe must not read text.`); },
    };
}

/** One plan step of one case. A query step is a fenced exchange whose reply the next read journals; a feed step only orders. */
async function runStep(step, record, { fence, owner, contract }) {
    if (step.feed !== undefined) {
        record.feeds.push(step.feed);
        if (step.query) {
            const exchange = await fence.colorExchange(step.feed, step.query);
            record.barriers.push(exchange.barrier);
            record.pending.push({ spec: step.query, barrier: exchange.barrier, reply: exchange.reply });
        } else record.barriers.push(await fence.write(step.feed));
        return;
    }
    const [experimentId, stageId] = contract.roles[record.screens.length].split('/');
    const queries = record.pending;
    const observed = await owner('selc-read', record.barriers.at(-1), { caseId: contract.caseId, experimentId, stageId, queries });
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], queries });
    record.screens.push(observed.screen);
    record.pending = [];
}

/** Every step of one case, serially: each write, exchange and read must settle before the next changes state. */
async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], screens: [], pending: [] };
    for (const step of contract.plan) await runStep(step, record, { ...io, contract }); // NOSONAR - serial by design
    if (record.feeds.length !== selectionColorsFeeds(contract).length || record.screens.length !== selectionColorsReads(contract) || record.pending.length) {
        throw new Error(`Incomplete ${contract.caseId} selection-color fixture.`);
    }
    for (const screen of record.screens) validateSelectionColorsScreen(screen);
    const { feeds, barriers, reads, screens } = record;
    return { caseId: contract.caseId, feeds, barriers, reads, screens };
}

/** Restore the runtime colors and the screen whatever happened, aggregating a collection failure with a restoration failure. */
async function collectAndRestore({ fence, owner, nonce }) {
    const cases = [];
    let primary, failed = false;
    try {
        await owner('selc-bind', await fence.write(selectionColorsBind + nonce));
        for (const contract of selectionColorsContracts) cases.push(await collectCase(contract, { fence, owner })); // NOSONAR - serial by design
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        // A failed fence never requests or accepts another barrier: the write is a bounded best-effort cleanup that the receipt cannot count.
        if (fence.failed) await fence.writeCleanup(selectionColorsFinish);
        else await owner('selc-finish', await fence.write(selectionColorsFinish));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'selc collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return cases;
}

/** Verify and attach the pinned result of one collected case: the pinned code runs over its own served evidence and its
 * consumption must be exactly the contracted fixture (the set cases' probe stops after its first capture when the
 * original reply is missing or malformed; the reset cases' probe consumes its one feed and one cell read in every run). */
function pinnedResultOf(contract, item, gradingModule) {
    const context = contract.isResetCase
        ? selectionColorsRecordingContext(contract, [], pinnedScreen(contract, item.screens))
        : selectionColorsRecordingContext(contract, pinnedReplies(contract, item.reads), null);
    const pinned = gradingModule.ntildeRunSelectionColorsTermless(contract.caseId, context);
    const stopped = contract.isResetCase ? false
        : item.reads[0].queries[0].reply === null && ['no-response', 'invalid-reply'].includes(pinned.observation?.reason ?? '');
    if (contract.isResetCase && pinned.notTested?.reason !== 'no-semantic-observable') throw new Error(`Pinned ${contract.caseId} did not report its no-semantic-observable result.`);
    if (!contract.isResetCase && pinned.observation?.evidence !== 'query') throw new Error(`Pinned ${contract.caseId} did not grade from its query.`);
    if (!stopped && context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
    if (contract.isResetCase && context.cellsRead !== 1) throw new Error(`Pinned ${contract.caseId} did not read its cell.`);
    return pinned;
}

/** Run the contracted cases serially with parent-journaled reads, replies and screens. */
export async function collectSelectionColorsGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSelectionColorsAdapter;
    await owner('selc-start');
    const cases = await collectAndRestore({ fence, owner, nonce: selectionColorsNoncePrefix + config.runId });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of cases) {
        const contract = selectionColorsContracts.find(entry => entry.caseId === item.caseId);
        const pinned = pinnedResultOf(contract, item, gradingModule);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
