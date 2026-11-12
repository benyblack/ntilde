// A publication-only projection of the independently rederived retained owned run.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = 'f2e5bad2e2188ba5420c19b21035780aa41a9e13';
export const measuredAppRevision = '2d8433d9bee2dc7fa15895935e08eefe7f21354b';
export function oscTitlePublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'ost-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const scope = 'Pinned termless and terminal paths over independently staged fixtures, plus versioned effect contracts for stored title and icon state and pane-metadata-and-session-status. No host icon presentation, remote behavior or stock-score inference.';
    const cases = measured.cases.map(item => {
        const termless = item.pinned.termless, pinnedOutcome = item.pinned.qualifiedOutcome, effect = item.effectContract.outcome;
        const stockObservation = raw.observations.find(entry => entry.featureId === item.caseId);
        if (!stockObservation) throw new Error(`Missing stock outcome: ${item.caseId}`);
        return { ...item, pinned: { ...termless, termless, terminal: item.pinned.terminal, qualifiedOutcome: pinnedOutcome },
            contractResult: { outcome: effect }, stock: stockObservation,
            ...(pinnedOutcome !== effect ? { divergence: { kind: 'pinned-termless-vs-effect', pinnedOutcome, contractOutcome: effect } } : {}),
            conclusion: { outcome: pinnedOutcome, evidence: 'stored-state and pane-metadata-and-session-status', grading: 'pinned-termless-replayed-over-owned-observations' } };
    });
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId,
        adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256,
            probeHash: raw.probeHash, sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades; owned effects do not change the stock score.' } };
    return { summary, report };
}
