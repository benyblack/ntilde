// A publication-only projection of the independently rederived retained owned run for the rxvt OSC 720 qualification (extensions.osc720-scroll-up).
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { osc720CaseIds } from './osc720-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = '06ee7177a8ae6fff17b88bcf437c686f17e1bf91';
/** The measured App build revision (stamped into the sidecar assembly). The retained selected-source manifest independently pins the measured scripts; these human-readable labels are not binary-to-source attestation. */
export const measuredAppRevision = '06ee7177a8ae6fff17b88bcf437c686f17e1bf91';
const scope = 'Pinned terminfo.dev@4.0.0 paths for extensions.osc720-scroll-up preserved: the terminal path (two writes, a cursor-position query and unverifiedEffect; always inconclusive, evidence "consumed", and the path the '
    + 'stock collector grades) and the termless function (thirty fed lines, two scrollback reads and the bare OSC 720 BEL, which carries no `;` and so is not dispatched by urxvt either; replayed UNCHANGED over a parent-built scrollback context mapped from the App view position, which is not a '
    + 'termless backend run and not a stock grade), beside a separately identified effect contract graded by an independent oracle written from rxvt-unicode: OSC 720 ; Pt moves the viewing window up Pt lines and, for a zero or '
    + 'empty Pt, clears the scrollback (rxvt(7) and src/command.C scr_page / scr_erase_savelines); an OSC is dispatched only after a `;`, so the pinned bare form is ignored by urxvt and is expected to change nothing. Observed quantities are the App view scroll position, the stored history (count, generation, '
    + 'evictions and content), the cursor and the screen; the wheel (a routed generated-input notch) and ED 3 are observer controls only and never evidence for OSC 720. No OSC 721, painted pixels, remote/mux, Windows/Linux, '
    + 'stock-score or common-harness claim.';
function stockObservation(raw, caseId) {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (observation === undefined) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
}

/** A projection admits only files bound to one cleaned-up owned run. Stock collection has its own run identity. */
function validatePublication(dir, context, stock, measured, raw, receipt) {
    const digest = file => createHash('sha256').update(readFileSync(join(dir, file))).digest('hex');
    const check = value => { if (!value) throw new Error('Inconsistent OSC 720 publication run or receipt binding.'); };
    check(receipt.fixtureRemoved === true && receipt.runId === context.runId && measured.runId === context.runId);
    check(receipt.contextSha256 === digest('context.json') && receipt.rawSha256 === digest('raw.json') && receipt.ownershipProofSha256 === digest('ownership-proof.json'));
    check(receipt.osc720Evidence?.runId === context.runId && receipt.osc720Evidence.file === 'ovu-observations.json' && receipt.osc720Evidence.sha256 === digest('ovu-observations.json')
        && receipt.osc720Evidence.actionsSha256 === digest('ovu-actions.json'));
    const counts = raw.observations.reduce((out, item) => ({ ...out, [item.outcome]: (out[item.outcome] ?? 0) + 1 }), {});
    check(stock.runId === raw.runId && stock.rawSha256 === receipt.rawSha256 && isDeepStrictEqual(stock.counts, counts)
        && stock.observations === raw.observations.length && stock.suiteComplete === raw.suiteComplete && stock.measuredAt === raw.measuredAt);
}

function publishedCase(item, raw) {
    const effect = item.effectContract.outcome, terminal = item.pinned.terminal;
    return { caseId: item.caseId, effectContract: item.effectContract, experiments: item.experiments,
        pinned: { terminal, observation: terminal.observation, note: terminal.note, termlessReplay: item.pinned.termless, termlessInputs: item.pinned.termlessInputs,
            verdictSource: 'terminal path (the path real terminal applications are graded by; the termless replay is recorded separately)', qualifiedOutcome: item.pinned.qualifiedOutcome },
        contractResult: { outcome: effect }, stock: stockObservation(raw, item.caseId),
        ...(item.pinned.qualifiedOutcome !== effect ? { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome: item.pinned.qualifiedOutcome, contractOutcome: effect } } : {}),
        conclusion: { outcome: item.pinned.qualifiedOutcome, evidence: 'view-position, stored history, cursor and screen (pane-metadata-and-session-status)', grading: 'pinned-terminal-preserved-over-owned-observations',
            note: terminal.note } };
}

export function osc720Publication(dir, { appRevision = measuredAppRevision, scriptsRevision = measuredScriptsRevision } = {}) {
    const context = json(dir, 'context.json'), stock = json(dir, 'summary.json');
    const measured = json(dir, 'ovu-observations.json'), raw = json(dir, 'raw.json'), receipt = json(dir, 'cell-receipt.json'), actions = json(dir, 'ovu-actions.json');
    validatePublication(dir, context, stock, measured, raw, receipt);
    const cases = measured.cases.map(item => publishedCase(item, raw));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...osc720CaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, wheelActions: actions, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision, scriptsRevision, context, receipt,
        stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision,
            note: 'Stock terminal-path grades from the same owned run; the OSC 720 effect does not change the stock score.' } };
    return { summary, report };
}
