// Worker-side driver for the ASCII/SGR qualification batch. Replays each
// contracted case's exact pinned fixture bytes through the owned fence, reads
// the resulting cells/flags through the parent-observed readScreen journal,
// and then executes the pinned termless probe (from the derived grading
// extension) against those independently observed screens.
import { asciiSgrContracts, asciiSgrNoncePrefix, asciiSgrReset, contractFeeds, contractReads, cellAt } from './ascii-sgr-contracts.mjs';

/** Serve pre-captured screens to a pinned termless probe, cross-checking that
 * the pinned code itself replays exactly the contracted fixture bytes. */
export function recordingContext(contract, screens) {
    const pinnedFeeds = contractFeeds(contract).slice(1);
    let feedIndex = 0, readIndex = 0, served;
    const serve = kind => {
        const step = contract.plan.filter(item => item.read)[readIndex];
        if (!step || step.read !== kind || readIndex >= screens.length) {
            throw new Error(`Pinned ${contract.caseId} read order differs from the contract.`);
        }
        if (!served) served = screens[readIndex];
        return served;
    };
    return {
        feed(text) {
            if (text !== pinnedFeeds[feedIndex]) {
                throw new Error(`Pinned ${contract.caseId} feed ${feedIndex} differs from the contract.`);
            }
            feedIndex++;
            served = undefined;
            readIndex = feedIndex - 1;
        },
        getText() {
            return serve('text').lines.join('\n');
        },
        getCell(row, col) {
            return cellAt(serve('cells'), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectAsciiSgrGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeGradingAdapter;
    const nonce = asciiSgrNoncePrefix + config.runId;
    const restore = asciiSgrReset + '\x1b[?1049l';
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    await owner('grading-start');
    // Keep the collection failure visible when restoration also fails.
    let primary, failed = false;
    try {
        const bindBarrier = await fence.write('\x1b[?1049h' + asciiSgrReset + nonce);
        await owner('grading-bind', bindBarrier);
        for (const contract of asciiSgrContracts) {
            const feeds = [], barriers = [], reads = [], screens = [];
            for (const step of contract.plan) {
                if (step.feed) {
                    feeds.push(step.feed);
                    barriers.push(await fence.write(step.feed));
                } else {
                    const observed = await owner('grading-read', barriers.at(-1));
                    reads.push({ kind: step.read, screen: observed.screen });
                    screens.push(observed.screen);
                }
            }
            if (feeds.length !== contractFeeds(contract).length || screens.length !== contractReads(contract)) {
                throw new Error(`Incomplete ${contract.caseId} graded fixture.`);
            }
            const pinned = gradingModule.ntildeRunTermless(contract.caseId, recordingContext(contract, screens));
            run.cases.push({ caseId: contract.caseId, feeds, barriers,
                reads: reads.map(read => ({ kind: read.kind, cursor: [read.screen.cursorRow, read.screen.cursorCol] })),
                pinned });
        }
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner('grading-finish', await fence.write(restore));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'Graded collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return run;
}
