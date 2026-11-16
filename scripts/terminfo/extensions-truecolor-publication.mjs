// Retrospective replay of the separately registered extensions.truecolor callback
// over the exact foreground fixture and journal from an already completed run.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { buildSgrTruecolorGradingReport } from './sgr-truecolor-grading.mjs';

export const sourcePublication = '2026-11-15-kitty-color-control';
export const extensionTruecolorSuiteId = 'ntilde-owned-extensions-truecolor-projection-v1';
export const extensionTruecolorCarrierSuiteId = 'ntilde-owned-sgr-truecolor-graded-v1';
export const extensionTruecolorVersion = 1;
export const extensionTruecolorIds = Object.freeze(['extensions.truecolor']);
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const read = (dir, file) => readFileSync(join(dir, file));
const json = (dir, file) => JSON.parse(read(dir, file));
const check = (ok, message) => { if (!ok) throw new Error(`extensions.truecolor projection: ${message}`); };
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// Hashes pin the relevant spans as well as the complete upstream bundle. The
// receipt-bound adapter must contain the identical callback and helpers.
const spans = [
    ['function parserStateResult(', '\nfunction notTestedResult(', '48ab0585a1b40452904cb9b65445258b99a6aadef60a8e6e189e55c624bb385e'],
    ['function sameRgbCells(', '\nfunction probeForeground(', 'f4e7cfbfa9efbce3dcb2fc60f56f0cfc1fae298ccf4bb176d585459497d8406f'],
    ['      ...probe("extensions.truecolor", (ctx) => {', '\n      }, async (ctx) => {', '17ba743f933dc3a58e69669ddf1d3258ba80805929359d1695c821f9cb49ba3e'],
];
function extract(source, [start, end, digest]) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    check(from >= 0 && to > from && source.indexOf(start, from + start.length) < 0, 'pinned source seam missing or duplicated');
    const text = source.slice(from, to);
    check(sha(text) === digest, 'pinned source bytes differ');
    return text;
}

/** Run the exact pinned termless callback and helper bodies on journaled cells. */
export function replayExtensionTruecolor(source, fixture, cells, archive = readFileSync(new URL('../../docs/qa/terminfo/2026-11-16-extensions-truecolor/terminfo-dev-4.0.0.bundle.mjs.gz', import.meta.url))) {
    const bundle = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    check(sha(bundle) === 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6', 'upstream bundle differs');
    const original = bundle.toString('utf8');
    const measured = spans.map(span => extract(source, span));
    check(spans.every((span, index) => extract(original, span) === measured[index]), 'pinned and measured source differ');
    const [resultHelper, compareHelper, registration] = measured;
    const callback = registration.slice(registration.indexOf('(ctx) => {')) + '\n}';
    let fed = 0;
    const ctx = { feed(bytes) { check(fed++ === 0 && bytes === fixture, 'pinned feed differs from measured fixture'); },
        getCell(row, col) { check(fed === 1 && row === 0 && Number.isInteger(col) && col >= 0 && col < 5, 'unexpected pinned read'); return cells[col]; } };
    const fn = runInNewContext(`${resultHelper}\n${compareHelper}\n(${callback})`, Object.create(null), { timeout: 1000 });
    const result = fn(ctx);
    check(fed === 1, 'pinned callback did not feed fixture');
    return JSON.parse(JSON.stringify(result));
}

export function extensionTruecolorPublication(dir, source) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json');
    const retained = json(dir, 'sgtc-observations.json'), run = json(dir, 'sgtc-run.json');
    const frames = read(dir, 'sgtc-snapshots.jsonl').toString('utf8').trim().split('\n').map(JSON.parse);
    const binding = receipt.sgrTruecolorEvidence;
    check(source.ownedRunId === context.runId && binding?.runId === context.runId && retained.runId === context.runId, 'run identity mismatch');
    check(binding.suiteId === extensionTruecolorCarrierSuiteId && binding.contractsVersion === 1 && receipt.fixtureRemoved === true, 'receipt binding or cleanup missing');
    check(binding.sha256 === sha(read(dir, 'sgtc-observations.json')) && binding.runSha256 === sha(read(dir, 'sgtc-run.json')), 'receipt evidence differs');
    check(binding.adapterBundleSha256 === retained.adapter.bundleSha256 && sha(read(dir, 'collector-sgtc.mjs')) === binding.adapterBundleSha256, 'truecolor adapter differs');
    check(sha(read(dir, 'collector-sgrgb.mjs')) === context.sgrRgbBundleSha256 && receipt.sgrRgbEvidence.adapterBundleSha256 === context.sgrRgbBundleSha256, 'source adapter differs');
    check(context.upstreamBundleSha256 === 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6', 'upstream pin differs');
    check(same(buildSgrTruecolorGradingReport(frames, run, context.runId, retained.adapter, { contractsVersion: 1 }), retained), 'journal does not rederive');
    const item = retained.cases.find(c => c.caseId === 'sgr.fg.truecolor');
    check(item && item.pinnedFeeds.length === 1 && item.pinned?.response, 'foreground fixture missing');
    const state = JSON.parse(item.pinned.response);
    const cells = ['baseline', 'controlFirst', 'controlSecond', 'targetFirst', 'targetSecond'].map(k => state[k]);
    const pinned = replayExtensionTruecolor(read(dir, 'collector-sgrgb.mjs').toString('utf8'), item.pinnedFeeds[0], cells);
    const pv = pinned.observation.outcome, effect = item.contractResult.outcome;
    const result = { caseId: 'extensions.truecolor', pinnedCriterion: 'Two distinct direct-RGB foreground samples match their requested values',
        pinnedFeeds: item.pinnedFeeds, pinned, contractResult: item.contractResult, observed: item.observed, colorBinding: item.colorBinding,
        ...(pv === effect ? {} : { divergence: { kind: 'pinned-differs-from-effect', pinnedOutcome: pv, contractOutcome: effect } }),
        conclusion: { ...item.conclusion, grading: 'retrospective-pinned-callback-plus-existing-effect-v1' } };
    const report = { schemaVersion: 1, suiteId: extensionTruecolorSuiteId, sourceSuiteId: extensionTruecolorCarrierSuiteId, contractsVersion: 1, runId: context.runId, adapter: retained.adapter,
        scope: 'Retrospective exact pinned extensions.truecolor termless replay over the retained foreground fixture and its journaled stored and App-resolved cells. The existing foreground effect contract and receipt are reused; no fresh measurement, stock grade, rendered appearance, or other platform is claimed.', cases: [result] };
    const summary = { schemaVersion: 1, measuredAt: source.measuredAt, ownedRunId: context.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision,
        source: { publication: `docs/qa/terminfo/${sourcePublication}/`, summarySha256: source.summarySha256, archiveSha256: source.archiveSha256,
            note: 'This is a retrospective replay of the existing sgr.fg.truecolor measurement; the receipt binds the carrying suite, not a new collection.' },
        context, receipt, stockCollector: { ...source.stockCollector, note: 'Stock collector results from the carrying run; the retrospective replay does not alter them.' } };
    return { summary, report };
}
