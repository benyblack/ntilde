// Worker-side driver for the routed-input qualification. The generated events themselves run in the shared owned input collection
// (plan v2); this module then executes the PINNED termless probes (input.x10-mouse, input.button-event-mouse, modes.application-keypad)
// over mode values served from the child's own DECRQM replies captured in that collection, never from App metadata. The parent
// observer journaled every reply; the parent report re-derives and re-executes everything below from those journals.
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { routedInputCaseIds, routedInputPinned, routedInputRecordingContext, servedModes } from './routed-input-contracts.mjs';

/** Run each pinned termless probe over its served values; refuse any probe that fed or read anything outside its contract. */
export function collectRoutedInputPinned({ geometry, controls, restored, gradingModule }) {
    const adapter = gradingModule.ntildeRoutedInputAdapter;
    if (!adapter || JSON.stringify(adapter.probeIds) !== JSON.stringify([...routedInputCaseIds])) throw new Error('Routed-input adapter does not name the contracted probes.');
    const cases = makeRoutedInputCases(geometry.rows, geometry.cols);
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const caseId of routedInputCaseIds) {
        const served = servedModes(caseId, controls, restored, cases);
        const context = routedInputRecordingContext(caseId, served);
        const { probeId, ...pinned } = gradingModule.ntildeRunRoutedInputTermless(caseId, context);
        if (probeId !== caseId || context.fedCount !== routedInputPinned[caseId].feeds.length) throw new Error(`Pinned ${caseId} did not replay its complete fixture.`);
        run.cases.push({ caseId, feeds: [...routedInputPinned[caseId].feeds], served, pinned: { probeId, ...pinned } });
    }
    return run;
}
