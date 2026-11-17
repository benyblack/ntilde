// A publication-only projection of the independently rederived retained owned run for the OSC 22 basic pointer-shape qualification (extensions.osc22-pointer).
// Projection only: the receipt and journal remain the authority for every value.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { osc22CaseIds } from './osc22-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
/** The scripts revision the measured run's private source snapshot was built from. The retained selected-source manifest independently pins the measured scripts; these labels are not binary-to-source attestation. */
export const measuredScriptsRevision = 'de218cd705ad8516d61f598fe29fa88648311702';
/** The measured App build revision. */
export const measuredAppRevision = 'de218cd705ad8516d61f598fe29fa88648311702';
const scope = 'Pinned terminfo.dev@4.0.0 extensions.osc22-pointer terminal path (no termless function) sends two exact writes then asks for CPR and always remains inconclusive. '
    + 'Separate ntilde-osc22-app-cursor-effect-v1 grades the main-screen requested shape and actual Avalonia Cursor object kind for pointer, text, crosshair, default and empty reset, '
    + 'BEL/ST and optional equals, with text delivery and invalid/advanced-form controls, complete stored-state checks and restoration. No native pointer image or location, full Kitty stacks/queries/other shapes, '
    + 'alternate-screen runtime, hover runtime, mux/replay, real emitters, other platforms, stock-score or common-harness claim.';

function stockObservation(raw, caseId) {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (observation === undefined) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
}

export function osc22Publication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision, publicationScope = scope } = {}) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'ptr-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId,
        pinned: { pass: item.pinned.terminal.pass, note: item.pinned.terminal.note, observation: item.pinned.terminal.observation, ...item.pinned }, contractResult: item.contractResult, effectContract: item.effectContract,
        experiments: item.experiments, stock: stockObservation(raw, item.caseId), ...(item.divergence === undefined ? {} : { divergence: item.divergence }),
        conclusion: { ...item.conclusion, evidence: 'owned-pane-metadata-and-session-status' } }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...osc22CaseIds])) throw new Error('Unexpected OSC 22 cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope: publicationScope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision, scriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision } };
    return { summary, report };
}
