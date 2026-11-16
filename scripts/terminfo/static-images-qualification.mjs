// Qualification of the two exact terminfo.dev@4.0.0 static-image cases over the already-receipted
// owned static-image suite (ntilde-owned-static-images-v1). Publication-only: the measured source
// graph, source policy and receipt schema are unchanged. The carrier report is the authority for every
// stage; this projection adds the exact pinned-probe replay, two separately identified effect contracts
// and explicit validity controls. A pinned verdict, an effect verdict and a stock grade never merge.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';

export const staticImagesQualificationSuiteId = 'ntilde-owned-static-images-qualification-v1';
export const staticImagesQualificationVersion = 1;
export const staticImagesQualificationIds = Object.freeze(['extensions.sixel', 'extensions.iterm2-images']);
export const staticImagesCarrierSuiteId = 'ntilde-owned-static-images-v1';
export const staticImagesCarrierPolicy = 'owned-static-images-paired-raw-render-v2';
export const upstreamBundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
// The measured App and scripts are the same unmodified main revision: no measured source changed, only publication modules were added.
export const measuredAppRevision = '49bc7b7627a1f2c7385e2c400d5d0baedaeb96d4';
export const measuredScriptsRevision = '49bc7b7627a1f2c7385e2c400d5d0baedaeb96d4';
export const pinnedBundleArchive = 'terminfo-dev-4.0.0.bundle.mjs.gz';

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error(`static images qualification: ${message}`); };
const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));

/** Independently specified effect contracts. They name the carrier cases they compose, never the pinned probes. */
export const effectContracts = Object.freeze({
    'extensions.sixel': Object.freeze({
        id: 'ntilde-sixel-native-raster-effect-v1', version: 1, carrierCases: Object.freeze(['sixel.native-raster']),
        statement: 'A fixed DEC SIXEL packet (7-bit DCS, explicit 1:1 aspect, declared 16x16 raster, RGB-percent palette, unequal four-colour quadrants, P2=1) is absent before the write, '
            + 'paints the independently source-derived native raster at the controlled anchor with unchanged surroundings, and is absent again after an erase.',
        primary: 'https://vt100.net/docs/vt3xx-gp/chapter14.html',
    }),
    'extensions.iterm2-images': Object.freeze({
        id: 'ntilde-iterm2-inline-image-effect-v1', version: 1, carrierCases: Object.freeze(['iterm.explicit-stretch', 'iterm.default-aspect']),
        statement: 'A single-shot OSC 1337 File=inline=1 PNG with a 4x2 cell request is absent before the write, paints the independently source-derived pixels (full-envelope stretch when '
            + 'preserveAspectRatio=0, contained undistorted fit by default) with unchanged surroundings and a transparent hole revealing the known background, and is absent after an erase.',
        primary: 'https://github.com/gnachman/iterm2-website/blob/d6249e4f778822ac2ec89cc099607a3bc5ef4e3f/source/_includes/3.7/documentation-images.md#L64',
    }),
});

// The exact probe registrations are pinned by hash inside the pinned upstream bundle.
const helperSpan = ['function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(', 'e337ddbb1f81a40c2c6f997912e6f53aa9a86fa4d0cfde2705dbdb0b6b9899bb'];
const registrations = Object.freeze({
    'extensions.sixel': ['    probe("extensions.sixel", (ctx) => {', '\n    {\n      ...probe("extensions.osc8"', 'e9e53d23730ef28bba98b9dbd1ff348b2aa2860d5cfb405a1b554e179bb080e5'],
    'extensions.iterm2-images': ['    probe("extensions.iterm2-images", (ctx) => {', '\n    oscSimpleQueryProbe("extensions.osc1337-cellsize"', '20c0502f9c40648d3bae8eaf044f731a772bde9a4b9e49ce533c88c600992568'],
});
export const pinnedSpanDigests = Object.freeze({ helper: helperSpan[2], 'extensions.sixel': registrations['extensions.sixel'][2], 'extensions.iterm2-images': registrations['extensions.iterm2-images'][2] });

function extract(source, [start, end, digest]) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    check(from >= 0 && to > from && !source.slice(from + start.length).includes(start), 'pinned source seam missing or duplicated');
    const text = source.slice(from, to);
    check(sha(text) === digest, `pinned source bytes differ (${sha(text)})`);
    return text;
}

