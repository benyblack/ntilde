// A publication-only projection of the independently rederived retained owned run for the extended-mouse-encoding qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { mexCaseIds } from './mex-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = 'aaaece9966cd62ae3e0cbf9afe1c87a52fb120db';
export const measuredAppRevision = 'aaaece9966cd62ae3e0cbf9afe1c87a52fb120db';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

export function mexPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'mex-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedFeeds: item.pinnedFeeds, served: item.served,
        pinned: item.pinned, effectContract: item.effectContract, contractResult: item.contractResult, experiments: item.experiments, stock: stockOf(raw, item.caseId),
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...mexCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const header = Object.fromEntries(Object.entries(measured).filter(([key]) => key !== 'cases'));
    const report = { ...header, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run. input.urxvt-mouse stays inconclusive under the unchanged pinned code; modes.utf8-mouse-1005 is graded supported by the stock collector\'s DECRPM path (decrpmResult) because the production change answers DECRPM 1/2 for ?1005: a stock effect of the production change, a DECRQM recognition check that is neither an encoding measurement nor an upgrade of the pinned termless verdict or of the effect contract.' } };
    return { summary, report };
}
