// Worker-side driver for the OSC 633 qualification (extensions.osc633-a, -b, -c and -d). Replays every experiment's exact plan through the owned fence
// (shared graded-plan driver): the PINNED fixture writes from a measured baseline and from measured prior states, with unmarked controls and gating
// markers. Every read is journaled by the parent observer as a paired complete screen / pane-metadata / session-status capture. The pinned termless and
// terminal probes are then run over the journaled stock-context reads: the code the terminfo.dev bundle ships, never a reimplementation.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { osc633CaseIds } from './osc633-grading-collector.mjs';
import { osc133Fixture } from './osc133-contracts.mjs';
import { makeOsc633Experiments, osc633Pinned, osc633Prefix, osc633TerminalContext, osc633TermlessContext } from './osc633-contracts.mjs';

const unavailableCodes = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** Run every pinned probe path over the stock-context read of its case; refuse any probe that wrote or fed anything outside its contract. */
async function runPinned(gradingModule, collected) {
    const pinned = [];
    for (const caseId of osc633CaseIds) {
        const screen = collected.find(item => item.caseId === `${caseId}/stock`)?.screens[0];
        if (!screen) throw new Error(`Missing ${caseId} stock-context read.`);
        const termlessContext = osc633TermlessContext(caseId, screen), terminalContext = osc633TerminalContext(caseId, screen);
        const { probeId: termlessId, ...termless } = gradingModule.ntildeRunOsc633Termless(caseId, termlessContext);
        const { probeId: terminalId, ...terminal } = await gradingModule.ntildeRunOsc633Terminal(caseId, terminalContext); // NOSONAR pinned probes replay serially
        if (termlessId !== caseId || terminalId !== caseId || termlessContext.fedCount !== osc633Pinned[caseId].termlessFeeds.length
            || terminalContext.writtenCount !== osc633Pinned[caseId].terminalWrites.length) throw new Error(`Pinned ${caseId} did not replay its complete fixture.`);
        pinned.push({ caseId, termless: { feeds: [...osc633Pinned[caseId].termlessFeeds], result: termless },
            terminal: { writes: [...osc633Pinned[caseId].terminalWrites], result: terminal } });
    }
    return pinned;
}

/** Run the contracted experiments serially with parent-journaled reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectOsc633({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOsc633Adapter;
    if (adapter?.id !== 'ntilde-owned-osc633-grading-v1' || JSON.stringify(adapter.probeIds) !== JSON.stringify([...osc633CaseIds])) throw new Error('OSC 633 adapter does not name the contracted probes.');
    const fixture = osc133Fixture(config.runId), contracts = makeOsc633Experiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: osc633Prefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableCodes.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads }) => ({ caseId, feeds, barriers, reads }));
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], experiments, pinned: await runPinned(gradingModule, collected) } };
}
