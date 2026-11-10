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

/** One plan step of one case. A capture step is a fenced exchange whose reply the next read journals; a feed step clears it. */
async function runStep(step, record, { fence, owner, caseId }) {
    if (step.feed) {
        record.feeds.push(step.feed);
        if (step.capture) {
            record.report = await fence.checksumReport(step.feed);
            record.barriers.push(record.report.barrier);
        } else {
            record.report = null;
            record.barriers.push(await fence.write(step.feed));
        }
        return;
    }
    const isReport = step.read === 'report';
    if (isReport && record.report === null) throw new Error(`${caseId} report read without an exchange.`);
    const observed = await owner('csum-read', record.barriers.at(-1), isReport ? { report: record.report } : {});
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(isReport ? { report: record.report } : {}) });
    record.screens.push(observed.screen);
    record.replies.push(isReport ? record.report.reply : null);
    record.report = null;
}

/** Every step of one case, serially: each write, exchange and read must settle before the next changes state. */
async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], screens: [], replies: [], report: null };
    for (const step of contract.plan) await runStep(step, record, { ...io, caseId: contract.caseId }); // NOSONAR - serial by design
    if (record.feeds.length !== rectChecksumFeeds(contract).length || record.screens.length !== rectChecksumReads(contract)) {
        throw new Error(`Incomplete ${contract.caseId} rectangle-checksum fixture.`);
    }
    const { feeds, barriers, reads, screens, replies } = record;
    return { caseId: contract.caseId, feeds, barriers, reads, screens, replies };
}

/** Restore the screen whatever happened, aggregating a collection failure with a restoration failure instead of dropping either. */
async function collectAndRestore({ fence, owner, nonce }) {
    const cases = [];
    let primary, failed = false;
    try {
        await owner('csum-bind', await fence.write('\x1b[?1049h' + rectChecksumReset + nonce));
        for (const contract of rectChecksumContracts) cases.push(await collectCase(contract, { fence, owner })); // NOSONAR - serial by design
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner('csum-finish', await fence.write(rectChecksumReset + '\x1b[?1049l'));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'csum collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return cases;
}

/** Run the contracted case serially with parent-journaled reads and replies. */
export async function collectRectChecksumGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeRectChecksumAdapter;
    await owner('csum-start');
    const cases = await collectAndRestore({ fence, owner, nonce: rectChecksumNoncePrefix + config.runId });
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
