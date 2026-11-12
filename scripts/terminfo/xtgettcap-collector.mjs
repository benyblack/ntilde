// Worker-side driver for the XTGETTCAP qualification (device.xtgettcap). Replays the case's exact plan through the owned fence:
// the PINNED request on a measured seeded alternate screen, then the graded controls. A request step is one fenced exchange that
// retains every raw DCS reply (or their explicit absence) before the DA barrier; every read is journaled by the parent observer
// together with those replies. The pinned termless probe then runs over the replies of its own exchange, and the pinned terminal
// path over the same journaled bytes.
import { xtgettcapContracts, xtgettcapNoncePrefix, xtgettcapReset, xtgettcapSeed, xtgettcapPlanSeed,
    validateXtgettcapFeeds } from './xtgettcap-contracts.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';
import { parseXtgettcapFrame } from './xtgettcap-report.mjs';

/** Serve the journaled exchange of the pinned request to the pinned probe contexts, cross-checking that the pinned code itself feeds
 * exactly the contracted bytes and nothing else. An absent reply is the empty capture. */
export function xtgettcapRecordingContext(contract, exchange) {
    let fed = 0, asked = 0;
    const expect = text => {
        if (fed >= contract.pinnedFeeds.length || text !== contract.pinnedFeeds[fed]) throw new Error(`Pinned ${contract.caseId} feed ${fed} differs from the contract.`);
        fed++;
    };
    return {
        get fedCount() { return fed; },
        get askedCount() { return asked; },
        feed: text => { throw new Error(`Pinned ${contract.caseId} must not plain-feed ${JSON.stringify(text)}.`); },
        feedCapture: text => {
            if (fed !== 0) throw new Error(`Pinned ${contract.caseId} captures more than once.`);
            expect(text);
            return exchange === null ? '' : exchange.replies.join('');
        },
        write: text => { throw new Error(`Pinned ${contract.caseId} must not write ${JSON.stringify(text)}.`); },
        // The real queryWithSentinelOutcome is async: every refusal arrives as a rejected promise, never a synchronous throw.
        queryWithSentinelOutcome: (text, pattern) => {
            try {
                if (asked !== 0 || fed !== 0) throw new Error(`Pinned ${contract.caseId} asked the terminal path outside its fixture.`);
                expect(text);
                asked++;
                const raw = exchange === null ? '' : exchange.replies.join('') + exchange.barrier;
                return Promise.resolve({ reason: 'reply', raw, match: pattern.exec(raw) });
            } catch (error) { return Promise.reject(error); }
        },
        getCell: () => { throw new Error(`Pinned ${contract.caseId} probe must not read cells.`); },
        getCursor: () => { throw new Error(`Pinned ${contract.caseId} probe must not read the cursor.`); },
        getText: () => { throw new Error(`Pinned ${contract.caseId} probe must not read text.`); },
        getTitle: () => { throw new Error(`Pinned ${contract.caseId} probe must not read the title.`); },
    };
}

/** One plan step of the case. A capture step is a fenced exchange whose replies the next read journals; any other feed clears them. */
async function runStep(step, record, { fence, owner, caseId }) {
    if (step.feed) {
        record.feeds.push(step.feed);
        if (step.capture) {
            record.report = await fence.exchange(step.feed, parseXtgettcapFrame);
            record.barriers.push(record.report.barrier);
        } else {
            record.report = null;
            record.barriers.push(await fence.write(step.feed));
        }
        return;
    }
    const isReport = step.read === 'report';
    if (isReport && record.report === null) throw new Error(`${caseId} report read without an exchange.`);
    const observed = await owner('xgtc-read', record.barriers.at(-1), isReport ? { report: record.report } : {});
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(isReport ? { report: record.report } : {}) });
    record.report = null;
}

/** Every step of the case, serially: each write, exchange and read must settle before the next changes state. */
async function collectCase(contract, io, nonce) {
    const record = { feeds: [], barriers: [], reads: [], report: null };
    for (const step of contract.plan) await runStep(step, record, { ...io, caseId: contract.caseId }); // NOSONAR - serial by design
    if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) {
        throw new Error(`Incomplete ${contract.caseId} XTGETTCAP fixture.`);
    }
    validateXtgettcapFeeds(contract, record.feeds);
    const { feeds, barriers, reads } = record;
    return { caseId: contract.caseId, feeds, barriers, reads };
}

/** Restore the screen whatever happened, aggregating a collection failure with a restoration failure instead of dropping either. */
async function collectAndRestore({ fence, owner, nonce }) {
    const cases = [];
    let primary, failed = false;
    try {
        await owner('xgtc-bind', await fence.write('\x1b[?1049h' + xtgettcapReset + xtgettcapSeed(nonce)));
        for (const contract of xtgettcapContracts) cases.push(await collectCase(contract, { fence, owner }, nonce)); // NOSONAR - serial by design
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner('xgtc-finish', await fence.write(xtgettcapReset + '\x1b[?1049l'));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'xgtc collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return cases;
}

/** Run the contracted case serially with parent-journaled reads and replies, then replay both pinned paths. */
export async function collectXtgettcapGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeXtgettcapAdapter;
    await owner('xgtc-start');
    const nonce = xtgettcapNoncePrefix + config.runId;
    const cases = await collectAndRestore({ fence, owner, nonce });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of cases) {
        const contract = xtgettcapContracts.find(entry => entry.caseId === item.caseId);
        const pinnedExchange = item.reads[contract.pinnedRead]?.report ?? null;
        const termless = gradingModule.ntildeRunXtgettcapTermless(contract.caseId, xtgettcapRecordingContext(contract, pinnedExchange));
        const terminal = await gradingModule.ntildeRunXtgettcapTerminal(contract.caseId, xtgettcapRecordingContext(contract, pinnedExchange));
        run.cases.push({ caseId: item.caseId, feeds: item.feeds, barriers: item.barriers, reads: item.reads, pinned: { termless, terminal } });
    }
    return run;
}

/** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
export const xtgettcapBind = runId => '\x1b[?1049h' + xtgettcapPlanSeed(runId);
