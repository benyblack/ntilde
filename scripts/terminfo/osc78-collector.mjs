// Worker-side driver for the OSC 7 / OSC 8 qualification (extensions.osc7-cwd and extensions.osc8). Replays every experiment's exact plan through the owned
// fence (shared graded-plan driver) from a measured baseline and from measured prior states, with unmarked controls and gating bytes. Every read is journaled
// by the parent observer as a paired complete screen / pane-metadata / session-status capture. The pinned termless and terminal probes are then run over the
// journaled stock-context reads: the code the terminfo.dev bundle ships, never a reimplementation. The pinned OSC 7 probe writes and reads nothing.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { makeOsc78Experiments, osc78CaseIds, osc78Fixture, osc78Pinned, osc78Prefix, osc78TerminalContext, osc78TermlessContext } from './osc78-contracts.mjs';

const unavailableCodes = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** Run every pinned probe path over the stock-context read of its case; refuse any probe that wrote or fed anything outside its contract. `linksOf(caseId)` is
 * the hyperlink runs the parent observer reported with that read (the stored-cell link metadata the pinned OSC 8 termless probe reads). */
async function runPinned(gradingModule, collected, linksOf) {
    return Promise.all(osc78CaseIds.map(async caseId => {
        const stock = collected.find(item => item.caseId === `${caseId}/stock`);
        if (!stock?.screens[0]) throw new Error(`Missing ${caseId} stock-context read.`);
        const termlessContext = osc78TermlessContext(caseId, stock.screens[0], linksOf(caseId)), terminalContext = osc78TerminalContext(caseId, stock.screens[0]);
        const { probeId: termlessId, ...termless } = gradingModule.ntildeRunOsc78Termless(caseId, termlessContext);
        const { probeId: terminalId, ...terminal } = await gradingModule.ntildeRunOsc78Terminal(caseId, terminalContext);
        if (termlessId !== caseId || terminalId !== caseId || termlessContext.fedCount !== osc78Pinned[caseId].termlessFeeds.length
            || terminalContext.writtenCount !== osc78Pinned[caseId].terminalWrites.length) throw new Error(`Pinned ${caseId} did not replay its complete fixture.`);
        return { caseId, termless: { feeds: [...osc78Pinned[caseId].termlessFeeds], result: termless },
            terminal: { writes: [...osc78Pinned[caseId].terminalWrites], result: terminal } };
    }));
}

/** The hyperlink runs of each case's stock read: reads are journaled in contract order, so the stock read's position is the count of reads before it. */
function linksOf(contracts, linkReads) {
    const position = new Map();
    let reads = 0;
    for (const contract of contracts) {
        if (contract.name === 'stock') position.set(contract.owner, reads);
        reads += planReads(contract);
    }
    return caseId => {
        const links = linkReads[position.get(caseId)];
        if (!Array.isArray(links)) throw new Error(`Missing ${caseId} stock-context hyperlink runs.`);
        return links;
    };
}

/** Run the contracted experiments serially with parent-journaled reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectOsc78({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeOsc78Adapter;
    if (!adapter || JSON.stringify(adapter.probeIds) !== JSON.stringify([...osc78CaseIds])) throw new Error('OSC 7/8 adapter does not name the contracted probes.');
    const fixture = osc78Fixture(config.runId), contracts = makeOsc78Experiments(config.runId);
    // The parent attaches the read's hyperlink runs to its answer; they are kept here, in read order, beside the shared driver's screens.
    const linkReads = [];
    const journalingOwner = async (kind, barrier) => {
        const answer = await owner(kind, barrier);
        if (kind === `${osc78Prefix}-read`) linkReads.push(answer.links);
        return answer;
    };
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: osc78Prefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner: journalingOwner, planFeeds, planReads });
    } catch (error) {
        if (unavailableCodes.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads }) => ({ caseId, feeds, barriers, reads }));
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], experiments, pinned: await runPinned(gradingModule, collected, linksOf(contracts, linkReads)) } };
}
