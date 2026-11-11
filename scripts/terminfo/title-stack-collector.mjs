// Worker-side driver for the XTWINOPS title/icon stack qualification (device.xtwinops-22 and device.xtwinops-23). Replays every experiment's exact plan through the
// owned fence (shared graded-plan driver): the PINNED fixtures write from a measured baseline, beside owned effect experiments (selectors, nesting, empty pops,
// bounded depth, alternate screens, DECSTR, RIS). Every read is journaled by the parent observer as a paired complete screen / pane-metadata / session-status
// capture, and the observer also hands the worker the raw title and icon name of that very observation. The pinned termless and terminal probes are then run over
// the journaled stock-context reads: the code the terminfo.dev bundle ships, never a reimplementation.
import { collectGradedPlans, planFeeds, planReads } from './graded-plan.mjs';
import { makeTitleStackExperiments, titleStackCaseIds, titleStackFixture, titleStackPinned, titleStackPrefix, titleStackTerminalContext, titleStackTermlessContext } from './title-stack-contracts.mjs';

const unavailableCodes = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** The journaled facts and cursor of one labelled read of one experiment. */
function readOf(collected, experimentId, label) {
    const item = collected.find(entry => entry.caseId === experimentId);
    const index = item?.reads.findIndex(entry => entry.kind === label) ?? -1;
    if (index < 0 || item.facts?.[index] === undefined) throw new Error(`Missing ${experimentId} ${label} read.`);
    return { facts: item.facts[index], cursor: item.reads[index].cursor };
}

/** Run one pinned case's probe paths over the stock-context reads; refuse any probe that wrote or fed anything outside its contract. */
async function runPinnedCase(gradingModule, collected, caseId) {
    const titles = Object.fromEntries(titleStackPinned[caseId].reads.map(([label]) => [label, readOf(collected, `${caseId}/stock`, label).facts.title]));
    const termlessContext = titleStackTermlessContext(caseId, titles);
    // The terminal path asks for the cursor after its own writes: the push case's writes are the first three feeds of its stock experiment (the read after them is
    // its changed read); the pop case's two writes run in their own experiment, which is read once right after them.
    const terminalRead = caseId === 'device.xtwinops-22' ? readOf(collected, `${caseId}/stock`, 'pinned-changed') : readOf(collected, `${caseId}/terminal-path`, 'pinned-terminal');
    const terminalContext = titleStackTerminalContext(caseId, terminalRead.cursor);
    const { probeId: termlessId, ...termless } = gradingModule.ntildeRunTitleStackTermless(caseId, termlessContext);
    const { probeId: terminalId, ...terminal } = await gradingModule.ntildeRunTitleStackTerminal(caseId, terminalContext);
    const pinned = titleStackPinned[caseId];
    if (termlessId !== caseId || terminalId !== caseId || termlessContext.fedCount !== pinned.termlessFeeds.length
        || termlessContext.readCount !== pinned.reads.length || terminalContext.writtenCount !== pinned.terminalWrites.length) {
        throw new Error(`Pinned ${caseId} did not replay its complete fixture.`);
    }
    return { caseId, termless: { feeds: [...pinned.termlessFeeds], result: termless }, terminal: { writes: [...pinned.terminalWrites], result: terminal } };
}

/** Run every pinned probe path over the stock-context reads of its case, in case order. */
const runPinned = (gradingModule, collected) => Promise.all(titleStackCaseIds.map(caseId => runPinnedCase(gradingModule, collected, caseId)));

/** The bounded facts an observation exposes beside its screen: the raw title and raw icon name of the same paired read. */
export const exposeTitleStackFacts = snapshot => ({ title: snapshot.metadata.oscTitle, icon: snapshot.metadata.buffer.requestedIconName ?? null });

/** Run the contracted experiments serially with parent-journaled reads. A typed refusal of the very first observation is the only optional outcome. */
export async function collectTitleStack({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeTitleStackAdapter;
    if (!adapter || JSON.stringify(adapter.probeIds) !== JSON.stringify([...titleStackCaseIds])) throw new Error('Title-stack adapter does not name the contracted probes.');
    const fixture = titleStackFixture(config.runId), contracts = makeTitleStackExperiments(config.runId);
    let collected;
    try {
        collected = await collectGradedPlans({ contracts, prefix: titleStackPrefix, reset: fixture.reset, bind: fixture.bind, finish: fixture.finish, nonce: fixture.nonce,
            fence, owner, planFeeds, planReads });
    } catch (error) {
        if (unavailableCodes.has(error?.code) && !error.message?.includes('after partial')) return { status: 'unavailable', reason: error.code };
        throw error;
    }
    const experiments = collected.map(({ caseId, feeds, barriers, reads, facts }) => ({ caseId, feeds, barriers,
        reads: reads.map((entry, index) => ({ ...entry, facts: facts[index] })) }));
    return { status: 'complete', run: { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], experiments, pinned: await runPinned(gradingModule, collected) } };
}
