// A publication-only projection of the independently rederived retained owned run.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '869194d4f516cbb8cd781b3a8e0fd9a7fdb4322d';
export const measuredAppRevision = 'b2121100df7c76773e1a15ba569cb3f5526209dc';
export function osc9Publication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'osn-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json'), focus = json(dir, 'osn-focus.json');
    const scope = 'Pinned termless and terminal paths (neither sends or reads a byte) preserved as inconclusive, plus versioned effect contracts for stored OSC 9;4 progress state, owner and actual tab suffix, and for the private in-window toast with its window-focus and opt-in preconditions, over pane-metadata-and-session-status. No OS taskbar or desktop notification delivery, remote behavior or stock-score inference.';
    const cases = measured.cases.map(item => {
        const { termless, terminal, termlessFunction } = item.pinned, effect = item.effectContract.outcome;
        const stockObservation = raw.observations.find(entry => entry.featureId === item.caseId);
        if (!stockObservation) throw new Error(`Missing stock outcome: ${item.caseId}`);
        const decided = termlessFunction ? termless : terminal;
        return { caseId: item.caseId, effectContract: item.effectContract, experiments: item.experiments, policy: item.policy,
            pinned: { termlessFunction, termless, terminal, observation: decided.observation, verdictSource: termlessFunction ? 'termless' : 'terminal (the registration has no termless function)',
                qualifiedOutcome: item.pinned.qualifiedOutcome, note: decided.note },
            contractResult: { outcome: effect }, stock: stockObservation,
            ...(item.pinned.qualifiedOutcome !== effect ? { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: item.pinned.qualifiedOutcome, contractOutcome: effect } } : {}),
            conclusion: { outcome: item.pinned.qualifiedOutcome, evidence: 'stored-state and pane-metadata-and-session-status', grading: 'pinned-preserved-over-owned-observations' } };
    });
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId,
        adapter: measured.adapter, scope, focusActions: focus, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256,
            probeHash: raw.probeHash, sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades; owned effects do not change the stock score.' } };
    return { summary, report };
}
