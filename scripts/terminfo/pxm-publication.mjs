// A publication-only projection of the independently rederived retained owned run for the SGR pixel-coordinate mouse qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pxmCaseIds } from './pxm-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '9972a6fcf7649ac66140da7354b82020bc10b131';
export const measuredAppRevision = '9972a6fcf7649ac66140da7354b82020bc10b131';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

export function pxmPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'pxm-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedFeeds: item.pinnedFeeds, served: item.served,
        pinned: item.pinned, effectContract: item.effectContract, contractResult: item.contractResult, experiments: item.experiments, stock: stockOf(raw, item.caseId),
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...pxmCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const header = Object.fromEntries(Object.entries(measured).filter(([key]) => key !== 'cases'));
    const report = { ...header, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256,
            probeHash: raw.probeHash, sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run. input.pixel-mouse stays inconclusive under the unchanged pinned code; no stock grade changes with this layer.' } };
    return { summary, report };
}
