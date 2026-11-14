// A publication-only projection of the independently rederived retained owned run.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { osc777CaseId, osc99CaseId } from './notify-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '2eb47f2816a46967ea6bd6997ee876ebd01f8b56';
export const measuredAppRevision = '2eb47f2816a46967ea6bd6997ee876ebd01f8b56';
const scope = 'Pinned OSC 777 terminal path (two writes and a cursor query, inconclusive by construction) replayed and preserved; pinned OSC 99 query decided by the unchanged stock probe in the same owned run and by the exact-query witness q07.extensions.osc99-kitty-notify (query policy v3), plus versioned effect contracts for the private in-window toast (title, message, visibility, auto-hide) with its window-focus and opt-in preconditions, over paired complete stored cells and pane metadata; no OS desktop notification delivery, no OSC 99 actions, close reports, icons, buttons, urgency, sounds or expiry, no real-program emission, no remote/mux, Windows/Linux, hidden-pane or unobserved focus-change claim';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

/** The exact-query witness of the OSC 99 pinned request, from the retained query report. */
function queryWitness(dir) {
    const witness = json(dir, 'query-observations.json').observations.find(item => item.id === 'q07.extensions.osc99-kitty-notify');
    if (!witness) throw new Error('Missing the OSC 99 exact-query witness.');
    const { id, expectedEvidence, expectedHex, receivedHex, contractMatched } = witness;
    return { id, expectedEvidence, expectedHex, receivedHex, contractMatched };
}

function pinnedOf(item, stock, witness) {
    if (item.caseId === osc777CaseId) {
        const { terminal, termlessFunction, termless } = item.pinned;
        return { termlessFunction, termless, terminal, observation: terminal.observation, verdictSource: 'terminal (the registration has no termless function)',
            qualifiedOutcome: item.pinned.qualifiedOutcome, note: terminal.note, writes: item.pinned.writes, cursor: item.pinned.cursor };
    }
    return { termlessFunction: false, termless: null, terminal: null, observation: { outcome: stock.outcome, ...(stock.reason === undefined ? {} : { reason: stock.reason }), evidence: stock.evidence, note: stock.note },
        verdictSource: 'stock collector (the unchanged pinned probe) against the App in this owned run, corroborated by the exact-query witness', qualifiedOutcome: stock.outcome, note: stock.note, queryWitness: witness };
}

export function notifyPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'ntf-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json'), focus = json(dir, 'ntf-focus.json'), witness = queryWitness(dir);
    const cases = measured.cases.map(item => {
        const effect = item.effectContract.outcome, stockObservation = stockOf(raw, item.caseId), pinned = pinnedOf(item, stockObservation, witness);
        const is777 = item.caseId === osc777CaseId;
        return { caseId: item.caseId, effectContract: item.effectContract, experiments: item.experiments, policy: item.policy, pinned, contractResult: { outcome: effect }, stock: stockObservation,
            ...(pinned.qualifiedOutcome !== effect ? { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: pinned.qualifiedOutcome, contractOutcome: effect } } : {}),
            conclusion: is777 ? { outcome: pinned.qualifiedOutcome, evidence: 'stored-state and pane-metadata-and-session-status', grading: 'pinned-preserved-over-owned-observations' }
                : { outcome: pinned.qualifiedOutcome, evidence: 'stored-state and pane-metadata-and-session-status (effect); stock query reply (pinned)', grading: 'pinned-stock-collector-and-exact-query-witness' } };
    });
    if (!cases.some(item => item.caseId === osc99CaseId)) throw new Error('Missing the OSC 99 case.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId,
        adapter: measured.adapter, scope, focusActions: focus, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId,
        appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256,
            probeHash: raw.probeHash, sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades; owned effects do not change the stock score except where the unchanged stock probe itself now passes.' } };
    return { summary, report };
}
