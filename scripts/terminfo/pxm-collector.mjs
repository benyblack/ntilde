// Worker-side driver for the SGR pixel-coordinate mouse qualification. The generated events themselves run in the shared owned input collection
// (plan v5); this module then executes the PINNED termless probe (input.pixel-mouse) over the value served from the child's own DECRQM replies
// captured in that collection, never from App metadata. The parent observer journaled every reply; the parent report re-derives and re-executes
// everything below from those journals.
import { makePxmCases } from './pxm-cases.mjs';
import { pxmCaseIds, pxmPinned, pxmRecordingContext, servedPxm } from './pxm-contracts.mjs';

/** Run the pinned termless probe over its served value; refuse any probe that fed, captured or read anything outside its contract. */
export function collectPxmPinned({ geometry, controls, restored, gradingModule }) {
    const adapter = gradingModule.ntildePxmAdapter;
    if (!adapter || JSON.stringify(adapter.probeIds) !== JSON.stringify([...pxmCaseIds])) throw new Error('SGR-pixel adapter does not name the contracted probe.');
    makePxmCases(geometry.rows, geometry.cols);
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const caseId of pxmCaseIds) {
        const served = servedPxm(caseId, controls, restored);
        const context = pxmRecordingContext(caseId, served);
        const { probeId, ...pinned } = gradingModule.ntildeRunPxmTermless(caseId, context);
        if (probeId !== caseId || context.fedCount !== pxmPinned[caseId].feeds.length || context.captureCount !== 0) throw new Error(`Pinned ${caseId} did not replay its complete fixture.`);
        run.cases.push({ caseId, feeds: [...pxmPinned[caseId].feeds], served, pinned: { probeId, ...pinned } });
    }
    return run;
}
