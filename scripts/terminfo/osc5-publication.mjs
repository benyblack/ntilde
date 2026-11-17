// A publication-only projection of the independently rederived retained owned run for the rxvt special-color (OSC 5/105) qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { osc5CaseIds } from './osc5-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = 'd5548908e3db0e2aa4bc457f07516f8134ef80b4';
/** The last commit that changed production (src/): the sidecar App was built from this source and later commits changed only scripts and docs. */
export const measuredAppRevision = 'd5548908e3db0e2aa4bc457f07516f8134ef80b4';
const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc5-special-color (OSC 5;0;? BEL answered by one complete OSC 5;0;rgb:h{1,4}/h{1,4}/h{1,4} frame ended by BEL or ST; a query-only '
    + 'probe that can never return unsupported), decided by the unchanged stock collector against the App in this owned run, corroborated by the exact-query witness (query policy v6) and replayed '
    + 'byte-exactly on both pinned paths over the journaled raw reply of its own fenced exchange, beside a separately identified effect contract graded by an independent oracle written from rxvt\'s '
    + 'special-color text: chained numeric selector/value pairs (0 bold, 1 underline, 2 blink, 3 reverse, 4 italic) over a five-slot resource set separate from the OSC 4 palette, the OSC 10-19 dynamic '
    + 'colors and the kitty OSC 21 keys (each direction pinned by its own set/read group with the other service\'s measured default), per-selector and bare OSC 105 resets, the numeric encodings, reply '
    + 'order and the ST terminator; numeric selectors 0-4 only, ignored unmodeled selectors, malformed pairs, the oversize cap, set-only silence and the default-foreground slot default are policy groups '
    + 'that never decide; raw replies and stored state, not rendering or painted pixels; no setting of named colors, rxvt OSC 106 color modes, remote/mux, Windows/Linux or common-harness claim.';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

/** The exact-query witness of the pinned request, from the retained query report. */
function queryWitness(dir) {
    const witness = json(dir, 'query-observations.json').observations.find(item => item.id === 'q07.extensions.osc5-special-color');
    if (!witness) throw new Error('Missing the OSC 5 exact-query witness.');
    const { id, expectedEvidence, expectedPattern, receivedHex, contractMatched } = witness;
    return { id, expectedEvidence, expectedPattern, receivedHex, contractMatched };
}

export function osc5Publication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'osc5-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json'), witness = queryWitness(dir);
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract,
        observed: item.observed, pinned: item.pinned, contractResult: item.contractResult, stock: stockOf(raw, item.caseId), queryWitness: witness,
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...osc5CaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; the unchanged pinned probe now passes against the App, moving extensions.osc5-special-color from inconclusive to supported.' } };
    return { summary, report };
}
