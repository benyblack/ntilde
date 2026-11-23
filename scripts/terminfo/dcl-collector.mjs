// Worker-side driver for the DECCOLM qualification (modes.deccolm). Replays the case's exact plan through the owned fence on a measured seeded alternate screen: the recognised-mode control, then the
// PINNED exchanges and the graded set, reset, allow-switching and cycle groups, then the clear/home control. A request step is one fenced exchange that retains every raw reply (or their explicit absence)
// before the DA barrier; every read is journaled by the parent observer together with those replies and the App and kernel grids. Both pinned paths then run over the journaled bytes of their own
// exchanges: the termless path over the pinned exchange's captured reply text and the App grid width of the same read, the terminal path over the replies of the pinned mode query. The shared
// query-probe framing, observer and journal helpers are reused; the pinned replay is the suite's own because its paths ask mode state rather than a framed colour or cursor query.
import { dclContracts, dclFinish, dclPlanSeed, dclPrefix, pinnedModeState } from './dcl-contracts.mjs';
import { planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { parseDclFrame } from './dcl-report.mjs';

const label = 'deccolm';
const fail = message => { throw new Error(message); };

/** Serve the journaled exchange of the pinned termless request to the pinned termless path, cross-checking that the pinned code makes exactly the contracted calls and nothing else. `cols` is the App grid
 *  width of the same read (the pinned code reports it); an absent reply is the empty capture. Only `feed`, `feedCapture` and the `cols` getter are served. */
export function dclRecordingContext(contract, exchange, cols) {
    const script = contract.pinnedScript.termless, seen = [];
    const next = (kind, text) => {
        const expected = script[seen.length];
        if (expected === undefined || expected[0] !== kind || expected[1] !== text) throw new Error(`Pinned ${contract.caseId} call ${seen.length} differs from the contract.`);
        seen.push(kind);
    };
    const served = {
        feed: text => { next('feed', text); },
        feedCapture: text => { next('feedCapture', text); return exchange === null ? '' : exchange.replies.join(''); },
        get cols() { return cols; },
    };
    const context = new Proxy(served, {
        get(target, key) {
            if (!(key in target)) throw new Error(`Unexpected pinned context access: ${String(key)}.`);
            return target[key];
        },
    });
    return { context, get callCount() { return seen.length; }, expectedCalls: script.length };
}

/** A context for the pinned terminal path: `queryMode` is the only member served, for mode 3 once; it answers the state the pinned queryMode derives from the journaled replies of the pinned query's own
 *  exchange (null when none matched). */
export function dclTerminalContext(contract, exchange) {
    let asked = 0;
    const served = {
        queryMode: async mode => {
            if (mode !== 3 || asked !== 0) throw new Error(`Pinned ${contract.caseId} asked an unexpected mode query.`);
            asked++;
            return pinnedModeState(exchange === null ? [] : exchange.replies);
        },
    };
    const context = new Proxy(served, {
        get(target, key) {
            if (!(key in target)) throw new Error(`Unexpected pinned context access: ${String(key)}.`);
            return target[key];
        },
    });
    return { context, get askedCount() { return asked; } };
}

/** Settle one step into a value or an error, so a failed collection can never skip the restoration that follows it. */
const settle = async step => {
    try { return { value: await step() }; } catch (error) { return { error }; }
};

/** Run the work, then the restoration; when both fail, report both instead of dropping either. */
async function guarded(work, restore) {
    const done = await settle(work), restored = await settle(restore);
    const failures = [done, restored].filter(outcome => 'error' in outcome).map(outcome => outcome.error);
    if (failures.length === 2) throw new AggregateError(failures, `${label} collection and restoration failed.`);
    if (failures.length === 1) throw failures[0];
    return done.value;
}

/** A read step journals the screen the parent observer sees after the last barrier, with the replies of the exchange that preceded it. */
async function readStep(step, record, { owner, caseId }) {
    if (step.read !== 'report' && step.read !== 'cells') fail(`${caseId} unknown plan step ${step.read}.`);
    const wantsReport = step.read === 'report';
    if (wantsReport && record.report === null) fail(`${caseId} report read without an exchange.`);
    const observed = await owner(`${dclPrefix}-read`, record.barriers.at(-1), wantsReport ? { report: record.report } : {});
    const journal = wantsReport ? { report: record.report } : {};
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], size: [observed.screen.rows, observed.screen.cols], ...journal });
    record.report = null;
}

