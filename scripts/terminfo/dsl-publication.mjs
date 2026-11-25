// A publication-only projection of the independently rederived retained owned run for the DECSCLM qualification (modes.decsclm).
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { dslCaseIds } from './dsl-contracts.mjs';

/** The measured scripts and App revisions (the App build is stamped into the sidecar assembly; the retained selected-source manifest independently pins the measured scripts). */
export const measuredScriptsRevision = '7394fe6d707803f022999e834f58e88a17a3d703';
export const measuredAppRevision = '7394fe6d707803f022999e834f58e88a17a3d703';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.decsclm (decrpmModeProbe("modes.decsclm", 4, ...): the termless path feeds CSI ? 4 h, captures the CSI ? 4 $ p reply and feeds CSI ? 4 l and always returns '
    + 'parserStateResult(null, ...), inconclusive by construction; the terminal path asks queryMode(4) and returns decrpmResult, inconclusive while mode 4 is not recognized), decided by the unchanged stock '
    + 'collector against the App in this owned run and replayed byte-exactly on both pinned paths over the journaled replies of their own fenced exchanges, beside a separately identified effect contract graded '
    + 'by independent literals written from DEC\'s DECSCLM contract (smooth scrolling paces scrolling output at about six lines per second): CSI ? 4 h / l alone and a forty-line scroll burst of the margin band '
    + 'with smooth scrolling selected, held, reset and never touched are ignored (policy, #1001: terminal output always jump-scrolls), leave the screen, cursor, rendition, margins, modes and active screen as '
    + 'contracted (identical across the bursts), finish every exchange inside a two-second bound that scrolling at six lines per second could not meet (read on the parent\'s monotonic clock, beside a control '
    + 'that reads a deliberate 2.5-second hold) and leave mode 4 answered not recognized, beside a recognised-mode control and a clear/home control. Raw replies, stored terminal state and parent timing, not a '
    + 'rendered frame, painted pixels, the wheel smooth-scroll setting, remote/mux, Windows/Linux or common-harness behavior; no stock-score claim.';

/** One cleaned-up owned run binds every projected file; the stock collector run has its own identity and must agree with its raw output. */
function admit(dir, { context, stock, measured, raw, receipt }) {
    const digest = file => createHash('sha256').update(readFileSync(join(dir, file))).digest('hex');
    const evidence = receipt.dslEvidence, counts = {};
    for (const { outcome } of raw.observations) counts[outcome] = (counts[outcome] ?? 0) + 1;
    const bound = [receipt.fixtureRemoved === true, receipt.runId === context.runId, measured.runId === context.runId,
        ...[['contextSha256', 'context.json'], ['rawSha256', 'raw.json'], ['ownershipProofSha256', 'ownership-proof.json']].map(([field, file]) => receipt[field] === digest(file)),
        evidence?.runId === context.runId, evidence?.file === 'dsl-observations.json', evidence?.sha256 === digest('dsl-observations.json'),
        stock.runId === raw.runId, stock.rawSha256 === receipt.rawSha256, isDeepStrictEqual(stock.counts, counts), stock.observations === raw.observations.length,
        stock.suiteComplete === raw.suiteComplete, stock.measuredAt === raw.measuredAt];
    if (!bound.every(Boolean)) throw new Error('Inconsistent decsclm publication run or receipt binding.');
}

/** A case as published: the contract, the observed groups and both pinned results beside the unchanged stock grade; a divergence only when the measurement recorded one. */
const publishedCase = (item, raw) => {
    const stock = raw.observations.find(entry => entry.featureId === item.caseId);
    if (stock === undefined) throw new Error(`Missing stock outcome: ${item.caseId}`);
    const { caseId, pinnedCriterion, pinnedScript, effectContract, observed, pinned, contractResult, divergence, conclusion } = item;
    return { caseId, pinnedCriterion, pinnedScript, effectContract, observed, pinned, contractResult, stock, ...(divergence === undefined ? {} : { divergence }), conclusion };
};

export function dslPublication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision } = {}) {
    const [context, stock, measured, raw, receipt] = ['context.json', 'summary.json', 'dsl-observations.json', 'raw.json', 'cell-receipt.json']
        .map(file => JSON.parse(readFileSync(join(dir, file))));
    admit(dir, { context, stock, measured, raw, receipt });
    const cases = measured.cases.map(item => publishedCase(item, raw));
    if (!isDeepStrictEqual(cases.map(item => item.caseId), [...dslCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const { suiteId, contractsVersion, runId, adapter } = measured;
    const { counts, suiteComplete, rawSha256, measuredAt } = stock;
    return {
        summary: { schemaVersion: 1, measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision, scriptsRevision, context, receipt,
            stockCollector: { counts, suiteComplete, rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
                note: 'Stock terminal-path grades from the same owned run; the decsclm effect does not change the stock score.' } },
        report: { schemaVersion: 1, suiteId, contractsVersion, runId, adapter, scope, cases },
    };
}
