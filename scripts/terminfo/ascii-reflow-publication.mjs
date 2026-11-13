// A publication-only projection of the independently rederived retained owned run.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = 'e8c8e9dbae96500d421b57e61a9eae81d1af8d4e';
export const measuredAppRevision = 'e8c8e9dbae96500d421b57e61a9eae81d1af8d4e';
export function asciiReflowPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'reflow-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const scope = 'Pinned termless and terminal paths (always inconclusive by design, no fixture, no resize request, no cell readback) replayed byte-exactly over the journaled declared capability, plus the versioned effect contract for controlled ASCII reflow in a disposable main screen buffer: two parent-owned shrink/restore experiments graded from paired complete stored cells, cursor, modes, attributes and complete ordered history at three actual geometries, with independent kernel geometry and output-processing proof per action. No Unicode, wide-cell, grapheme or history-attribute claim, no rendered appearance, no stock-score inference.';
    const cases = measured.cases.map(item => {
        const effect = item.effectContract.outcome;
        const stockObservation = raw.observations.find(entry => entry.featureId === item.caseId);
        if (!stockObservation) throw new Error(`Missing stock outcome: ${item.caseId}`);
        return { ...item, contractResult: { outcome: effect }, stock: stockObservation,
            ...(effect !== item.pinned.observation.outcome ? { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: item.pinned.observation.outcome, contractOutcome: effect } } : {}),
            conclusion: { outcome: item.pinned.observation.outcome, evidence: 'stored-state and actual-scrollback', grading: 'pinned-termless-preserved-by-design',
                note: item.pinned.note ?? null, scope } };
    });
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId,
        adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256,
            probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
            note: 'Stock terminal-path grades collected in the same owned run; the owned effect verdict does not change the stock score.' } };
    return { summary, report };
}
