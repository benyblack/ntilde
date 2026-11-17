// A publication-only projection of the independently rederived retained owned run for the rxvt special-color (OSC 5/105) qualification.
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { osc5CaseIds } from './osc5-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '9c922a4a3857fb109ba1ef7978a35ae013a8e63f';
/** The batch commit whose src/ tree the sidecar App was built from: the OSC 5 implementation landed there, and the measured run then exercised exactly that build. The retained
 *  source-snapshot manifest (context.sourceSnapshotManifestSha256) pins the precise measured tree besides these human-readable revisions. */
export const measuredAppRevision = '9c922a4a3857fb109ba1ef7978a35ae013a8e63f';
const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc5-special-color (OSC 5;0;? BEL answered by one complete OSC 5;0;rgb:h{1,4}/h{1,4}/h{1,4} frame ended by BEL or ST; a query-only '
    + 'probe that can never return unsupported), decided by the unchanged stock collector against the App in this owned run, corroborated by the exact-query witness (query policy v6) and replayed '
    + 'byte-exactly on both pinned paths over the journaled raw reply of its own fenced exchange, beside a separately identified effect contract graded by an independent oracle written from rxvt\'s '
    + 'special-color text: chained numeric selector/value pairs (0 bold, 1 underline, 2 blink, 3 reverse, 4 italic) over a five-slot resource set separate from the OSC 4 palette, the OSC 10-19 dynamic '
    + 'colors and the kitty OSC 21 keys (each direction pinned by its own set/read group with the other service\'s measured default), per-selector and bare OSC 105 resets, the numeric encodings, reply '
    + 'order and the ST terminator; numeric selectors 0-4 only, ignored unmodeled selectors, malformed pairs, the oversize cap, set-only silence and the default-foreground slot default are policy groups '
    + 'that never decide; raw replies and stored state, not rendering or painted pixels; no setting of named colors, rxvt OSC 106 color modes, remote/mux, Windows/Linux or common-harness claim.';
function stockObservation(raw, caseId) {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (observation === undefined) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
}

/** The exact-query witness of the pinned request, from the retained query report. */
function queryWitness(dir) {
    const observations = json(dir, 'query-observations.json').observations;
    const witness = observations.find(item => item.id === 'q07.extensions.osc5-special-color');
    if (witness === undefined) throw new Error('Missing the OSC 5 exact-query witness.');
    return { id: witness.id, expectedEvidence: witness.expectedEvidence, expectedPattern: witness.expectedPattern,
        receivedHex: witness.receivedHex, contractMatched: witness.contractMatched };
}

function publishedCase(item, raw, witness) {
    return { caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract,
        observed: item.observed, pinned: item.pinned, contractResult: item.contractResult, stock: stockObservation(raw, item.caseId), queryWitness: witness,
        ...(item.divergence !== undefined ? { divergence: item.divergence } : {}), conclusion: item.conclusion };
}

/** A projection admits only files bound to one cleaned-up owned run. Stock collection has its own run identity. */
function validatePublication(dir, context, stock, measured, raw, receipt) {
    const digest = file => createHash('sha256').update(readFileSync(join(dir, file))).digest('hex');
    const check = value => { if (!value) throw new Error('Inconsistent special-color publication run or receipt binding.'); };
    check(receipt.fixtureRemoved === true && receipt.runId === context.runId && measured.runId === context.runId);
    check(receipt.contextSha256 === digest('context.json') && receipt.rawSha256 === digest('raw.json')
        && receipt.ownershipProofSha256 === digest('ownership-proof.json'));
    check(receipt.osc5Evidence?.runId === context.runId && receipt.osc5Evidence.file === 'osc5-observations.json'
        && receipt.osc5Evidence.sha256 === digest('osc5-observations.json'));
    const query = json(dir, 'query-observations.json');
    check(query.runId === context.runId && receipt.queryEvidence?.runId === context.runId && receipt.queryEvidence.file === 'query-observations.json'
        && receipt.queryEvidence.sha256 === digest('query-observations.json'));
    const counts = raw.observations.reduce((out, item) => ({ ...out, [item.outcome]: (out[item.outcome] ?? 0) + 1 }), {});
    check(stock.runId === raw.runId && stock.rawSha256 === receipt.rawSha256 && isDeepStrictEqual(stock.counts, counts)
        && stock.observations === raw.observations.length && stock.suiteComplete === raw.suiteComplete && stock.measuredAt === raw.measuredAt);
}

export function osc5Publication(dir) {
    const context = json(dir, 'context.json'), stock = json(dir, 'summary.json');
    const measured = json(dir, 'osc5-observations.json'), raw = json(dir, 'raw.json'), receipt = json(dir, 'cell-receipt.json');
    validatePublication(dir, context, stock, measured, raw, receipt);
    const witness = queryWitness(dir);
    const cases = measured.cases.map(item => publishedCase(item, raw, witness));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...osc5CaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; the unchanged pinned probe now passes against the App, moving extensions.osc5-special-color from inconclusive to supported.' } };
    return { summary, report };
}
