// Worker-side driver for the modifyOtherKeys qualification. The declared events themselves run in the shared owned input
// collection (plan v3); this module then executes the PINNED termless probe (input.modify-other-keys) over the declaration
// value the child itself answered (its XTGETTCAP refusal/advertisement for the modifyOtherKeys capability name, captured in
// the plan's declaration case), never from App metadata. The parent observer journaled every reply; the parent report
// re-derives and re-executes everything below from those journals.
import { makeModifyOtherKeysCases } from './modify-other-keys-cases.mjs';
import { modifyOtherKeysCaseIds, modifyOtherKeysPinned, modifyOtherKeysRecordingContext, servedDeclaration } from './modify-other-keys-contracts.mjs';

const declarationCaseId = 'input.mok.declaration.xtgettcap';

/** Run the pinned termless probe over its served declaration; refuse any probe that fed or read anything outside its contract. */
export function collectModifyOtherKeysPinned({ geometry, controls, gradingModule }) {
    const adapter = gradingModule.ntildeModifyOtherKeysAdapter;
    if (!adapter || JSON.stringify(adapter.probeIds) !== JSON.stringify([...modifyOtherKeysCaseIds])) throw new Error('modifyOtherKeys adapter does not name the contracted probes.');
    makeModifyOtherKeysCases(geometry.rows, geometry.cols);
    const served = servedDeclaration(controls?.[declarationCaseId]);
    const context = modifyOtherKeysRecordingContext(served);
    const { probeId, ...pinned } = gradingModule.ntildeRunModifyOtherKeysTermless(modifyOtherKeysCaseIds[0], context);
    if (probeId !== modifyOtherKeysCaseIds[0] || context.readCount !== 1) throw new Error(`Pinned ${modifyOtherKeysCaseIds[0]} did not replay its complete fixture.`);
    return { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds],
        cases: [{ caseId: modifyOtherKeysCaseIds[0], declarationCase: declarationCaseId, feeds: [...modifyOtherKeysPinned[modifyOtherKeysCaseIds[0]].feeds], served, pinned: { probeId, ...pinned } }] };
}
