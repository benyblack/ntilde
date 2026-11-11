// Worker-side driver for the OSC 133 qualification (extensions.osc133-a, -b, -c and -d). Replays every experiment's exact plan through the owned fence
// (shared graded-plan driver): the PINNED fixture writes from a measured baseline and from measured prior states, with unmarked controls and gating
// markers. Every read is journaled by the parent observer as a paired complete screen / pane-metadata / session-status capture. The pinned termless and
// terminal probes are then run over the journaled stock-context reads: the code the terminfo.dev bundle ships, never a reimplementation.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { makeOsc133Experiments, osc133CaseIds, osc133Fixture, osc133Pinned, osc133Prefix, osc133TerminalContext, osc133TermlessContext } from './osc133-contracts.mjs';

const unavailableCodes = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** Run every pinned probe path over the stock-context read of its case; refuse any probe that wrote or fed anything outside its contract. */
async function runPinned(gradingModule, collected) {
    const pinned = [];
    for (const caseId of osc133CaseIds) {
        const screen = collected.find(item => item.caseId === `${caseId}/stock`)?.screens[0];
        if (!screen) throw new Error(`Missing ${caseId} stock-context read.`);
        const termlessContext = osc133TermlessContext(caseId, screen), terminalContext = osc133TerminalContext(caseId, screen);
        const { probeId: termlessId, ...termless } = gradingModule.ntildeRunOsc133Termless(caseId, termlessContext);
        const { probeId: terminalId, ...terminal } = await gradingModule.ntildeRunOsc133Terminal(caseId, terminalContext);
        if (termlessId !== caseId || terminalId !== caseId || termlessContext.fedCount !== osc133Pinned[caseId].termlessFeeds.length
            || terminalContext.writtenCount !== osc133Pinned[caseId].terminalWrites.length) throw new Error(`Pinned ${caseId} did not replay its complete fixture.`);
        pinned.push({ caseId, termless: { feeds: [...osc133Pinned[caseId].termlessFeeds], result: termless },
            terminal: { writes: [...osc133Pinned[caseId].terminalWrites], result: terminal } });
    }
    return pinned;
}

/** Run the contracted experiments serially with parent-journaled reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectOsc133({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOsc133Adapter;
    if (!adapter || JSON.stringify(adapter.probeIds) !== JSON.stringify([...osc133CaseIds])) throw new Error('OSC 133 adapter does not name the contracted probes.');
    const fixture = osc133Fixture(config.runId), contracts = makeOsc133Experiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: osc133Prefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableCodes.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads }) => ({ caseId, feeds, barriers, reads }));
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], experiments, pinned: await runPinned(gradingModule, collected) } };
}
