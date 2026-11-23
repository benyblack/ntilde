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

/** Settle one step into a value or an error, so a failed collection can never skip the restoration that follows it. */
const settle = async step => {
    try { return { value: await step() }; } catch (error) { return { error }; }
};

/** Run the work, then the restoration; when both fail, report both instead of dropping either. `label` names the suite. */
export async function guarded(work, restore, label) {
    const done = await settle(work), restored = await settle(restore);
    const failures = [done, restored].filter(outcome => 'error' in outcome).map(outcome => outcome.error);
    if (failures.length === 2) throw new AggregateError(failures, `${label} collection and restoration failed.`);
    if (failures.length === 1) throw failures[0];
    return done.value;
}

/** A read step journals the screen the parent observer sees after the last barrier, with the replies of the exchange that preceded it. `suite` names the suite: { label, prefix, parseFrame }. */
async function readStep(step, record, { owner, caseId, suite }) {
    if (step.read !== 'report' && step.read !== 'cells') fail(`${caseId} unknown plan step ${step.read}.`);
    const wantsReport = step.read === 'report';
    if (wantsReport && record.report === null) fail(`${caseId} report read without an exchange.`);
    const observed = await owner(`${suite.prefix}-read`, record.barriers.at(-1), wantsReport ? { report: record.report } : {});
    const journal = wantsReport ? { report: record.report } : {};
    record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(suite.journalSize ? { size: [observed.screen.rows, observed.screen.cols] } : {}), ...journal });
    record.report = null;
}

/** A feed step writes the plan bytes; a capture step is one fenced exchange whose raw replies the next read journals, any other feed clears them. */
async function feedStep(step, record, { fence, suite }) {
    record.feeds.push(step.feed);
    if (step.capture === true) {
        record.report = await fence.exchange(step.feed, suite.parseFrame);
        record.barriers.push(record.report.barrier);
        return;
    }
    record.report = null;
    record.barriers.push(await fence.write(step.feed));
}

/** Run one case's exact plan through the owned fence and return its journal of feeds, barriers and reads. Shared by every suite that frames its own exchanges. */
export async function collectPlan(contract, io, suite) {
    const record = { feeds: [], barriers: [], reads: [], report: null }, context = { ...io, caseId: contract.caseId, suite };
    for (const step of contract.plan) await (step.feed === undefined ? readStep : feedStep)(step, record, context); // NOSONAR - serial by design
    if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) fail(`Incomplete ${contract.caseId} ${suite.label} fixture.`);
    validateExactFeeds(contract, record.feeds);
    const { feeds, barriers, reads } = record;
    return { caseId: contract.caseId, feeds, barriers, reads };
}

const pclSuite = Object.freeze({ label, prefix: pclPrefix, parseFrame: parsePclFrame });

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
            for (const contract of pclContracts) done.push(await collectPlan(contract, io, pclSuite)); // NOSONAR - serial by design
            return done;
        },
        async () => { await owner(`${pclPrefix}-finish`, await fence.write(pclFinish(config.runId))); }, label);
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