// The executed text is upstream bytes pinned by SHA-256 above, evaluated in an empty context with a hard timeout; it reads no input.
function loadPinnedRegistration(helper, registration, id) {
    const definition = runInNewContext(`${helper}\n(${registration})`, Object.create(null), { timeout: 1000 }); // NOSONAR - pinned upstream bytes, isolated context
    check(definition.id === id && typeof definition.termless === 'function' && typeof definition.term === 'function', 'pinned registration shape differs');
    return definition;
}

async function replayOne(source, helper, id) {
    const definition = loadPinnedRegistration(helper, extract(source, registrations[id]).trim().replace(/,$/, ''), id);
    const capabilitiesFor = value => (id === 'extensions.sixel' ? { sixel: value } : { extensions: new Set(value ? ['iterm2Images'] : []) });
    const [declaredTrue, declaredFalse] = [true, false].map(value => structuredClone(definition.termless({ capabilities: capabilitiesFor(value) })));
    check(same(declaredTrue.observation, declaredFalse.observation), 'pinned termless verdict depends on the declaration');
    const terminal = structuredClone(await definition.term({}));
    return { termless: { ...declaredTrue, declaredFalseResponse: declaredFalse.response }, terminal };
}

/** Run both exact pinned callbacks. Neither reads a pixel: their observations are constants, whatever the declaration. */
export async function replayPinnedImageProbes(archive) {
    const bundle = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    check(sha(bundle) === upstreamBundleSha256, 'upstream bundle differs');
    const source = bundle.toString('utf8'), helper = extract(source, helperSpan);
    const replayed = await Promise.all(staticImagesQualificationIds.map(id => replayOne(source, helper, id)));
    return Object.fromEntries(staticImagesQualificationIds.map((id, index) => [id, replayed[index]]));
}

const passedAll = results => results.every(value => value === 'passed');
/** Grade a stage list exactly as an effect: failed wins, otherwise every stage must pass. */
export function caseOutcome(results) {
    if (passedAll(results)) return 'passed';
    return results.includes('failed') ? 'failed' : 'inconclusive';
}
const effectNames = Object.freeze({ passed: 'supported', failed: 'unsupported', inconclusive: 'inconclusive' });
const effectOf = outcome => effectNames[outcome];

const stageSummary = item => ({
    stage: item.featureId, result: item.result, inconclusiveReason: item.inconclusiveReason,
    acknowledgement: item.reply, // protocol acknowledgement only; never a pixel assertion
    assertions: item.assertions.map(({ description, passed }) => ({ description, passed })),
    causality: item.causality,
    measurement: {
        envelope: item.measurement.envelope, contentBounds: item.measurement.contentBounds, geometry: item.measurement.geometry,
        ...(item.measurement.sourceCanvas ? { sourceCanvas: item.measurement.sourceCanvas, reservationCells: item.measurement.reservationCells,
            nativeProjection: item.measurement.nativeProjection, nativeRaster: item.measurement.nativeRaster, compatibility: item.measurement.compatibility,
            nonTargetEnvelope: item.measurement.nonTargetEnvelope } : {}),
    },
});

