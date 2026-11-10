// Worker-side driver for the rectangle-checksum qualification (editing.decrqcra). Replays the case's exact plan through the
// owned fence: the PINNED seed and request on a measured reset screen, then the graded controls. A request step is one fenced
// exchange that retains the raw DCS reply (or its explicit absence) before the DA barrier; every read is journaled by the parent
// observer together with that reply. The pinned termless probe then runs over the reply of its own fixture.
import { rectChecksumContracts, rectChecksumNoncePrefix, rectChecksumReset, rectChecksumFeeds, rectChecksumReads,
    validateRectChecksumScreen } from './rect-checksum-contracts.mjs';

/** Serve the journaled reply of the pinned exchange to the pinned termless probe, cross-checking that the pinned code itself feeds
 * exactly the contracted bytes (the seed, then the request) and nothing else. An absent reply is the empty capture. */
export function rectChecksumRecordingContext(contract, replies) {
    let fed = 0;
    const expect = text => {
        if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed]) throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
        fed++;
    };
    return {
        get fedCount() { return fed; },
        feed: text => {
            if (fed !== 0) throw new Error(`Pinned ${contract.caseId} feeds its request without a capture.`);
            expect(text);
        },
        feedCapture: text => {
            if (fed !== 1) throw new Error(`Pinned ${contract.caseId} captures before its seed.`);
            expect(text);
            return replies[contract.pinnedRead]?.raw ?? '';
        },
        getCell: () => { throw new Error(`Pinned ${contract.caseId} probe must not read cells.`); },
        getCursor: () => { throw new Error(`Pinned ${contract.caseId} probe must not read the cursor.`); },
        getText: () => { throw new Error(`Pinned ${contract.caseId} probe must not read text.`); },
    };
}

/** Run the contracted case serially with parent-journaled reads and replies. */
export async function collectRectChecksumGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeRectChecksumAdapter;
    const nonce = rectChecksumNoncePrefix + config.runId;
    const cases = [];
    await owner('csum-start');
    let primary, failed = false;
    try {
        await owner('csum-bind', await fence.write('\x1b[?1049h' + rectChecksumReset + nonce));
        for (const contract of rectChecksumContracts) {
            const feeds = [], barriers = [], reads = [], screens = [], replies = [];
            let report = null;
            for (const step of contract.plan) {
                if (step.feed && step.capture) {
                    feeds.push(step.feed);
                    report = await fence.checksumReport(step.feed); // serial by design: the reply orders against the next read (NOSONAR)
                    barriers.push(report.barrier);
                } else if (step.feed) {
                    feeds.push(step.feed);
                    report = null;
                    barriers.push(await fence.write(step.feed)); // serial by design: each write must settle before the next (NOSONAR)
                } else {
                    const extra = step.read === 'report' ? { report } : {};
                    if (step.read === 'report' && report === null) throw new Error(`${contract.caseId} report read without an exchange.`);
                    const observed = await owner('csum-read', barriers.at(-1), extra); // serial by design: reads journal between writes (NOSONAR)
                    reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(step.read === 'report' ? { report } : {}) });
                    screens.push(observed.screen);
                    replies.push(step.read === 'report' ? report.reply : null);
                    report = null;
                }
            }
            if (feeds.length !== rectChecksumFeeds(contract).length || screens.length !== rectChecksumReads(contract)) {
                throw new Error(`Incomplete ${contract.caseId} rectangle-checksum fixture.`);
            }
            cases.push({ caseId: contract.caseId, feeds, barriers, reads, screens, replies });
        }
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner('csum-finish', await fence.write(rectChecksumReset + '\x1b[?1049l'));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'csum collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of cases) {
        const contract = rectChecksumContracts.find(entry => entry.caseId === item.caseId);
        for (const screen of item.screens) validateRectChecksumScreen(screen);
        const context = rectChecksumRecordingContext(contract, item.replies);
        const pinned = gradingModule.ntildeRunRectChecksumTermless(contract.caseId, context);
        if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} did not replay its complete fixture.`);
        const { caseId, feeds, barriers, reads } = item;
        run.cases.push({ caseId, feeds, barriers, reads, pinned });
    }
    return run;
}
