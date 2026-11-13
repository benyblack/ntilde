// A publication-only projection of the independently rederived retained owned run.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));

export const measuredScriptsRevision = 'e6c4571ba48c1d716feb18d9c208695637ca587d';
export const measuredAppRevision = '5a20054d70ff72b0953ea0b342381d93035b7571';
export function selectionColorsPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'selc-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const scope = 'Pinned selection-color termless paths over independently journaled fixtures, plus versioned effect contracts for exact fenced replies, app-resolved colors, stored cells and controlled renders with the established synthetic selection. Unset selection sources reply nothing behind the completed ordering barrier and are never substituted with an RGB value. No physical mouse selection, native desktop appearance, pointer color, clipboard behavior or stock-score inference';
    const cases = measured.cases.map(item => {
        const pinnedOutcome = item.conclusion.pinnedTermless, effect = item.contractResult.outcome;
        const stockObservation = raw.observations.find(entry => entry.featureId === item.caseId);
        if (!stockObservation) throw new Error(`Missing stock outcome: ${item.caseId}`);
        return { ...item, stock: stockObservation,
            ...(pinnedOutcome !== effect ? { divergence: item.divergence ?? { pinnedOutcome, contractOutcome: effect } } : {}) };
    });
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId,
        adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256,
            probeHash: raw.probeHash, sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades; owned effects do not change the stock score.' } };
    return { summary, report };
}
