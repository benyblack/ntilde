// Worker-side driver for the DECSCLM qualification (modes.decsclm). Replays the case's exact plan through the owned fence on a measured seeded alternate screen: the recognised-mode control, then the
// PINNED exchanges, the graded set and reset groups, the scroll bursts and the timing instrument control, then the clear/home control. A request step is one fenced exchange that retains every raw reply
// (or their explicit absence) before the DA barrier; every read is journaled by the parent observer together with those replies and the parent's own timing. The only delay the worker adds is the
// instrument control's contracted hold after its exchange (the parent's clock must see it). Both pinned paths then run over the journaled bytes of their own exchanges: the termless path over the pinned
// exchange's captured reply text, the terminal path over the replies of the pinned mode query. The shared query-probe framing, observer and journal helpers are reused; the pinned replay is the suite's
// own because its paths ask mode state rather than a framed colour or cursor query.
import { dslContracts, dslFinish, dslPlanSeed, dslPrefix, pinnedModeState } from './dsl-contracts.mjs';
import { collectPlan, guarded } from './pcl-collector.mjs';
import { parseDslFrame } from './dsl-report.mjs';

const label = 'decsclm';
const fail = message => { throw new Error(message); };

/** Serve the journaled exchange of the pinned termless request to the pinned termless path, cross-checking that the pinned code makes exactly the contracted calls and nothing else. An absent reply is the
 *  empty capture. Only `feed` and `feedCapture` are served. */
export function dslRecordingContext(contract, exchange) {
    const script = contract.pinnedScript.termless, seen = [];
    const next = (kind, text) => {
        const expected = script[seen.length];
        if (expected === undefined || expected[0] !== kind || expected[1] !== text) throw new Error(`Pinned ${contract.caseId} call ${seen.length} differs from the contract.`);
        seen.push(kind);
    };
    const served = {
        feed: text => { next('feed', text); },
        feedCapture: text => { next('feedCapture', text); return exchange === null ? '' : exchange.replies.join(''); },
    };
    const context = new Proxy(served, {
        get(target, key) {
            if (!(key in target)) throw new Error(`Unexpected pinned context access: ${String(key)}.`);
            return target[key];
        },
    });
    return { context, get callCount() { return seen.length; }, expectedCalls: script.length };
}

/** A context for the pinned terminal path: `queryMode` is the only member served, for mode 4 once; it answers the state the pinned queryMode derives from the journaled replies of the pinned query's own
 *  exchange (null when none matched). */
export function dslTerminalContext(contract, exchange) {
    let asked = 0;
    const served = {
        queryMode: async mode => {
            if (mode !== 4 || asked !== 0) throw new Error(`Pinned ${contract.caseId} asked an unexpected mode query.`);
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

const dslSuite = Object.freeze({ label, prefix: dslPrefix, parseFrame: parseDslFrame, journalSize: true });

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
/** The fence the plan runs through: the owned fence itself, except that the exchange of a group with a contracted hold (the timing instrument control) waits that long after it has completed, so the
 *  parent's clock has a slow interval to read. The holds are matched by position, never by bytes (several groups send the same bytes), and every exchange must be the contracted request of its group.
 *  No write is touched. */
export function heldFence(fence, contract, sleep = delay) {
    let position = 0;
    return {
        write: (...args) => fence.write(...args),
        exchange: async (bytes, parse) => {
            const group = contract.groups[position++];
            if (group === undefined || group.request !== bytes) fail(`The ${contract.caseId} exchange ${position - 1} is not the contracted request.`);
            const done = await fence.exchange(bytes, parse);
            if (group.pauseMs !== null) await sleep(group.pauseMs);
            return done;
        },
    };
}

/** Replay both pinned paths over the journaled bytes of their own exchanges, making exactly the contracted calls. */
async function replayPinned(gradingModule, contract, collected) {
    const termlessRead = collected.reads[contract.pinnedRead], terminalRead = collected.reads[contract.pinnedTerminalRead];
    const termlessContext = dslRecordingContext(contract, termlessRead?.report ?? null);
    const termless = gradingModule.runTermless(contract.caseId, termlessContext.context);
    if (termlessContext.callCount !== termlessContext.expectedCalls) fail(`Pinned ${contract.caseId} termless replay made a different number of calls.`);
    const terminalContext = dslTerminalContext(contract, terminalRead?.report ?? null);
    const terminal = await gradingModule.runTerminal(contract.caseId, terminalContext.context);
    if (terminalContext.askedCount !== 1) fail(`Pinned ${contract.caseId} terminal replay made a different number of calls.`);
    return { termless, terminal };
}

/** Run the contracted cases with parent-journaled reads and replies, then replay both pinned paths of each. The grading module must expose its adapter as `adapter` and its pinned runners as
 *  `runTermless`/`runTerminal`. */
async function collectGrading({ config, fence, owner, gradingModule, sleep = delay }) {
    const adapter = gradingModule.adapter;
    await owner(`${dslPrefix}-start`);
    const collected = await guarded(
        async () => {
            await owner(`${dslPrefix}-bind`, await fence.write('\x1b[?1049h' + dslPlanSeed(config.runId)));
            const done = [];
            for (const contract of dslContracts) done.push(await collectPlan(contract, { fence: heldFence(fence, contract, sleep), owner }, dslSuite)); // NOSONAR - serial by design
            return done;
        },
        async () => { await owner(`${dslPrefix}-finish`, await fence.write(dslFinish(config.runId))); }, label);
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = dslContracts.find(entry => entry.caseId === item.caseId); // NOSONAR - serial by design: each pinned replay settles in case order
        run.cases.push({ ...item, pinned: await replayPinned(gradingModule, contract, item) }); // NOSONAR - preserve sequential replay and failure ordering
    }
    return run;
}

/** Translate the layered bundle's exported names (ntildeDslAdapter, ntildeRunDslTermless, ntildeRunDslTerminal) into the driver's grading-module shape, then run the contracted cases. */
export function collectDslGrading({ config, fence, owner, gradingModule, sleep }) {
    return collectGrading({ config, fence, owner, sleep, gradingModule: {
        adapter: gradingModule.ntildeDslAdapter,
        runTermless: gradingModule.ntildeRunDslTermless,
        runTerminal: gradingModule.ntildeRunDslTerminal,
    } });
}

/** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
export const dslBind = runId => '\x1b[?1049h' + dslPlanSeed(runId);
