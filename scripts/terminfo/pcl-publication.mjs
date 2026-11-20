// A publication-only projection of the independently rederived retained owned run for the xterm pointer-color qualification (extensions.osc113-reset-pointer-fg, extensions.osc114-reset-pointer-bg).
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pclCaseIds } from './pcl-contracts.mjs';

/** The measured scripts and App revisions (the App build is stamped into the sidecar assembly; the retained selected-source manifest independently pins the measured scripts). */
export const measuredScriptsRevision = 'b76e6801498aa33311b2b2206ba916724ad81b7e';
export const measuredAppRevision = '67675feecc6da035338c36dbb8d03198e6f5b271';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for extensions.osc113-reset-pointer-fg and extensions.osc114-reset-pointer-bg (colorResetProbe(13, 113) / (14, 114): the termless path queries OSC 13 / OSC 14 and '
    + 'is inconclusive without a complete rgb reply; the terminal path writes the reset, asks the cursor position and returns unverifiedEffect, inconclusive by construction), decided by the unchanged stock collector '
    + 'against the App in this owned run and replayed byte-exactly on both pinned paths over the journaled raw reply and cursor of their own fenced exchange, beside a separately identified effect contract graded by '
    + 'independent literals written from the seeded colors and the xterm chain order: every OSC 13 / OSC 14 query, set and OSC 113 / OSC 114 reset spelling, and a chained OSC 12 set through the pointer slots, is '
    + 'ignored (policy, #991: the App draws the operating system pointer and cannot recolor it), never answered, leaves the complete screen, cursor, rendition, margins and modes unchanged and leaves the foreground, '
    + 'background, cursor and selection colors exactly as seeded, with a cursor-color change read back as a control. Raw replies and stored terminal state, not the operating system pointer image, the OSC 22 shape, '
    + 'painted pixels, remote/mux, Windows/Linux or common-harness behavior; no stock-score claim.';


/** One cleaned-up owned run binds every projected file; the stock collector run has its own identity and must agree with its raw output. */
function admit(dir, { context, stock, measured, raw, receipt }) {
    const digest = file => createHash('sha256').update(readFileSync(join(dir, file))).digest('hex');
    const evidence = receipt.pclEvidence, counts = {};
    for (const { outcome } of raw.observations) counts[outcome] = (counts[outcome] ?? 0) + 1;
    const bound = [receipt.fixtureRemoved === true, receipt.runId === context.runId, measured.runId === context.runId,
        ...[['contextSha256', 'context.json'], ['rawSha256', 'raw.json'], ['ownershipProofSha256', 'ownership-proof.json']].map(([field, file]) => receipt[field] === digest(file)),
        evidence?.runId === context.runId, evidence?.file === 'pcl-observations.json', evidence?.sha256 === digest('pcl-observations.json'),
        stock.runId === raw.runId, stock.rawSha256 === receipt.rawSha256, isDeepStrictEqual(stock.counts, counts), stock.observations === raw.observations.length,
        stock.suiteComplete === raw.suiteComplete, stock.measuredAt === raw.measuredAt];
    if (!bound.every(Boolean)) throw new Error('Inconsistent pointer-color publication run or receipt binding.');
}

/** A case as published: the contract, the observed groups and both pinned results beside the unchanged stock grade; a divergence only when the measurement recorded one. */
const publishedCase = (item, raw) => {
    const stock = raw.observations.find(entry => entry.featureId === item.caseId);
    if (stock === undefined) throw new Error(`Missing stock outcome: ${item.caseId}`);
    const { caseId, pinnedCriterion, pinnedScript, effectContract, observed, pinned, contractResult, divergence, conclusion } = item;
    return { caseId, pinnedCriterion, pinnedScript, effectContract, observed, pinned, contractResult, stock, ...(divergence === undefined ? {} : { divergence }), conclusion };
};

export function pclPublication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision } = {}) {
    const [context, stock, measured, raw, receipt] = ['context.json', 'summary.json', 'pcl-observations.json', 'raw.json', 'cell-receipt.json']
        .map(file => JSON.parse(readFileSync(join(dir, file))));
    admit(dir, { context, stock, measured, raw, receipt });
    const cases = measured.cases.map(item => publishedCase(item, raw));
    if (!isDeepStrictEqual(cases.map(item => item.caseId), [...pclCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const { suiteId, contractsVersion, runId, adapter } = measured;
    const { counts, suiteComplete, rawSha256, measuredAt } = stock;
    return {
        summary: { schemaVersion: 1, measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision, scriptsRevision, context, receipt,
            stockCollector: { counts, suiteComplete, rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
                note: 'Stock terminal-path grades from the same owned run; the pointer-color effect does not change the stock score.' } },
        report: { schemaVersion: 1, suiteId, contractsVersion, runId, adapter, scope, cases },
    };
}
