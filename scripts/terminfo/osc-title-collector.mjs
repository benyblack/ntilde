import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { oscTitleCaseIds, oscTitleCases, oscTitleExperiments, oscTitleFixture, oscTitlePrefix } from './osc-title-contracts.mjs';

export const exposeOscTitleFacts = snapshot => ({ title: snapshot.metadata.oscTitle, icon: snapshot.metadata.buffer.requestedIconName ?? null });
const unavailableReasons = ['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable'];

/** A prerequisite may be skipped only through its typed initial refusal; otherwise it must have restored. */
export function oscTitlePrerequisiteReason(restored, unavailable, reason) {
    if (unavailable === true) {
        if (!unavailableReasons.includes(reason)) throw new Error('Unexpected OSC title prerequisite refusal.');
        return reason;
    }
    if (!restored) throw new Error('Title-stack suite must restore before OSC title observations.');
    return null;
}

/** Serve the pinned code only its own feed and the title reads on either side of it. */
export function oscTitleTermlessContext(caseId, before, after) {
    const spec = oscTitleCases[caseId];
    if (!spec || typeof before !== 'string' || typeof after !== 'string') throw new Error('Missing pinned title facts.');
    let feeds = 0, reads = 0;
    return { get fedCount() { return feeds; }, get readCount() { return reads; },
        feed(value) {
            if (feeds !== 0 || value !== spec.fixture || reads !== (spec.code === 2 ? 0 : 1)) throw new Error('Changed pinned feed or order.');
            feeds++;
        },
        getTitle() {
            if (spec.code === 2 ? feeds !== 1 || reads !== 0 : (reads === 0 ? feeds !== 0 : reads !== 1 || feeds !== 1)) {
                throw new Error('Changed pinned title read order.');
            }
            return reads++ === 0 && spec.code !== 2 ? before : after;
        } };
}

export async function collectOscTitle({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOscTitleAdapter;
    if (JSON.stringify(adapter?.probeIds) !== JSON.stringify(oscTitleCaseIds)) throw new Error('Changed OSC title adapter.');
    const fixture = oscTitleFixture(config.runId), contracts = oscTitleExperiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: oscTitlePrefix, reset: fixture.reset, bind: fixture.bind,
            finish: fixture.finish, nonce: fixture.nonce, fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableReasons.includes(error?.code)
            && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads, facts }) => ({ caseId, feeds, barriers,
        reads: reads.map((entry, index) => ({ ...entry, facts: facts[index] })) }));
    const pinned = [];
    for (const caseId of oscTitleCaseIds) {
        const item = experiments.find(entry => entry.caseId === `${caseId}/pinned`);
        const context = oscTitleTermlessContext(caseId, item.reads[0].facts.title, item.reads[1].facts.title);
        const { probeId, ...termless } = gradingModule.ntildeRunOscTitleTermless(caseId, context);
        const terminalContext = new Proxy({}, { get() { throw new Error('Unexpected pinned terminal-path access.'); } });
        const { probeId: terminalId, ...terminal } = await gradingModule.ntildeRunOscTitleTerminal(caseId, terminalContext);
        if (probeId !== caseId || terminalId !== caseId || context.fedCount !== 1 || context.readCount !== (oscTitleCases[caseId].code === 2 ? 1 : 2)) {
            throw new Error('Incomplete pinned OSC title replay.');
        }
        pinned.push({ caseId, termless, terminal });
    }
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...oscTitleCaseIds], experiments, pinned } };
}
