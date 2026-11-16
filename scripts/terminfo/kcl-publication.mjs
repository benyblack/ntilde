// A publication-only projection of the independently rederived retained owned run for the kitty color-control (OSC 21) qualification.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { kclCaseIds } from './kcl-contracts.mjs';

const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));
export const measuredScriptsRevision = 'ec6c5b99431af6d8fcee5083f196a585e66f2d13';
/** The last commit that changed production (src/): the sidecar App was built from this source and later commits changed only scripts and docs. */
export const measuredAppRevision = 'd82374aa8e22e7b4e1115344908e3c2bb975c7c6';
const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc21-kitty-color (OSC 21;foreground=? ST: one framed OSC 21 whose single foreground field is empty or rgb:h/h/h with one to four hex digits; '
    + 'a query-only probe that can never return unsupported), decided by the unchanged stock collector against the App in this owned run, corroborated by the exact-query witness (query policy v5) and replayed '
    + 'byte-exactly on both pinned paths over the journaled raw reply of its own fenced exchange, beside a separately identified effect contract graded by an independent oracle written from kitty\'s color-control '
    + 'protocol text: exact 8-bit rgb replies for foreground, background, cursor, the selection colors and palette entries 5 and 200 that other sequences (OSC 4, 10, 11, 12, 17, 19) set, each key alone and together in '
    + 'request order, ST-terminated replies to BEL requests, set-then-query and the numeric encodings read back through OSC 21 and through the legacy OSC replies, a bare key restoring the baseline reply, the legacy '
    + 'resets restoring the baseline, unknown keys as Base64 unknown fields, every exchange leaving the complete screen, cursor, margins, modes and active screen unchanged; color names, alpha, the empty (dynamic) '
    + 'value, cursor_text, visual_bell, transparent_background_color keys, key case, the oversize cap and the field cap are policy groups that never decide, and the #RGB and out-of-range rgbi spellings differ from '
    + 'kitty\'s text and are recorded as policy divergences; raw replies and stored state, not rendering or painted pixels; no setting of dynamic or named colors, no alpha or opacity, no remote/mux, Windows/Linux or '
    + 'common-harness claim.';
const stockOf = (raw, caseId) => {
    const observation = raw.observations.find(entry => entry.featureId === caseId);
    if (!observation) throw new Error(`Missing stock outcome: ${caseId}`);
    return observation;
};

/** The exact-query witness of the pinned request, from the retained query report. */
function queryWitness(dir) {
    const witness = json(dir, 'query-observations.json').observations.find(item => item.id === 'q07.extensions.osc21-kitty-color');
    if (!witness) throw new Error('Missing the OSC 21 exact-query witness.');
    const { id, expectedEvidence, expectedPattern, receivedHex, contractMatched } = witness;
    return { id, expectedEvidence, expectedPattern, receivedHex, contractMatched };
}

export function kclPublication(dir) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const measured = json(dir, 'kcl-observations.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json'), witness = queryWitness(dir);
    const cases = measured.cases.map(item => ({ caseId: item.caseId, pinnedCriterion: item.pinnedCriterion, pinnedScript: item.pinnedScript, effectContract: item.effectContract,
        observed: item.observed, pinned: item.pinned, contractResult: item.contractResult, stock: stockOf(raw, item.caseId), queryWitness: witness,
        ...(item.divergence ? { divergence: item.divergence } : {}), conclusion: item.conclusion }));
    if (JSON.stringify(cases.map(item => item.caseId)) !== JSON.stringify([...kclCaseIds])) throw new Error('The published cases differ from the contracted cases.');
    const report = { schemaVersion: 1, suiteId: measured.suiteId, contractsVersion: measured.contractsVersion, runId: measured.runId, adapter: measured.adapter, scope, cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: measuredAppRevision,
        scriptsRevision: measuredScriptsRevision, context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash,
            sourceRevision: raw.sourceRevision, note: 'Stock terminal-path grades from the same owned run; the unchanged pinned probe now passes against the App, moving extensions.osc21-kitty-color from inconclusive to supported.' } };
    return { summary, report };
}
