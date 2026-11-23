// Worker-side driver for the DECCOLM qualification (modes.deccolm). Replays the case's exact plan through the owned fence on a measured seeded alternate screen: the recognised-mode control, then the
// PINNED exchanges and the graded set, reset, allow-switching and cycle groups, then the clear/home control. A request step is one fenced exchange that retains every raw reply (or their explicit absence)
// before the DA barrier; every read is journaled by the parent observer together with those replies and the App and kernel grids. Both pinned paths then run over the journaled bytes of their own
// exchanges: the termless path over the pinned exchange's captured reply text and the App grid width of the same read, the terminal path over the replies of the pinned mode query. The shared
// query-probe framing, observer and journal helpers are reused; the pinned replay is the suite's own because its paths ask mode state rather than a framed colour or cursor query.
import { dclContracts, dclFinish, dclPlanSeed, dclPrefix, pinnedModeState } from './dcl-contracts.mjs';
import { collectPlan, guarded } from './pcl-collector.mjs';
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

const dclSuite = Object.freeze({ label, prefix: dclPrefix, parseFrame: parseDclFrame, journalSize: true });

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
            for (const contract of dclContracts) done.push(await collectPlan(contract, io, dclSuite)); // NOSONAR - serial by design
            return done;
        },
        async () => { await owner(`${dclPrefix}-finish`, await fence.write(dclFinish(config.runId))); }, label);
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
