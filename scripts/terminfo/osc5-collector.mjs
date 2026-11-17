// Worker-side driver for the rxvt special-color qualification (extensions.osc5-special-color). Replays the case's exact plan through the owned fence: the PINNED request on a measured seeded alternate screen,
// then the graded controls. A request step is one fenced exchange that retains every raw reply (or their explicit absence) before the DA barrier; every read is journaled by the parent observer together with
// those replies. Both pinned paths then run over the journaled bytes of the pinned request's own exchange.
import { osc5Contracts, osc5Finish, osc5PinnedPattern, osc5PlanSeed, osc5Prefix, validateOsc5Feeds } from './osc5-contracts.mjs';
import { devqRecordingContext } from './devq-collector.mjs';
import { planFeeds, planReads } from './graded-plan.mjs';
import { parseOsc5Frame } from './osc5-report.mjs';

/** Serve the journaled exchange of the pinned request to one pinned probe path. The pinned code may only call feedCapture with its own request and queryWithSentinelOutcome with its own frame pattern;
 *  every other member (cells, cursor, text, title) stays refused by the underlying recording context, and an absent reply is the empty capture. */
export function osc5RecordingContext(contract, exchange) {
    const inner = devqRecordingContext(contract, exchange);
    const query = (text, pattern) => {
        const samePattern = pattern instanceof RegExp && pattern.source === osc5PinnedPattern.source && pattern.flags === osc5PinnedPattern.flags;
        if (!samePattern) return Promise.reject(new Error(`Pinned ${contract.caseId} terminal path searched for a different frame pattern.`));
        return inner.queryWithSentinelOutcome(text, pattern);
    };
    return Object.create(inner, { queryWithSentinelOutcome: { value: query, enumerable: true } });
}

/** Run the work, then the restoration; when both fail, report both instead of dropping either. */
async function guarded(work, restore) {
    let result, primary = null, restoreError = null;
    try { result = await work(); }
    catch (error) { primary = error; }
    try { await restore(); }
    catch (error) { restoreError = error; }
    if (primary !== null && restoreError !== null) throw new AggregateError([primary, restoreError], 'special-color collection and restoration failed.');
    if (primary !== null) throw primary;
    if (restoreError !== null) throw restoreError;
    return result;
}

/** One plan step of the case. A capture step is a fenced exchange whose replies the next read journals; any other feed clears them. */
async function runStep(step, record, io) {
    const { fence, owner, caseId } = io;
    if (step.feed === undefined) {
        if (step.read !== 'report' && step.read !== 'cells') throw new Error(`${caseId} unknown plan step ${step.read}.`);
        if (step.read === 'report' && record.report === null) throw new Error(`${caseId} report read without an exchange.`);
        const observed = await owner(`${osc5Prefix}-read`, record.barriers.at(-1), step.read === 'report' ? { report: record.report } : {});
        record.reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol], ...(step.read === 'report' ? { report: record.report } : {}) });
        record.report = null;
        return;
    }
    const capture = step.capture === true;
    record.feeds.push(step.feed);
    record.report = capture ? await fence.exchange(step.feed, parseOsc5Frame) : null;
    record.barriers.push(capture ? record.report.barrier : await fence.write(step.feed));
}

/** Every step of the case, serially: each write, exchange and read must settle before the next changes state. */
async function collectCase(contract, io) {
    const record = { feeds: [], barriers: [], reads: [], report: null };
    for (const step of contract.plan) await runStep(step, record, { ...io, caseId: contract.caseId }); // NOSONAR - serial by design
    if (record.feeds.length !== planFeeds(contract).length || record.reads.length !== planReads(contract)) throw new Error(`Incomplete ${contract.caseId} special-color fixture.`);
    validateOsc5Feeds(contract, record.feeds);
    return { caseId: contract.caseId, feeds: record.feeds, barriers: record.barriers, reads: record.reads };
}

/** Run the pinned probe's two paths over the journaled exchange; the paths together must make exactly the contracted calls. */
async function replayPinned(gradingModule, contract, collected) {
    const exchange = collected.reads[contract.pinnedRead]?.report ?? null;
    const termlessContext = osc5RecordingContext(contract, exchange);
    const termless = gradingModule.ntildeRunOsc5Termless(contract.caseId, termlessContext);
    const terminalContext = osc5RecordingContext(contract, exchange);
    const terminal = await gradingModule.ntildeRunOsc5Terminal(contract.caseId, terminalContext);
    const calls = `${termlessContext.fedCount},${termlessContext.askedCount},${terminalContext.fedCount},${terminalContext.askedCount}`;
    if (calls !== '1,0,1,1') throw new Error(`Pinned ${contract.caseId} replay made a different number of calls (${calls}).`);
    return { termless, terminal };
}

/** Run the contracted case with parent-journaled reads and replies, then replay both pinned paths. */
export async function collectOsc5Grading({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOsc5Adapter;
    const io = { fence, owner };
    await owner(`${osc5Prefix}-start`);
    const collected = await guarded(
        async () => {
            await owner(`${osc5Prefix}-bind`, await fence.write('\x1b[?1049h' + osc5PlanSeed(config.runId)));
            const done = [];
            for (const contract of osc5Contracts) done.push(await collectCase(contract, io)); // NOSONAR - serial by design
            return done;
        },
        async () => { await owner(`${osc5Prefix}-finish`, await fence.write(osc5Finish(config.runId))); });
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const item of collected) {
        const contract = osc5Contracts.find(entry => entry.caseId === item.caseId); // NOSONAR - serial by design: each pinned replay settles in case order
        run.cases.push({ ...item, pinned: await replayPinned(gradingModule, contract, item) });
    }
    return run;
}

/** The exact bind bytes of the suite, exposed so tests can mirror the measured fixture. */
export const osc5Bind = runId => '\x1b[?1049h' + osc5PlanSeed(runId);
