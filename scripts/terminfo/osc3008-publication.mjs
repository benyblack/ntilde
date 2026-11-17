// A publication-only projection of the independently rederived retained owned run for the OSC 3008 hierarchical-context qualification (extensions.osc3008-context).
// Projection only: the receipt and journal remain the authority for every value.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { osc3008CaseIds } from './osc3008-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
/** The scripts revision the measured run's private source snapshot was built from. The retained selected-source manifest independently pins the measured scripts; these labels are not binary-to-source attestation. */
export const measuredScriptsRevision = '0000000000000000000000000000000000000000';
/** The measured App build revision. */
export const measuredAppRevision = '0000000000000000000000000000000000000000';
const scope = 'Pinned terminfo.dev@4.0.0 extensions.osc3008-context (no termless function; the terminal path writes ESC[1;1H ESC[2K and OSC 3008;type=test BEL, queries the cursor and returns '
    + 'unmeasuredCellResult, inconclusive by construction; the payload is not a UAPI.15 start or end) replayed byte-exactly over the journaled cursor, with the unchanged pinned code re-run from '
    + 'the retained derived bundle, and preserved. Beside it a separately identified effect contract graded by an independent oracle written from UAPI.15 v1.0 over paired complete stored '
    + 'cells, pane metadata (including the untrusted stored context stack and the ended-record ring) and session status: start and end, nesting, the specification example, an update flushing '
    + 'metadata and ending sub-contexts, BEL and ST terminators, id bounds, field validity, escapes, end metadata, a soft reset leaving the stack alone, and negative controls (the pinned payload, '
    + 'foreign spellings, OSC 133/633 not creating a context and a context not moving the title, working directory or shell state). Stack depth, the ended ring, end-ends-sub-contexts, repeated '
    + 'fields and bare backslashes are policy groups that never decide. Stored untrusted metadata in a disposable direct-local App only: no presentation, command-assist or agent use, no hangup, '
    + 'RIS, alternate-screen or real-emitter claim, no remote/mux, other platforms, common-harness or stock-score claim; the ended ring and original null title/working directory are not restored.';

function stockObservation(raw, caseId) {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (observation === undefined) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
}

export function osc3008Publication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision, publicationScope = scope } = {}) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'ocx-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinned: item.pinned, contractResult: item.contractResult, effectContract: item.effectContract,
        experiments: item.experiments, stock: stockObservation(raw, item.caseId), ...(item.divergence === undefined ? {} : { divergence: item.divergence }),
        conclusion: { ...item.conclusion, evidence: 'owned-pane-metadata-and-session-status' } }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...osc3008CaseIds])) throw new Error('Unexpected OSC 3008 cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope: publicationScope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision, scriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision } };
    return { summary, report };
}
