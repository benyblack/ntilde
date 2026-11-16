// Projection only: the receipt and journal remain the authority for every value.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { osc633CaseIds } from './osc633-grading-collector.mjs';

export const measuredScriptsRevision = '9cedcc50e1f29206dcdc83e54d5b41a0fba77e7f';
export const measuredAppRevision = '9c692a04aa880372a9461be51e4144e307ab5c83';
const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
const scope = 'Pinned terminfo.dev@4.0.0 OSC 633 A–D termless and terminal marker-consumption probes replayed over parent-journaled stored cells and cursor; '
    + 'the unchanged pinned verdicts are always inconclusive. A separately identified lifecycle effect contract uses paired pane metadata and session status, '
    + 'prior-state, unmarked, positive OSC 133 and wrong-marker controls. ST and optional exit-code variants are observations only. Direct-local disposable App; '
    + 'no command exit-event payload, real shell, remote or mux, other platforms, common-harness or stock-score claim. '
    + 'OSC 9 and OSC 777/99 evidence is from the separately identified, independently revalidated predecessor run.';

export function osc633Publication(dir, identity = { scope, appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision }) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'vs633-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const cases = measured.cases.map(item => {
        const stockCase = raw.observations.find(value => value.featureId === item.caseId);
        if (!stockCase) throw new Error(`Missing stock outcome: ${item.caseId}`);
        const termless = item.pinned.termless.result, terminal = item.pinned.terminal.result;
        return { caseId: item.caseId, pinnedFixture: { termlessFeeds: item.pinned.termless.feeds, terminalWrites: item.pinned.terminal.writes },
            pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
            contractResult: item.contractResult, effectContract: item.effectContract, experiments: item.experiments, stock: stockCase,
            ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: { ...item.conclusion, evidence: 'owned-pane-metadata-and-session-status', note: termless.note } };
    });
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...osc633CaseIds])) throw new Error('Unexpected OSC 633 cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope: identity.scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision: identity.appRevision, scriptsRevision: identity.scriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision } };
    return { summary, report };
}
