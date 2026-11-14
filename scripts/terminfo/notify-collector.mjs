import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { exposeOsc9Facts } from './osc9-collector.mjs';
import { notifyCaseIds, notifyExperiments, notifyFixture, notifyPrefix, osc777CaseId, osc777PinnedWrites, osc99CaseId } from './notify-contracts.mjs';

const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** The bounded facts the worker needs from one paired observation: the stored progress (a toast must not be progress), the actual tab text, the private toast and the focus/opt-in conditions. */
export const exposeNotifyFacts = exposeOsc9Facts;

/** A prerequisite may be skipped only through its typed initial refusal; otherwise it must have restored. */
export function notifyPrerequisiteReason(restored, unavailable, reason) {
    if (unavailable === true) {
        if (!unavailableReasons.has(reason)) throw new Error('Unexpected notification prerequisite refusal.');
        return reason;
    }
    if (!restored) throw new Error('The OSC 9 suite must restore before notification observations.');
    return null;
}

/** A context that serves the pinned code nothing but what it legitimately reads: any other access is a changed probe. */
function pinnedContext(served) {
    return new Proxy(served, {
        get(target, key) {
            if (!(key in target)) throw new Error(`Unexpected pinned context access: ${String(key)}.`);
            return target[key];
        },
    });
}

/** The pinned OSC 777 terminal path over the cursor position the parent observed after the pinned bytes (one-based, as a cursor-position report is). Its two writes must be exactly the pinned ones. */
export async function replayOsc777Pinned(gradingModule, cursor) {
    if (!Array.isArray(cursor) || cursor.length !== 2 || !cursor.every(Number.isSafeInteger)) throw new Error('Invalid OSC 777 pinned cursor.');
    const writes = [], position = { row: cursor[0] + 1, col: cursor[1] + 1 };
    const { probeId, ...terminal } = await gradingModule.ntildeRunNotifyTerminal(osc777CaseId, pinnedContext({
        write: text => { writes.push(text); }, queryCursorPosition: () => Promise.resolve(position) }));
    if (probeId !== osc777CaseId || JSON.stringify(writes) !== JSON.stringify([...osc777PinnedWrites])) throw new Error('Pinned OSC 777 terminal replay changed.');
    return { caseId: osc777CaseId, termlessFunction: false, termless: null, terminal, writes, cursor: [position.row, position.col] };
}

/** Both pinned records: OSC 777 replayed, OSC 99 identified only (its verdict belongs to the stock collector and the exact-query witness). */
export async function replayNotifyPinned(gradingModule, experiments) {
    for (const caseId of notifyCaseIds) {
        if (gradingModule.ntildeNotifyHasTermless(caseId) !== false) throw new Error(`Pinned ${caseId} termless presence changed.`);
    }
    const fixture = experiments.find(item => item.caseId === `${osc777CaseId}/pinned-fixture`);
    const after = fixture?.reads.find(item => item.kind === 'after');
    if (!after) throw new Error('The OSC 777 pinned-fixture experiment is missing.');
    return [await replayOsc777Pinned(gradingModule, after.cursor),
        { caseId: osc99CaseId, termlessFunction: false, termless: null, terminal: null, replay: 'stock-collector-and-exact-query-witness' }];
}

/** Run the contracted experiments serially with parent-journaled paired reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectNotify({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeNotifyAdapter;
    if (JSON.stringify(adapter?.probeIds) !== JSON.stringify([...notifyCaseIds])) throw new Error('Changed notification adapter.');
    const fixture = notifyFixture(config.runId), contracts = notifyExperiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: notifyPrefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableReasons.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads, facts, actions }) => ({ caseId, feeds, barriers,
        reads: reads.map((entry, index) => ({ ...entry, facts: facts[index] })), ...(actions ? { actions } : {}) }));
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...notifyCaseIds], experiments, pinned: await replayNotifyPinned(gradingModule, experiments) } };
}
