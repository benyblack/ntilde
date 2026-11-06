// Worker-side driver for the SGR reset/standard-color qualification batch.
// Replays each contracted case's exact pinned fixture bytes through the owned
// fence, reads the resulting stored attributes and palette indices through the
// parent-observed readScreen journal, and then executes the pinned termless
// probe (from the derived SGR-colors grading extension) against those
// independently observed screens.
import { sgrColorsContracts, sgrColorsNoncePrefix, sgrColorsReset,
    sgrColorsFeeds, sgrColorsReads, sgrColorsCellAt } from './sgr-colors-contracts.mjs';

/** Serve pre-captured screens to a pinned termless probe, cross-checking that
 * the pinned code itself replays exactly the contracted fixture bytes. */
export function sgrColorsRecordingContext(contract, screens) {
    const pinnedFeeds = sgrColorsFeeds(contract).slice(1);
    let feedIndex = 0, readIndex = 0, served;
    const serve = () => {
        if (readIndex >= screens.length) throw new Error(`Pinned ${contract.caseId} read order differs from the contract.`);
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
            throw new Error(`Pinned ${contract.caseId} probe must not read text.`);
        },
        getCell(row, col) {
            return sgrColorsCellAt(serve(), row, col);
        },
    };
}

/** Run every contracted case serially with parent-journaled controls. */
export async function collectSgrColorsGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeSgrColorsAdapter;
    const nonce = sgrColorsNoncePrefix + config.runId;
    const restore = sgrColorsReset + '\x1b[?1049l';
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    await owner('sgcolors-start');
    // Keep the collection failure visible when restoration also fails.
    let primary, failed = false;
    try {
        const bindBarrier = await fence.write('\x1b[?1049h' + sgrColorsReset + nonce);
        await owner('sgcolors-bind', bindBarrier);
        for (const contract of sgrColorsContracts) {
            const feeds = [], barriers = [], reads = [], screens = [];
            for (const step of contract.plan) {
                if (step.feed) {
                    feeds.push(step.feed);
                    barriers.push(await fence.write(step.feed));
                } else {
                    const observed = await owner('sgcolors-read', barriers.at(-1));
                    reads.push({ kind: step.read, screen: observed.screen });
                    screens.push(observed.screen);
                }
            }
            if (feeds.length !== sgrColorsFeeds(contract).length || screens.length !== sgrColorsReads(contract)) {
                throw new Error(`Incomplete ${contract.caseId} graded fixture.`);
            }
            const pinned = gradingModule.ntildeRunSgrColorsTermless(contract.caseId,
                sgrColorsRecordingContext(contract, screens));
            run.cases.push({ caseId: contract.caseId, feeds, barriers,
                reads: reads.map(read => ({ kind: read.kind, cursor: [read.screen.cursorRow, read.screen.cursorCol] })),
                pinned });
        }
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner('sgcolors-finish', await fence.write(restore));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'Sgr-colors collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return run;
}
