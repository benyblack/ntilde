import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { osc9CaseIds, osc9DeclaredCapabilities, osc9Experiments, osc9Fixture, osc9Pinned, osc9Prefix } from './osc9-contracts.mjs';

const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** The small, bounded facts the worker needs from one paired observation: stored progress, the actual tab text, the private toast and the focus/opt-in conditions. */
export const exposeOsc9Facts = snapshot => {
    const p = snapshot.metadata.presentation;
    return { progress: { owner: p.progressOwner, kind: p.progressKind, percent: p.progressPercent }, tabText: p.tabText,
        toast: { visible: p.toastVisible, title: p.toastTitle, message: p.toastMessage },
        focus: { windowActive: p.windowActive, windowVisible: p.windowVisible, paneActive: p.paneActive, focusMode: p.focusMode,
            notificationsEnabled: p.notificationsEnabled, progressEnabled: p.progressEnabled } };
};

/** A prerequisite may be skipped only through its typed initial refusal; otherwise it must have restored. */
export function osc9PrerequisiteReason(restored, unavailable, reason) {
    if (unavailable === true) {
        if (!unavailableReasons.has(reason)) throw new Error('Unexpected OSC 9 prerequisite refusal.');
        return reason;
    }
    if (!restored) throw new Error('The XTGETTCAP suite must restore before OSC 9 observations.');
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

/** Run every pinned path of both probes. Neither probe writes, feeds or reads anything, so the contexts serve at most the declared capabilities. */
export async function replayOsc9Pinned(gradingModule) {
    return Promise.all(osc9CaseIds.map(async caseId => {
        const descriptor = osc9Pinned[caseId];
        const hasTermless = gradingModule.ntildeOsc9HasTermless(caseId);
        if (hasTermless !== descriptor.termlessFunction) throw new Error(`Pinned ${caseId} termless presence changed.`);
        let termless = null;
        if (hasTermless) {
            const { probeId, ...result } = gradingModule.ntildeRunOsc9Termless(caseId, pinnedContext({ capabilities: { extensions: new Set(osc9DeclaredCapabilities) } }));
            if (probeId !== caseId) throw new Error(`Incomplete pinned ${caseId} termless replay.`);
            termless = result;
        }
        const { probeId: terminalId, ...terminal } = await gradingModule.ntildeRunOsc9Terminal(caseId, pinnedContext({}));
        if (terminalId !== caseId) throw new Error(`Incomplete pinned ${caseId} terminal replay.`);
        return { caseId, termlessFunction: hasTermless, termless, terminal };
    }));
}

/** Run the contracted experiments serially with parent-journaled paired reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectOsc9({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOsc9Adapter;
    if (JSON.stringify(adapter?.probeIds) !== JSON.stringify([...osc9CaseIds])) throw new Error('Changed OSC 9 adapter.');
    const fixture = osc9Fixture(config.runId), contracts = osc9Experiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: osc9Prefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableReasons.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads, facts, actions }) => ({ caseId, feeds, barriers,
        reads: reads.map((entry, index) => ({ ...entry, facts: facts[index] })), ...(actions ? { actions } : {}) }));
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...osc9CaseIds], experiments, pinned: await replayOsc9Pinned(gradingModule) } };
}
