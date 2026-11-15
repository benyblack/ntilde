// A publication-only projection of the independently rederived retained owned run for the device-query qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { devqCaseIds } from './devq-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '8692e250b6199ba909dbf8f40c99bda906571c39';
export const measuredAppRevision = '8692e250b6199ba909dbf8f40c99bda906571c39';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for device.xtgetxres (DCS + Q termName), device.xtreportcolors (CSI # R, whose pinned pattern has no ? marker and cannot match xterm\'s own CSI ? depth ; deepest # Q reply), '
    + 'device.xtwinops-20 and device.xtwinops-21 (OSC 1 / OSC 2 then CSI 20 t / 21 t, with the run\'s initial settings turning on AllowTitleReport) replayed byte-exactly over the journaled raw replies of their own fenced '
    + 'exchanges, beside separately identified effect contracts graded by an independent oracle written from xterm\'s text and source; raw terminal state, not host presentation; termName is the documented session TERM, not '
    + 'read from the child; the default-off title gate is unit-tested, not measured; the xterm stack\'s deepest counter stays at its measured value in the disposable App; no remote/mux, Windows/Linux or common-harness claim. '
    + 'device.term-features is documented only: its pinned probe reads process.env.TERM_FEATURES, emits no request and can never return supported.';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

/** The exact-query witness of one pinned request, from the retained query report. */
function queryWitness(queries, caseId) {
    const id = `q07.${caseId}`;
    const witness = queries.observations.find(item => item.id === id);
    if (!witness) throw new Error(`Missing the exact-query witness ${id}.`);
    const { expectedEvidence, expectedHex, receivedHex, contractMatched } = witness;
    return { id, expectedEvidence, expectedHex, receivedHex, contractMatched };
}

export function devqPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'dvq-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json'), queries = json(dir, 'query-observations.json');
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedFeeds: item.pinnedFeeds, effectContract: item.effectContract, observed: item.observed,
        pinned: item.pinned, contractResult: item.contractResult, stock: stockOf(raw, item.caseId), queryWitness: queryWitness(queries, item.caseId),
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...devqCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope,
        termFeatures: { caseId: 'device.term-features', disposition: 'documented-only', stock: stockOf(raw, 'device.term-features') }, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; the run turns on AllowTitleReport, so the stock probes for the title reports and XTGETXRES now pass against the unchanged pinned code.' } };
    return { summary, report };
}