/** Validity controls over the whole carrier run: every one must hold before any image verdict is reported. */
export function validityControls(report, receipt, context, observationBytes) {
    const binding = receipt.staticImageEvidence, captures = report.snapshots.filter(frame => frame.capture), nonces = captures.flatMap(frame => frame.capture.nonces);
    // The earlier input phase retains each challenge as the comma-joined pair, so compare the same representation.
    const pairs = captures.map(frame => frame.capture.nonces.join(',')), prior = new Set(report.priorChallenges);
    const stages = report.observations;
    const blanks = stages.filter(item => item.featureId.endsWith('.blank'));
    const controls = [
        ['receipt-binds-image-report', binding?.runId === context.runId && binding.suiteId === staticImagesCarrierSuiteId && binding.sha256 === sha(observationBytes) && receipt.fixtureRemoved === true,
            'the complete receipt binds this exact report and records fixture removal'],
        ['carrier-policy', report.suiteId === staticImagesCarrierSuiteId && report.policy === staticImagesCarrierPolicy && context.ownedStaticImagePolicy === staticImagesCarrierPolicy && report.runId === context.runId,
            'the retained report is the paired raw render policy v2 report of this run'],
        ['raw-reader-healthy', report.seal.failed === false && report.seal.disposed === true && report.seal.attempts === report.actions.length,
            'the exclusive raw reader sealed without failure over every admitted write'],
        ['state-restored', report.initialSnapshot === 0 && report.restoredSnapshot === report.actions.length && report.snapshots.length === report.actions.length + 1,
            'the final paired state is bound to the initial state after every write'],
        ['controlled-background', report.backgroundControls.length === report.actions.length && report.backgroundControls.at(-1).snapshot === report.restoredSnapshot
            && report.backgroundControls.slice(0, -1).every(item => item.argb === 0xff101010 && same(item.query.channels16, [4112, 4112, 4112])),
            'every write before the final restoration independently queried the matching controlled OSC 11 and cell background; the final control is the restored original theme'],
        ['fresh-paired-fences', captures.length === report.actions.length && nonces.length === new Set(nonces).size && pairs.length === new Set(pairs).size && pairs.every(pair => !prior.has(pair)) && captures.every(frame => frame.capture.nonces.length === 2),
            'every capture carries its own unpredictable paired challenges, none reused from the earlier input phase or another capture (a stale capture cannot qualify)'],
        ['blank-before-controls', blanks.length === 5 && blanks.every(item => item.result === 'passed' && item.assertions.length === 2 && item.assertions.every(value => value.passed)),
            'every case began from an independently blank, unchanged-surroundings target'],
        ['acknowledgement-separate-from-pixels', stages.every(item => item.reply?.passed === true) && stages.every(item => item.assertions.length > 0),
            'every write was acknowledged by its raw fence while pixel assertions were graded separately'],
    ];
    return controls.map(([name, held, detail]) => ({ name, held, detail }));
}

/** Derive both effect verdicts from the carrier stages; the carrier's own per-case result must agree. */
export function gradeEffects(report) {
    const out = {};
    for (const id of staticImagesQualificationIds) {
        const contract = effectContracts[id], cases = contract.carrierCases.map(caseId => {
            const stages = report.observations.filter(item => item.featureId.startsWith(`${caseId}.`));
            const carrier = report.caseResults.find(item => item.caseId === caseId);
            check(carrier && same(carrier.stages, stages.map(item => item.featureId)), `${caseId}: stage list differs`);
            const derived = caseOutcome(stages.map(item => item.result));
            check(carrier.result === derived, `${caseId}: carrier result ${carrier.result} differs from its stages (${derived})`);
            return { caseId, result: derived, stages: stages.map(stageSummary) };
        });
        out[id] = { contract, cases, outcome: effectOf(caseOutcome(cases.map(item => item.result))) };
    }
    return out;
}

/** The SIXEL native-size finding is a separate, retained diagnostic: it never weakens the oracle. */
function sixelFinding(item) {
    const show = item.cases[0].stages.find(stage => stage.stage === 'sixel.native-raster.show'), m = show.measurement;
    if (item.outcome === 'supported') return null;
    const assertion = description => show.assertions.find(value => value.description === description)?.passed;
    return {
        issue: 'https://github.com/benyblack/ntilde/issues/851',
        independentControlsHeld: { sourceCanvasAgreesWithBitmap: assertion('independent source canvas matches reservation and actual bitmap dimensions'),
            float32ReservationAndAnchor: assertion('independent float32 ceiling reservation, anchor and unclipped native intent'), nonTargetUnchanged: assertion('all non-target capture pixels remain unchanged'),
            acknowledged: show.acknowledgement.passed },
        nativeRasterPassed: assertion('calibrated native raster fidelity'),
        nativeProjection: m.nativeProjection, observedContentBounds: m.contentBounds, reservationCells: m.reservationCells,
        nativeMismatches: m.nativeRaster?.mismatches, wholeCellCompatibilityMismatches: m.compatibility?.mismatches,
        note: 'Cell reservation is distinct from native raster sizing. The whole-cell compatibility projection is diagnostic only and cannot earn a native pass.',
    };
}

