// A publication-only projection of the independently rederived retained owned run for the DECCOLM qualification (modes.deccolm).
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { dclCaseIds } from './dcl-contracts.mjs';
import { kernelGrid } from './dcl-capture.mjs';
import { checkResizeEvidence } from './resize-receipt.mjs';

/** The measured scripts and App revisions (the App build is stamped into the sidecar assembly; the retained selected-source manifest independently pins the measured scripts). */
export const measuredScriptsRevision = '3ec330061f2722b1adf1c9373eda82cddcff7461';
export const measuredAppRevision = 'cf5d07ed39ae739fbf7592c519e850b711353484';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.deccolm (decrpmModeProbe("modes.deccolm", 3, ...): the termless path feeds CSI ? 3 h, captures the CSI ? 3 $ p reply and feeds CSI ? 3 l and returns '
    + 'parserStateResult(null, ...), inconclusive by construction; the terminal path asks queryMode(3) and returns decrpmResult, inconclusive while mode 3 is not recognized), decided by the unchanged stock '
    + 'collector against the App in this owned run and replayed byte-exactly on both pinned paths over the journaled replies and App grid width of their own fenced exchanges, beside a separately identified effect '
    + 'contract graded by independent literals written from xterm\'s DECCOLM and allow-switching (mode 40) contract: CSI ? 3 h / l alone, with switching allowed and not allowed, and a set/reset cycle, are ignored '
    + '(policy, #1000: the App has no program-initiated resize path), leave the complete screen, cursor, rendition, margins, modes and active screen unchanged, keep the App grid and the independently read kernel '
    + 'PTY grid at their baseline size (a width other than 80 and 132, so either direction would be visible) and leave mode 3 and mode 40 answered not recognized, beside a recognised-mode control and a clear/home '
    + 'control. Raw replies, stored terminal state and PTY geometry, not a window size, rendering, painted pixels, remote/mux, Windows/Linux or common-harness behavior; no stock-score claim.';


/** One cleaned-up owned run binds every projected file; the stock collector run has its own identity and must agree with its raw output. */
function admit(dir, { context, stock, measured, raw, receipt }) {
    const digest = file => createHash('sha256').update(readFileSync(join(dir, file))).digest('hex');
    const evidence = receipt.dclEvidence, counts = {};
    for (const { outcome } of raw.observations) counts[outcome] = (counts[outcome] ?? 0) + 1;
    const bound = [receipt.fixtureRemoved === true, receipt.runId === context.runId, measured.runId === context.runId,
        ...[['contextSha256', 'context.json'], ['rawSha256', 'raw.json'], ['ownershipProofSha256', 'ownership-proof.json']].map(([field, file]) => receipt[field] === digest(file)),
        evidence?.runId === context.runId, evidence?.file === 'dcl-observations.json', evidence?.sha256 === digest('dcl-observations.json'),
        stock.runId === raw.runId, stock.rawSha256 === receipt.rawSha256, isDeepStrictEqual(stock.counts, counts), stock.observations === raw.observations.length,
        stock.suiteComplete === raw.suiteComplete, stock.measuredAt === raw.measuredAt];
    if (!bound.every(Boolean)) throw new Error('Inconsistent deccolm publication run or receipt binding.');
}

/** A case as published: the contract, the observed groups and both pinned results beside the unchanged stock grade; a divergence only when the measurement recorded one. */
const publishedCase = (item, raw) => {
    const stock = raw.observations.find(entry => entry.featureId === item.caseId);
    if (stock === undefined) throw new Error(`Missing stock outcome: ${item.caseId}`);
    const { caseId, pinnedCriterion, pinnedScript, effectContract, observed, pinned, contractResult, divergence, conclusion } = item;
    return { caseId, pinnedCriterion, pinnedScript, effectContract, observed, pinned, contractResult, stock, ...(divergence === undefined ? {} : { divergence }), conclusion };
};

/** The live geometry control, taken from the same retained owned run: its owned resize suite made parent-selected App resizes and read both the App's settled grid and the kernel PTY grid (the same system stty
 *  and the same proved PTY the DECCOLM suite reads) after each one. The whole resize evidence is first re-derived by the production resize check (the receipt-bound report digest, the source plan, the paired frames
 *  and the ownership-proof action stream), so the actions below are the validated ones. Then every action must show the App and the kernel at its planned target, belong to this run and this pane, a changed width
 *  must differ from the DECCOLM baseline and every restore must return to it, so an unchanged grid in the DECCOLM groups is shown to be observable by exactly these readers in exactly this run. */
function geometryControl(dir, { context, receipt, baseline, paneId }) {
    const binding = receipt.resizeEvidence;
    if (binding?.runId !== context.runId || binding.suiteId !== 'ntilde-owned-resize-reflow-v1' || !isDeepStrictEqual(binding.counts, { passed: 3 })) throw new Error('The retained run carries no complete resize control.');
    const proof = JSON.parse(readFileSync(join(dir, 'ownership-proof.json')));
    checkResizeEvidence(dir, binding, context.runId, context, proof);
    const marker = proof.proofs[0].resize;
    if (marker.paneId !== paneId || !isDeepStrictEqual(marker.originalGeometry, baseline)) throw new Error('The resize control did not run on the DECCOLM pane at its baseline grid.');
    const lines = readFileSync(join(dir, 'resize-actions.jsonl'), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
    if (lines.length !== 6) throw new Error('The retained resize control does not hold its six parent-selected actions.');
    const actions = lines.map(({ index, caseId, stageId, target, observed, error }, position) => {
        if (index !== position || error !== undefined || observed.paneId !== paneId || observed.runId !== context.runId) throw new Error(`The ${caseId} ${stageId} resize control action is not this run's, in order and error-free.`);
        const kernel = kernelGrid(observed.terminalOutput), app = observed.app;
        const grids = [[app.rows, app.cols], [kernel.rows, kernel.cols], [target.rows, target.cols]];
        if (app.exact !== true || app.layoutSettled !== true || grids.some(grid => !isDeepStrictEqual(grid, grids[0]))) throw new Error(`The ${caseId} ${stageId} resize control readers disagree with the target.`);
        return { caseId, stageId, target: { rows: target.rows, cols: target.cols }, app: { rows: app.rows, cols: app.cols }, kernel: { rows: kernel.rows, cols: kernel.cols } };
    });
    const moved = actions.filter(action => action.stageId === 'after'), restored = actions.filter(action => action.stageId === 'restored');
    const widthMoved = moved.some(action => action.app.cols !== baseline.cols && action.kernel.cols !== baseline.cols);
    const back = restored.every(action => action.app.rows === baseline.rows && action.app.cols === baseline.cols && action.kernel.rows === baseline.rows && action.kernel.cols === baseline.cols);
    if (moved.length !== 3 || restored.length !== 3 || !widthMoved || !back) throw new Error('The retained resize control does not move and restore the DECCOLM baseline grid.');
    return { suiteId: binding.suiteId, runId: binding.runId, passed: binding.counts.passed, paneId, baseline, actions,
        note: 'Parent-selected App resizes of the same owned run, re-derived by the production resize check, each followed by the App grid and the kernel PTY grid through the same readers the DECCOLM groups use: both moved together and both returned.' };
}

export function dclPublication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision } = {}) {
    const [context, stock, measured, raw, receipt] = ['context.json', 'summary.json', 'dcl-observations.json', 'raw.json', 'cell-receipt.json']
        .map(file => JSON.parse(readFileSync(join(dir, file))));
    admit(dir, { context, stock, measured, raw, receipt });
    const cases = measured.cases.map(item => publishedCase(item, raw));
    if (!isDeepStrictEqual(cases.map(item => item.caseId), [...dclCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const { geometry } = cases[0].observed;
    const geometryControlRecord = geometryControl(dir, { context, receipt, baseline: { rows: geometry.rows, cols: geometry.cols }, paneId: measured.snapshots[0].paneId });
    const { suiteId, contractsVersion, runId, adapter } = measured;
    const { counts, suiteComplete, rawSha256, measuredAt } = stock;
    return {
        summary: { schemaVersion: 1, measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision, scriptsRevision, context, receipt, geometryControl: geometryControlRecord,
            stockCollector: { counts, suiteComplete, rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
                note: 'Stock terminal-path grades from the same owned run; the deccolm effect does not change the stock score.' } },
        report: { schemaVersion: 1, suiteId, contractsVersion, runId, adapter, scope, cases },
    };
}
