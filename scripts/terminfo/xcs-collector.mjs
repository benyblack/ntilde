// Worker-side driver for the xterm color-stack qualification (modes.xtpushcolors, modes.xtpopcolors). Replays each case's exact plan through the owned fence: the PINNED sequence on a measured
// seeded alternate screen, then the graded controls. A request step is one fenced exchange that retains every raw reply (or their explicit absence) before the DA barrier; every read is journaled
// by the parent observer together with those replies. Both pinned paths then run over the journaled bytes of their own exchange and the cursor of its read.
import { xcsContracts, xcsFinish, xcsNoncePrefix, xcsPlanSeed, xcsPrefix, xcsReset, xcsSeed, validateXcsFeeds } from './xcs-contracts.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';
import { parseXcsFrame } from './xcs-report.mjs';

/** Serve the journaled exchange of the pinned sequence to one pinned probe path, cross-checking that the pinned code itself makes exactly the contracted calls (kind, text and order) and nothing
 * else. An absent reply is the empty capture; a DA1 capture is the exchange's own barrier; the cursor query answers with the cursor of the read that followed the exchange. */
export function xcsRecordingContext(contract, path, exchange, cursor) {
    const script = contract.pinnedScript[path];
    let at = 0;
    const next = (kind, text) => {
        const step = script[at];
        if (step === undefined || step[0] !== kind || (text !== undefined && step[1] !== text)) throw new Error(`Pinned ${contract.caseId} ${path} call ${at} differs from the contract.`);
        at++;
        return step;
    };
    const refuse = what => () => { throw new Error(`Pinned ${contract.caseId} probe must not read ${what}.`); };
    return {
        get callCount() { return at; },
        feed: text => { next('feed', text); },
        feedCapture: text => {
            const step = next('feedCapture', text);
            if (exchange === null) return '';
            return step[2] === 'replies' ? exchange.replies.join('') : exchange.barrier;
        },
        write: text => { next('write', text); },
        // The real queryCursorPosition is async: the position (or null) arrives as a promise.
        queryCursorPosition: () => {
            try {
                next('cursor');
                return Promise.resolve(Array.isArray(cursor) ? { row: cursor[0] + 1, col: cursor[1] + 1 } : null);
            } catch (error) { return Promise.reject(error); }
        },
        getCell: refuse('cells'), getCursor: refuse('the cursor'), getText: refuse('text'), getTitle: refuse('the title'),
    };
}

/** How many calls one pinned path makes over the journaled exchange. The terminal path always makes them all (its cursor query and closing write run whatever came back); the termless path returns
 *  early with the pinned invalid-reply result at the first capture that carries output. */
export function xcsExpectedCalls(contract, path, exchange) {
    const script = contract.pinnedScript[path];
    if (path === 'terminal' || exchange === null || exchange.replies.join('').length === 0) return script.length;
    return script.findIndex(step => step[2] === 'replies') + 1;
}

/** One plan step of the case. A capture step is a fenced exchange whose replies the next read journals; any other feed clears them. */
async function runStep(step, record, { fence, owner, caseId }) {
    if (step.feed) {
        record.feeds.push(step.feed);
        if (step.capture) {
            record.report = await fence.exchange(step.feed, parseXcsFrame);
            record.barriers.push(record.report.barrier);
        } else {
            record.report = null;
            record.barriers.push(await fence.write(step.feed));
        }
        return;
    }
    const isReport = step.read === 'report';
    if (isReport && record.report === null) throw new Error(`${caseId} report read without an exchange.`);
    const observed = await owner(`${xcsPrefix}-read`, record.barriers.at(-1), isReport ? { report: record.report } : {});
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(isReport ? { report: record.report } : {}) });
    record.report = null;
}

/** Every step of the case, serially: each write, exchange and read must settle before the next changes state. */
async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], report: null };
    for (const step of contract.plan) await runStep(step, record, { ...io, caseId: contract.caseId }); // NOSONAR - serial by design
    if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) throw new Error(`Incomplete ${contract.caseId} xterm-color-stack fixture.`);
    validateXcsFeeds(contract, record.feeds);
    const { feeds, barriers, reads } = record;
    return { caseId: contract.caseId, feeds, barriers, reads };
}

/** Restore the screen whatever happened, aggregating a collection failure with a restoration failure instead of dropping either. */
async function collectAndRestore({ fence, owner, nonce, runId }) {
    const cases = [];
    let primary, failed = false;
    try {
        await owner(`${xcsPrefix}-bind`, await fence.write('\x1b[?1049h' + xcsReset + xcsSeed(nonce)));
        for (const contract of xcsContracts) cases.push(await collectCase(contract, { fence, owner })); // NOSONAR - serial by design
    } catch (error) { primary = error; failed = true; }
    let restoreFailed = false, restoreError;
    try {
        await owner(`${xcsPrefix}-finish`, await fence.write(xcsFinish(runId)));
    } catch (error) { restoreFailed = true; restoreError = error; }
    if (failed && restoreFailed) throw new AggregateError([primary, restoreError], 'xcs collection and restoration failed.');
    if (restoreFailed) throw restoreError;
    if (failed) throw primary;
    return cases;
}

/** Run the contracted cases serially with parent-journaled reads and replies, then replay both pinned paths. */
export async function collectXcsGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeXcsAdapter;
    await owner(`${xcsPrefix}-start`);
    const nonce = xcsNoncePrefix + config.runId;
    const cases = await collectAndRestore({ fence, owner, nonce, runId: config.runId });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of cases) {
        const contract = xcsContracts.find(entry => entry.caseId === item.caseId);
        const read = item.reads[contract.pinnedRead], pinnedExchange = read?.report ?? null;
        const termlessContext = xcsRecordingContext(contract, 'termless', pinnedExchange, read?.cursor ?? null);
        const termless = gradingModule.ntildeRunXcsTermless(contract.caseId, termlessContext);
        const terminalContext = xcsRecordingContext(contract, 'terminal', pinnedExchange, read?.cursor ?? null);
        const terminal = await gradingModule.ntildeRunXcsTerminal(contract.caseId, terminalContext); // NOSONAR - serial by design: each pinned replay settles in case order
        if (termlessContext.callCount !== xcsExpectedCalls(contract, 'termless', pinnedExchange) || terminalContext.callCount !== xcsExpectedCalls(contract, 'terminal', pinnedExchange)) throw new Error(`Pinned ${contract.caseId} replay made fewer calls than the contract.`);
        run.cases.push({ caseId: item.caseId, feeds: item.feeds, barriers: item.barriers, reads: item.reads, pinned: { termless, terminal } });
    }
    return run;
}

/** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
export const xcsBind = runId => '\x1b[?1049h' + xcsPlanSeed(runId);
