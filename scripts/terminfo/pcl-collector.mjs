// Worker-side driver for the xterm pointer-color qualification (extensions.osc113-reset-pointer-fg, extensions.osc114-reset-pointer-bg). Replays each case's exact plan through the owned fence on a measured
// seeded alternate screen: the PINNED query first, then the graded set, reset, chain and color-read groups. A request step is one fenced exchange that retains every raw reply (or their explicit absence)
// before the DA barrier; every read is journaled by the parent observer together with those replies. Both pinned paths then run over the journaled bytes of the pinned request's own exchange: the
// termless path over its captured reply text, the terminal path over the cursor position of the same read. The shared query-probe framing, observer and journal helpers are reused; the pinned replay
// is the suite's own because its terminal path writes and asks the cursor position instead of asking a framed query.
import { pclContracts, pclFinish, pclPlanSeed, pclPrefix, pclPinnedScripts } from './pcl-contracts.mjs';
import { devqRecordingContext } from './devq-collector.mjs';
import { planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { parsePclFrame } from './pcl-report.mjs';

const label = 'pointer-color';
const fail = message => { throw new Error(message); };

/** Serve the journaled exchange of the pinned request to the pinned termless path. Only feedCapture with the pinned query is accepted; an absent reply is the empty capture. */
export function pclRecordingContext(contract, exchange) {
    return devqRecordingContext(contract, exchange);
}

/** A context for the pinned terminal path: `write` and `queryCursorPosition` are the only members served, and the writes must be the pinned ones, in order. The cursor answers with the journaled cursor of the
 *  pinned exchange's own read (zero-based in the journal, one-based on the wire). */
export function pclTerminalContext(caseId, cursor) {
    const script = pclPinnedScripts[caseId];
    if (script === undefined) fail(`Unknown pointer-color case ${caseId}.`);
    const writes = [];
    const served = {
        write: data => { writes.push(data); },
        queryCursorPosition: async () => (cursor === null ? null : { row: cursor[0] + 1, col: cursor[1] + 1 }),
    };
    const context = new Proxy(served, {
        get(target, key) {
            if (!(key in target)) throw new Error(`Unexpected pinned context access: ${String(key)}.`);
            return target[key];
        },
    });
    return { context, writes, expectedWrites: script.terminal.filter(call => call[0] === 'write').map(call => call[1]) };
}

/** Run the work, then the restoration; when both fail, report both instead of dropping either. */
async function guarded(work, restore) {
    let result, primary = null, restoreError = null;
    try { result = await work(); }
    catch (error) { primary = error; }
    try { await restore(); }
    catch (error) { restoreError = error; }
    if (primary !== null && restoreError !== null) throw new AggregateError([primary, restoreError], `${label} collection and restoration failed.`);
    if (primary !== null) throw primary;
    if (restoreError !== null) throw restoreError;
    return result;
}

/** One plan step of the case. A capture step is a fenced exchange whose replies the next read journals; any other feed clears them. */
async function runStep(step, record, { fence, owner, caseId }) {
    if (step.feed === undefined) {
        if (step.read !== 'report' && step.read !== 'cells') fail(`${caseId} unknown plan step ${step.read}.`);
        if (step.read === 'report' && record.report === null) fail(`${caseId} report read without an exchange.`);
        const observed = await owner(`${pclPrefix}-read`, record.barriers.at(-1), step.read === 'report' ? { report: record.report } : {});
        record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(step.read === 'report' ? { report: record.report } : {}) });
        record.report = null;
        return;
    }
    const capture = step.capture === true;
    record.feeds.push(step.feed);
    record.report = capture ? await fence.exchange(step.feed, parsePclFrame) : null;
    record.barriers.push(capture ? record.report.barrier : await fence.write(step.feed));
}

async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], report: null };
    for (const step of contract.plan) await runStep(step, record, { ...io, caseId: contract.caseId }); // NOSONAR - serial by design
    if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) fail(`Incomplete ${contract.caseId} ${label} fixture.`);
    validateExactFeeds(contract, record.feeds);
    return { caseId: contract.caseId, feeds: record.feeds, barriers: record.barriers, reads: record.reads };
}

/** Replay both pinned paths over the journaled bytes of the pinned request's own exchange, making exactly the contracted calls. */
async function replayPinned(gradingModule, contract, collected) {
    const read = collected.reads[contract.pinnedRead];
    const exchange = read?.report ?? null;
    const termlessContext = pclRecordingContext(contract, exchange);
    const termless = gradingModule.runTermless(contract.caseId, termlessContext);
    if (termlessContext.fedCount !== contract.pinnedFeeds.length || termlessContext.askedCount !== 0) fail(`Pinned ${contract.caseId} termless replay made a different number of calls.`);
    const terminalContext = pclTerminalContext(contract.caseId, read?.cursor ?? null);
    const terminal = await gradingModule.runTerminal(contract.caseId, terminalContext.context);
    if (JSON.stringify(terminalContext.writes) !== JSON.stringify(terminalContext.expectedWrites)) fail(`Pinned ${contract.caseId} terminal replay made different writes.`);
    return { termless, terminal };
}

/** Run the contracted cases with parent-journaled reads and replies, then replay both pinned paths of each. The grading module must expose its adapter as `adapter` and its pinned runners as
 *  `runTermless`/`runTerminal`. */
async function collectGrading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.adapter;
    const io = { fence, owner };
    await owner(`${pclPrefix}-start`);
    const collected = await guarded(
        async () => {
            await owner(`${pclPrefix}-bind`, await fence.write('\x1b[?1049h' + pclPlanSeed(config.runId)));
            const done = [];
            for (const contract of pclContracts) done.push(await collectCase(contract, io)); // NOSONAR - serial by design
            return done;
        },
        async () => { await owner(`${pclPrefix}-finish`, await fence.write(pclFinish(config.runId))); });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = pclContracts.find(entry => entry.caseId === item.caseId); // NOSONAR - serial by design: each pinned replay settles in case order
        run.cases.push({ ...item, pinned: await replayPinned(gradingModule, contract, item) }); // NOSONAR - preserve sequential replay and failure ordering
    }
    return run;
}

/** Translate the layered bundle's exported names (ntildePclAdapter, ntildeRunPclTermless, ntildeRunPclTerminal) into the driver's grading-module shape, then run the contracted cases. */
export function collectPclGrading({ config, fence, owner, gradingModule }) {
    return collectGrading({ config, fence, owner, gradingModule: {
        adapter: gradingModule.ntildePclAdapter,
        runTermless: gradingModule.ntildeRunPclTermless,
        runTerminal: gradingModule.ntildeRunPclTerminal,
    } });
}

/** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
export const pclBind = runId => '\x1b[?1049h' + pclPlanSeed(runId);