/** A feed step writes the plan bytes; a capture step is one fenced exchange whose raw replies the next read journals, any other feed clears them. */
async function feedStep(step, record, { fence }) {
    record.feeds.push(step.feed);
    if (step.capture === true) {
        record.report = await fence.exchange(step.feed, parseDclFrame);
        record.barriers.push(record.report.barrier);
        return;
    }
    record.report = null;
    record.barriers.push(await fence.write(step.feed));
}

async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], report: null }, context = { ...io, caseId: contract.caseId };
    for (const step of contract.plan) await (step.feed === undefined ? readStep : feedStep)(step, record, context); // NOSONAR - serial by design
    if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) fail(`Incomplete ${contract.caseId} ${label} fixture.`);
    validateExactFeeds(contract, record.feeds);
    const { feeds, barriers, reads } = record;
    return { caseId: contract.caseId, feeds, barriers, reads };
}

/** Replay both pinned paths over the journaled bytes of their own exchanges, making exactly the contracted calls. */
async function replayPinned(gradingModule, contract, collected) {
    const termlessRead = collected.reads[contract.pinnedRead], terminalRead = collected.reads[contract.pinnedTerminalRead];
    const termlessContext = dclRecordingContext(contract, termlessRead?.report ?? null, termlessRead?.size?.[1]);
    const termless = gradingModule.runTermless(contract.caseId, termlessContext.context);
    if (termlessContext.callCount !== termlessContext.expectedCalls) fail(`Pinned ${contract.caseId} termless replay made a different number of calls.`);
    const terminalContext = dclTerminalContext(contract, terminalRead?.report ?? null);
    const terminal = await gradingModule.runTerminal(contract.caseId, terminalContext.context);
    if (terminalContext.askedCount !== 1) fail(`Pinned ${contract.caseId} terminal replay made a different number of calls.`);
    return { termless, terminal };
}

/** Run the contracted cases with parent-journaled reads and replies, then replay both pinned paths of each. The grading module must expose its adapter as `adapter` and its pinned runners as
 *  `runTermless`/`runTerminal`. */
async function collectGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.adapter;
    const io = { fence, owner };
    await owner(`${dclPrefix}-start`);
    const collected = await guarded(
        async () => {
            await owner(`${dclPrefix}-bind`, await fence.write('\x1b[?1049h' + dclPlanSeed(config.runId)));
            const done = [];
            for (const contract of dclContracts) done.push(await collectCase(contract, io)); // NOSONAR - serial by design
            return done;
        },
        async () => { await owner(`${dclPrefix}-finish`, await fence.write(dclFinish(config.runId))); });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = dclContracts.find(entry => entry.caseId === item.caseId); // NOSONAR - serial by design: each pinned replay settles in case order
        run.cases.push({ ...item, pinned: await replayPinned(gradingModule, contract, item) }); // NOSONAR - preserve sequential replay and failure ordering
    }
    return run;
}

/** Translate the layered bundle's exported names (ntildeDclAdapter, ntildeRunDclTermless, ntildeRunDclTerminal) into the driver's grading-module shape, then run the contracted cases. */
export function collectDclGrading({ config, fence, owner, gradingModule }) {
    return collectGrading({ config, fence, owner, gradingModule: {
        adapter: gradingModule.ntildeDclAdapter,
        runTermless: gradingModule.ntildeRunDclTermless,
        runTerminal: gradingModule.ntildeRunDclTerminal,
    } });
}

/** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
export const dclBind = runId => '\x1b[?1049h' + dclPlanSeed(runId);
