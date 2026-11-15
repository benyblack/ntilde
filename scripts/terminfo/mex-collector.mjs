// Worker-side driver for the extended-mouse-encoding qualification. The generated events themselves run in the shared owned input
// collection (plan v4); this module then executes the PINNED termless probes (modes.utf8-mouse-1005, input.urxvt-mouse) over values
// served from the child's own DECRQM replies captured in that collection, never from App metadata. The parent observer journaled every
// reply; the parent report re-derives and re-executes everything below from those journals.
import { makeMexCases } from './mex-cases.mjs';
import { mexCaseIds, mexPinned, mexRecordingContext, servedMex } from './mex-contracts.mjs';

/** Run each pinned termless probe over its served values; refuse any probe that fed, captured or read anything outside its contract. */
export function collectMexPinned({ geometry, controls, restored, gradingModule }) {
    const adapter = gradingModule.ntildeMexAdapter;
    if (!adapter || JSON.stringify(adapter.probeIds) !== JSON.stringify([...mexCaseIds])) throw new Error('Extended-mouse adapter does not name the contracted probes.');
    makeMexCases(geometry.rows, geometry.cols);
    const run = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds], cases: [] };
    for (const caseId of mexCaseIds) {
        const served = servedMex(caseId, controls, restored);
        const context = mexRecordingContext(caseId, served);
        const { probeId, ...pinned } = gradingModule.ntildeRunMexTermless(caseId, context);
        if (probeId !== caseId || context.fedCount !== mexPinned[caseId].feeds.length || context.captureCount !== (caseId === 'modes.utf8-mouse-1005' ? 1 : 0)) {
            throw new Error(`Pinned ${caseId} did not replay its complete fixture.`);
        }
        run.cases.push({ caseId, feeds: [...mexPinned[caseId].feeds], served, pinned: { probeId, ...pinned } });
    }
    return run;
}