const criteria = Object.freeze({
    'extensions.sixel': Object.freeze({
        termless: 'Declared Sixel capability does not establish rendered pixels (always inconclusive, evidence legacy)',
        terminal: 'No pixel readback for Sixel rendering (always inconclusive, evidence none)',
    }),
    'extensions.iterm2-images': Object.freeze({
        termless: 'Declared iTerm2 image capability does not establish rendered image pixels (always inconclusive, evidence legacy)',
        terminal: 'No pixel readback for iTerm2 inline image rendering (always inconclusive, evidence none)',
    }),
});

/** Project one extracted, receipt-verified owned run. `archive` is the retained pinned-bundle copy. */
export async function staticImagesQualification(dir, source, archive) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const observationBytes = readFileSync(join(dir, 'image-observations.json')), report = JSON.parse(observationBytes);
    const validity = validityControls(report, receipt, context, observationBytes);
    check(validity.every(item => item.held), `validity control failed: ${validity.find(item => !item.held)?.name}`);
    check(context.upstreamBundleSha256 === upstreamBundleSha256 && raw.probeHash === '16a690f34c0e', 'upstream pin differs');
    const pinned = await replayPinnedImageProbes(archive), graded = gradeEffects(report);
    const cases = staticImagesQualificationIds.map(id => {
        const stockCase = raw.observations.find(item => item.featureId === id);
        check(stockCase, `missing stock outcome: ${id}`);
        const termless = pinned[id].termless, terminal = pinned[id].terminal, effect = graded[id];
        check(stockCase.outcome === terminal.observation.outcome && stockCase.note === terminal.observation.note && stockCase.evidence === terminal.observation.evidence, `${id}: stock result differs from the pinned terminal callback`);
        const pinnedOutcome = termless.observation.outcome;
        return { caseId: id, pinnedCriterion: criteria[id],
            pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
            effectContract: { id: effect.contract.id, version: effect.contract.version, statement: effect.contract.statement, primary: effect.contract.primary, carrierCases: effect.contract.carrierCases, validity },
            contractResult: { outcome: effect.outcome, carrierCases: effect.cases.map(item => ({ caseId: item.caseId, result: item.result })) },
            observations: effect.cases, ...(id === 'extensions.sixel' && effect.outcome !== 'supported' ? { finding: sixelFinding(effect) } : {}),
            stock: stockCase,
            ...(pinnedOutcome === effect.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: effect.outcome } }),
            conclusion: { outcome: effect.outcome, grading: 'pinned-callbacks-preserved-plus-owned-pixel-effect-v1', evidence: 'owned-controlled-pixels-paired-raw-render', note: termless.note } };
    });
    check(same(cases.map(item => item.caseId), [...staticImagesQualificationIds]), 'unexpected cases');
    const suite = { schemaVersion: 1, suiteId: staticImagesQualificationSuiteId, carrierSuiteId: staticImagesCarrierSuiteId, contractsVersion: staticImagesQualificationVersion, runId: context.runId,
        adapter: { id: context.adapter, upstreamBundleSha256: context.upstreamBundleSha256, pinnedSpans: pinnedSpanDigests },
        scope: 'Two exact pinned terminfo.dev@4.0.0 cases, extensions.sixel and extensions.iterm2-images. Both pinned callbacks (termless and terminal) are replayed from the retained pinned bundle and are always '
            + 'inconclusive. Two separately identified effect contracts compose the existing owned static-image carrier: blank-before, source-derived raster pixels at the controlled anchor with unchanged '
            + 'surroundings, and clear-after, over paired original offscreen PNGs, complete cell/mode reads and fresh raw fences. Protocol acknowledgement, reservation metadata and painted pixels are separate facts. '
            + 'A single-shot PNG at one 4x2 cell request and one fixed SIXEL packet only: no animation, Unicode placeholders, multipart or other image formats, font/DPI change, different-background compositing, '
            + 'remote/mux, other platforms, common-harness or stock-score claim. Kitty cases of the same carrier are not part of this qualification.',
        cases };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision,
        context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision },
        carrier: { suiteId: staticImagesCarrierSuiteId, policy: staticImagesCarrierPolicy, file: 'image-observations.json', sha256: sha(observationBytes), counts: receipt.staticImageEvidence.counts,
            caseResults: report.caseResults.map(item => ({ caseId: item.caseId, result: item.result })) } };
    return { summary, report: suite };
}
