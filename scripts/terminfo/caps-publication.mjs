// A publication-only projection of the independently rederived retained owned run for the iTerm2 feature-reporting (OSC 1337 RequestCapabilities/Capabilities) qualification.
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { capsCaseIds } from './caps-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '7bfbbdc1341509557780931d1a02f80dec4e80c3';
/** The measured App build revision. The retained selected-source manifest independently pins the measured scripts; these human-readable labels are not binary-to-source attestation. */
export const measuredAppRevision = '7bfbbdc1341509557780931d1a02f80dec4e80c3';
const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc1337-capabilities (OSC 1337 ; RequestCapabilities BEL answered by one complete ESC]1337;Capabilities=<characters without BEL or ESC> '
    + 'frame ended by BEL or ST; a query-only probe that can never return unsupported and whose valid pattern accepts any feature string), decided by the unchanged stock collector against the App in this '
    + 'owned run, corroborated by the exact-query witness (query policy v7) and replayed byte-exactly on both pinned paths over the journaled raw reply of its own fenced exchange, beside a separately '
    + 'identified effect contract graded by an independent literal written from the iTerm2 feature-reporting specification\'s code table over this terminal\'s implemented, tested capabilities: the exact '
    + 'pinned request, the same request ended by ST answered by the same ST-terminated reply, two requests answered by two identical replies in request order, and silence for an unsolicited Capabilities '
    + 'assignment, a bare or query Capabilities form, a suffixed or wrong-case request and another OSC, with the exact request still answered after them; every exchange must leave the complete screen, '
    + 'cursor, rendition, margins, modes and active screen unchanged. The measured run\'s direct pane accepts OSC 52 writes under its default initial settings, so the declaration includes Cw; a host that '
    + 'cannot answer omits it (policy, unit-tested, not measured here). The declaration advertises implemented capabilities only — Aw, Uw and the measured-unsupported Sx stay out; the per-code behavior of '
    + 'every declared capability is owned by that capability\'s own implementations and tests; raw replies and stored state, not rendering or painted pixels; no remote/mux, Windows/Linux or common-harness claim.';
function stockObservation(raw, caseId) {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (observation === undefined) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
}

/** The exact-query witness of the pinned request, from the retained query report. */
function queryWitness(dir) {
    const observations = json(dir, 'query-observations.json').observations;
    const witness = observations.find(item => item.id === 'q07.extensions.osc1337-capabilities');
    if (witness === undefined) throw new Error('Missing the feature-reporting exact-query witness.');
    return { id: witness.id, expectedEvidence: witness.expectedEvidence, receivedHex: witness.receivedHex, contractMatched: witness.contractMatched };
}

function publishedCase(item, raw, witness) {
    return { caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract,
        observed: item.observed, pinned: item.pinned, contractResult: item.contractResult, stock: stockObservation(raw, item.caseId), queryWitness: witness,
        ...(item.divergence !== undefined ? { divergence: item.divergence } : {}), conclusion: item.conclusion };
}

/** A projection admits only files bound to one cleaned-up owned run. Stock collection has its own run identity. */
function validatePublication(dir, context, stock, measured, raw, receipt) {
    const digest = file => createHash('sha256').update(readFileSync(join(dir, file))).digest('hex');
    const check = value => { if (!value) throw new Error('Inconsistent feature-reporting publication run or receipt binding.'); };
    check(receipt.fixtureRemoved === true && receipt.runId === context.runId && measured.runId === context.runId);
    check(receipt.contextSha256 === digest('context.json') && receipt.rawSha256 === digest('raw.json')
        && receipt.ownershipProofSha256 === digest('ownership-proof.json'));
    check(receipt.capsEvidence?.runId === context.runId && receipt.capsEvidence.file === 'caps-observations.json'
        && receipt.capsEvidence.sha256 === digest('caps-observations.json'));
    const query = json(dir, 'query-observations.json');
    check(query.runId === context.runId && receipt.queryEvidence?.runId === context.runId && receipt.queryEvidence.file === 'query-observations.json'
        && receipt.queryEvidence.sha256 === digest('query-observations.json'));
    const counts = raw.observations.reduce((out, item) => ({ ...out, [item.outcome]: (out[item.outcome] ?? 0) + 1 }), {});
    check(stock.runId === raw.runId && stock.rawSha256 === receipt.rawSha256 && isDeepStrictEqual(stock.counts, counts)
        && stock.observations === raw.observations.length && stock.suiteComplete === raw.suiteComplete && stock.measuredAt === raw.measuredAt);
}

export function capsPublication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision } = {}) {
    const context = json(dir, 'context.json'), stock = json(dir, 'summary.json');
    const measured = json(dir, 'caps-observations.json'), raw = json(dir, 'raw.json'), receipt = json(dir, 'cell-receipt.json');
    validatePublication(dir, context, stock, measured, raw, receipt);
    const witness = queryWitness(dir);
    const cases = measured.cases.map(item => publishedCase(item, raw, witness));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...capsCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision,
        scriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; the unchanged pinned probe now passes against the App, moving extensions.osc1337-capabilities from inconclusive to supported.' } };
    return { summary, report };
}
