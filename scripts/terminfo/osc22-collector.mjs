// Worker-side driver for the OSC 22 qualification (extensions.osc22-pointer). Replays every experiment's exact plan through the owned fence (shared graded-plan driver): the PINNED
// fixture bytes on a measured baseline, then the contracted pointer setters, controls and policy probes. Every read is journaled by the parent observer as a paired complete screen /
// pane-metadata / session-status capture. The pinned terminal path is then run over the journaled cursor of the stock read: the code the terminfo.dev bundle ships, never a reimplementation.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { osc133Fixture } from './osc133-contracts.mjs';
import { makeOsc22Experiments, osc22CaseId, osc22CaseIds, osc22PinnedWrites, osc22Prefix } from './osc22-contracts.mjs';

const unavailableCodes = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** A context that serves the pinned code nothing but what it legitimately uses: any other access is a changed probe. */
function pinnedContext(served) {
    return new Proxy(served, {
        get(target, key) {
            if (!(key in target)) throw new Error(`Unexpected pinned context access: ${String(key)}.`);
            return target[key];
        },
    });
}

/** The pinned terminal path over the cursor position the parent observed after the pinned bytes (one-based, as a cursor-position report is). Its two writes must be exactly the pinned ones. */
export async function replayOsc22Pinned(gradingModule, cursor) {
    if (!Array.isArray(cursor) || cursor.length !== 2 || !cursor.every(Number.isSafeInteger)) throw new Error('Invalid OSC 22 pinned cursor.');
    if (gradingModule.ntildeOsc22HasTermless(osc22CaseId) !== false) throw new Error('Pinned OSC 22 termless presence changed.');
    const writes = [], position = { row: cursor[0] + 1, col: cursor[1] + 1 };
    const { probeId, ...terminal } = await gradingModule.ntildeRunOsc22Terminal(osc22CaseId, pinnedContext({
        write: text => { writes.push(text); }, queryCursorPosition: () => Promise.resolve(position) }));
    if (probeId !== osc22CaseId || JSON.stringify(writes) !== JSON.stringify([...osc22PinnedWrites])) throw new Error('Pinned OSC 22 terminal replay changed.');
    return { caseId: osc22CaseId, termlessFunction: false, termless: null, terminal, writes, cursor: [position.row, position.col] };
}

/** Run the contracted experiments serially with parent-journaled reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectOsc22({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOsc22Adapter;
    if (adapter?.id !== 'ntilde-owned-osc22-grading-v1' || JSON.stringify(adapter.probeIds) !== JSON.stringify([...osc22CaseIds])) throw new Error('OSC 22 adapter does not name the contracted probe.');
    const fixture = osc133Fixture(config.runId), contracts = makeOsc22Experiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: osc22Prefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableCodes.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads }) => ({ caseId, feeds, barriers, reads }));
    const stock = experiments.find(item => item.caseId === `${osc22CaseId}/stock`);
    if (stock?.reads.length !== 1) throw new Error('The OSC 22 stock experiment is missing.');
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], experiments, pinned: [await replayOsc22Pinned(gradingModule, stock.reads[0].cursor)] } };
}
