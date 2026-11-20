// A publication-only projection of the independently rederived retained owned run for the xterm pointer-color qualification (extensions.osc113-reset-pointer-fg, extensions.osc114-reset-pointer-bg).
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pclCaseIds } from './pcl-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
/** The measured scripts revision, recorded after the measurement from the committed final scripts. */
export const measuredScriptsRevision = '0000000000000000000000000000000000000000';
/** The measured App build revision (stamped into the sidecar assembly). The retained selected-source manifest independently pins the measured scripts; these human-readable labels are not binary-to-source attestation. */
export const measuredAppRevision = '0000000000000000000000000000000000000000';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for extensions.osc113-reset-pointer-fg and extensions.osc114-reset-pointer-bg (colorResetProbe(13, 113) / (14, 114): the termless path queries OSC 13 / OSC 14 and '
    + 'is inconclusive without a complete rgb reply; the terminal path writes the reset, asks the cursor position and returns unverifiedEffect, inconclusive by construction), decided by the unchanged stock collector '
    + 'against the App in this owned run and replayed byte-exactly on both pinned paths over the journaled raw reply and cursor of their own fenced exchange, beside a separately identified effect contract graded by '
    + 'independent literals written from the seeded colors and the xterm chain order: every OSC 13 / OSC 14 query, set and OSC 113 / OSC 114 reset spelling, and a chained OSC 12 set through the pointer slots, is '
    + 'ignored (policy, #991: the App draws the operating system pointer and cannot recolor it), never answered, leaves the complete screen, cursor, rendition, margins and modes unchanged and leaves the foreground, '
    + 'background, cursor and selection colors exactly as seeded, with a cursor-color change read back as a control. Raw replies and stored terminal state, not the operating system pointer image, the OSC 22 shape, '
    + 'painted pixels, remote/mux, Windows/Linux or common-harness behavior; no stock-score claim.';

function stockObservation(raw, caseId) {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (observation === undefined) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
}

/** A projection admits only files bound to one cleaned-up owned run. Stock collection has its own run identity. */
function validatePublication(dir, context, stock, measured, raw, receipt) {
    const digest = file => createHash('sha256').update(readFileSync(join(dir, file))).digest('hex');
    const check = value => { if (!value) throw new Error('Inconsistent pointer-color publication run or receipt binding.'); };
    check(receipt.fixtureRemoved === true && receipt.runId === context.runId && measured.runId === context.runId);
    check(receipt.contextSha256 === digest('context.json') && receipt.rawSha256 === digest('raw.json') && receipt.ownershipProofSha256 === digest('ownership-proof.json'));
    check(receipt.pclEvidence?.runId === context.runId && receipt.pclEvidence.file === 'pcl-observations.json' && receipt.pclEvidence.sha256 === digest('pcl-observations.json'));
    const counts = raw.observations.reduce((out, item) => ({ ...out, [item.outcome]: (out[item.outcome] ?? 0) + 1 }), {});
    check(stock.runId === raw.runId && stock.rawSha256 === receipt.rawSha256 && isDeepStrictEqual(stock.counts, counts)
        && stock.observations === raw.observations.length && stock.suiteComplete === raw.suiteComplete && stock.measuredAt === raw.measuredAt);
}

function publishedCase(item, raw) {
    return { caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract, observed: item.observed, pinned: item.pinned,
        contractResult: item.contractResult, stock: stockObservation(raw, item.caseId), ...(item.divergence !== undefined ? { divergence: item.divergence } : {}), conclusion: item.conclusion };
}

export function pclPublication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision } = {}) {
    const context = json(dir, 'context.json'), stock = json(dir, 'summary.json');
    const measured = json(dir, 'pcl-observations.json'), raw = json(dir, 'raw.json'), receipt = json(dir, 'cell-receipt.json');
    validatePublication(dir, context, stock, measured, raw, receipt);
    const cases = measured.cases.map(item => publishedCase(item, raw));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...pclCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision, scriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
            note: 'Stock terminal-path grades from the same owned run; the pointer-color effect does not change the stock score.' } };
    return { summary, report };
}
