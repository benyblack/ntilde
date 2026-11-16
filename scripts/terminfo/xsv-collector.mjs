// Worker-side driver for the xterm saved-mode qualification (modes.xtsave, modes.xtrestore). Replays each case's exact plan through the owned fence: the PINNED sequence on a measured
// seeded alternate screen, then the graded controls. A request step is one fenced exchange that retains every raw reply (or their explicit absence) before the DA barrier; every read is journaled
// by the parent observer together with those replies. Both pinned paths then run over the journaled bytes of their own exchange and the cursor of its read.
import { xsvContracts, xsvFinish, xsvNoncePrefix, xsvPlanSeed, xsvPrefix, xsvReset, xsvSeed, validateXsvFeeds } from './xsv-contracts.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';
import { parseXsvFrame } from './xsv-report.mjs';

/** Serving the journaled exchange to a pinned probe path and counting its calls are the color-stack suite's, reused unchanged: both read only the contract's `pinnedScript`, whose calls (kind, text and order) the pinned code must make exactly. */
import { xcsRecordingContext as xsvRecordingContext, xcsExpectedCalls as xsvExpectedCalls } from './xcs-collector.mjs';
export { xsvRecordingContext, xsvExpectedCalls };

/** One plan step of the case. A capture step is a fenced exchange whose replies the next read journals; any other feed clears them. */
async function runStep(step, record, { fence, owner, caseId }) {
    if (step.feed) {
        record.feeds.push(step.feed);
        if (step.capture) {
            record.report = await fence.exchange(step.feed, parseXsvFrame);
            record.barriers.push(record.report.barrier);
        } else {
            record.report = null;
            record.barriers.push(await fence.write(step.feed));
        }
        return;
    }
    const isReport = step.read === 'report';
    if (isReport && record.report === null) throw new Error(`${caseId} report read without an exchange.`);
    const observed = await owner(`${xsvPrefix}-read`, record.barriers.at(-1), isReport ? { report: record.report } : {});
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(isReport ? { report: record.report } : {}) });
    record.report = null;
}

/** Every step of the case, serially: each write, exchange and read must settle before the next changes state. */
async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], report: null };
    for (const step of contract.plan) await runStep(step, record, { ...io, caseId: contract.caseId }); // NOSONAR - serial by design
    if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) throw new Error(`Incomplete ${contract.caseId} xterm-saved-modes fixture.`);
    validateXsvFeeds(contract, record.feeds);
    const { feeds, barriers, reads } = record;
    return { caseId: contract.caseId, feeds, barriers, reads };
}

/** Restore the screen whatever happened, aggregating a collection failure with a restoration failure instead of dropping either. */
async function collectAndRestore({ fence, owner, nonce, runId }) {
    const cases = [];
    let primary, failed = false;
    try {
        await owner(`${xsvPrefix}-bind`, await fence.write('\x1b[?1049h' + xsvReset + xsvSeed(nonce)));
        for (const contract of xsvContracts) cases.push(await collectCase(contract, { fence, owner })); // NOSONAR - serial by design
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner(`${xsvPrefix}-finish`, await fence.write(xsvFinish(runId)));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'xsv collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return cases;
}

/** Run the contracted cases serially with parent-journaled reads and replies, then replay both pinned paths. */
export async function collectXsvGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeXsvAdapter;
    await owner(`${xsvPrefix}-start`);
    const nonce = xsvNoncePrefix + config.runId;
    const cases = await collectAndRestore({ fence, owner, nonce, runId: config.runId });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of cases) {
        const contract = xsvContracts.find(entry => entry.caseId === item.caseId);
        const read = item.reads[contract.pinnedRead], pinnedExchange = read?.report ?? null;
        const termlessContext = xsvRecordingContext(contract, 'termless', pinnedExchange, read?.cursor ?? null);
        const termless = gradingModule.ntildeRunXsvTermless(contract.caseId, termlessContext);
        const terminalContext = xsvRecordingContext(contract, 'terminal', pinnedExchange, read?.cursor ?? null);
        const terminal = await gradingModule.ntildeRunXsvTerminal(contract.caseId, terminalContext); // NOSONAR - serial by design: each pinned replay settles in case order
        if (termlessContext.callCount !== xsvExpectedCalls(contract, 'termless', pinnedExchange) || terminalContext.callCount !== xsvExpectedCalls(contract, 'terminal', pinnedExchange)) throw new Error(`Pinned ${contract.caseId} replay made fewer calls than the contract.`);
        run.cases.push({ caseId: item.caseId, feeds: item.feeds, barriers: item.barriers, reads: item.reads, pinned: { termless, terminal } });
    }
    return run;
}

/** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
export const xsvBind = runId => '\x1b[?1049h' + xsvPlanSeed(runId);
