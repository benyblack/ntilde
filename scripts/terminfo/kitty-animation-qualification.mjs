// Qualification of the exact terminfo.dev@4.0.0 case extensions.kitty-graphics.animation over the owned
// Kitty animation suite (ntilde-owned-kitty-animation-v2) of the same run. The carrier report is the authority for
// every stage; this projection adds the exact pinned-probe replay, one separately identified effect contract and
// explicit validity controls. A pinned verdict, an effect verdict and a stock grade never merge.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { animationCases } from './kitty-animation-cases.mjs';

export const kittyAnimationQualificationSuiteId = 'ntilde-owned-kitty-animation-qualification-v2';
export const kittyAnimationQualificationVersion = 2;
export const kittyAnimationQualificationIds = Object.freeze(['extensions.kitty-graphics.animation']);
export const kittyAnimationCarrierSuiteId = 'ntilde-owned-kitty-animation-v2';
export const kittyAnimationCarrierPolicy = 'owned-kitty-animation-paired-raw-render-v2';
export const upstreamBundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
// Measured on the committed tree at cf59628f: the Release sidecar was built from it and the measured scripts are its tree (later commits are publication, docs, test pins and merges of main only).
export const measuredAppRevision = 'cf59628f0ef73dd0a99dba0952acb4b6552678a4';
export const measuredScriptsRevision = 'cf59628f0ef73dd0a99dba0952acb4b6552678a4';
export const pinnedBundleArchive = 'terminfo-dev-4.0.0.bundle.mjs.gz';

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { if (!ok) throw new Error(`kitty animation qualification: ${message}`); };
const json = (dir, file) => JSON.parse(readFileSync(join(dir, file)));

/** Independently specified effect contract. It names the carrier cases it composes, never the pinned probe. */
export const effectContract = Object.freeze({
    id: 'ntilde-kitty-animation-playback-effect-v2', version: 2,
    carrierCases: Object.freeze(['kitty.animation.stopped-hold', 'kitty.animation.playback', 'kitty.animation.stop-freeze', 'kitty.animation.png-and-root-frames']),
    statement: 'A retained 16x16 raw RGBA image with two further frames added by a=f (z=5000 ms each, root gap 5000 ms) shows its root frame, stays on it past that gap while stopped, '
        + 'then after a=a,s=3 shows the second frame, the third frame and the root frame again at the times its gaps give (measured by paired offscreen captures against the App\'s wall clock), '
        + 'and after a=a,s=1 stays frozen on the frame it was showing past a hold that would have advanced it. A half-transparent PNG frame (a=f,f=100) built over the root frame (c=1) shows the protocol\'s straight-alpha blend '
        + 'of the two when chosen with a=a,c=; after the root is edited (a=f,r=1) and shown again, a second PNG frame built over it shows the blend of the edited pixels instead of the original. '
        + 'Every write is acknowledged OK and every cleared target is blank again.',
    primary: 'https://sw.kovidgoyal.net/kitty/graphics-protocol/#animation',
});

// The exact probe registration is pinned by hash inside the pinned upstream bundle.
const helperSpan = ['function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(', 'e337ddbb1f81a40c2c6f997912e6f53aa9a86fa4d0cfde2705dbdb0b6b9899bb'];
const registration = ['    probe("extensions.kitty-graphics.animation", (ctx) => {', '\n    probe("extensions.kitty-graphics.unicode-placeholders"', '4932e05f5f45fe474ebf7df9adf6eb248c04ebe5145b0b5c19fb51a5c72ea25f'];
export const pinnedSpanDigests = Object.freeze({ helper: helperSpan[2], 'extensions.kitty-graphics.animation': registration[2] });

function extract(source, [start, end, digest]) {
    const from = source.indexOf(start), to = source.indexOf(end, from + start.length);
    check(from >= 0 && to > from && !source.slice(from + start.length).includes(start), 'pinned source seam missing or duplicated');
    const text = source.slice(from, to);
    check(sha(text) === digest, `pinned source bytes differ (${sha(text)})`);
    return text;
}

// The executed text is upstream bytes pinned by SHA-256 above, evaluated in an empty context with a hard timeout; it reads no input.
function loadPinnedRegistration(helper, text, id) {
    const definition = runInNewContext(`${helper}\n(${text})`, Object.create(null), { timeout: 1000 }); // NOSONAR - pinned upstream bytes, isolated context
    check(definition.id === id && typeof definition.termless === 'function' && typeof definition.term === 'function', 'pinned registration shape differs');
    return definition;
}

/** Run both exact pinned callbacks. Neither reads a pixel or a frame time: their observations are constants whatever the declaration. */
export async function replayPinnedAnimationProbe(archive) {
    const bundle = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    check(sha(bundle) === upstreamBundleSha256, 'upstream bundle differs');
    const source = bundle.toString('utf8'), helper = extract(source, helperSpan), id = kittyAnimationQualificationIds[0];
    const definition = loadPinnedRegistration(helper, extract(source, registration).trim().replace(/,$/, ''), id);
    const [declaredTrue, declaredFalse] = [true, false].map(value => structuredClone(definition.termless({ capabilities: { kittyGraphics: value } })));
    check(same(declaredTrue.observation, declaredFalse.observation), 'pinned termless verdict depends on the declaration');
    const terminal = structuredClone(await definition.term({}));
    return { [id]: { termless: { ...declaredTrue, declaredFalseResponse: declaredFalse.response }, terminal } };
}

const passedAll = results => results.every(value => value === 'passed');
/** Grade a stage list exactly as an effect: failed wins, otherwise every stage must pass. */
export function caseOutcome(results) {
    if (passedAll(results)) return 'passed';
    return results.includes('failed') ? 'failed' : 'inconclusive';
}
const effectOf = outcome => ({ passed: 'supported', failed: 'unsupported', inconclusive: 'inconclusive' })[outcome];

const stageSummary = item => ({
    stage: item.featureId, result: item.result, inconclusiveReason: item.inconclusiveReason,
    acknowledgement: item.reply, // protocol acknowledgement only; never a pixel assertion
    assertions: item.assertions.map(({ description, passed }) => ({ description, passed })),
    causality: item.causality, classification: item.classification, timing: item.timing,
    measurement: { envelope: item.measurement.envelope, contentBounds: item.measurement.contentBounds },
});

/** Validity controls over the whole carrier run: every one must hold before any animation verdict is reported. */
export function validityControls(report, receipt, context, observationBytes, { suiteId = kittyAnimationCarrierSuiteId, policy = kittyAnimationCarrierPolicy, caseCount = animationCases.length, requireTimed = true } = {}) {
    const binding = receipt.animationEvidence, captures = report.snapshots.filter(frame => frame.capture);
    const pairs = captures.map(frame => frame.capture.nonces.join(',')), prior = new Set(report.priorChallenges);
    const stages = report.observations, blanks = stages.filter(item => item.featureId.endsWith('.blank'));
    const timed = stages.filter(item => item.timing.clock !== 'static');
    const controls = [
        ['receipt-binds-animation-report', binding?.runId === context.runId && binding.suiteId === suiteId && binding.sha256 === sha(observationBytes) && receipt.fixtureRemoved === true,
            'the complete receipt binds this exact report and records fixture removal'],
        ['carrier-policy', report.suiteId === suiteId && report.policy === policy && context.ownedAnimationPolicy === policy && report.runId === context.runId,
            policy === kittyAnimationCarrierPolicy ? 'the retained report is the paired raw render policy v2 animation report of this run' : `the retained report is the ${policy} animation report of this run`],
        ['raw-reader-healthy', report.seal.failed === false && report.seal.disposed === true && report.seal.attempts === report.actions.length,
            'the exclusive raw reader sealed without failure over every admitted write'],
        ['state-restored', report.initialSnapshot === 0 && report.restoredSnapshot === report.actions.length && report.snapshots.length === report.actions.length + 1,
            'the final paired state is bound to the initial state after every write'],
        ['controlled-background', report.backgroundControls.length === report.actions.length && report.backgroundControls.at(-1).snapshot === report.restoredSnapshot
            && report.backgroundControls.slice(0, -1).every(item => item.argb === 0xff101010 && same(item.query.channels16, [4112, 4112, 4112])),
            'every write before the final restoration independently queried the matching controlled OSC 11 and cell background; the final control is the restored original theme'],
        ['fresh-paired-fences', captures.length === report.actions.length && pairs.length === new Set(pairs).size && pairs.every(pair => !prior.has(pair)),
            'every capture carries its own unpredictable paired challenges, none reused from the earlier phases or another capture'],
        ['blank-before-controls', blanks.length === caseCount && blanks.every(item => item.result === 'passed' && item.assertions.length === 2 && item.assertions.every(value => value.passed)),
            'every case began from an independently blank, unchanged-surroundings target'],
        ['acknowledgement-separate-from-pixels', stages.every(item => item.reply?.passed === true) && stages.every(item => item.assertions.length > 0),
            'every write was acknowledged by its raw fence while pixel assertions were graded separately'],
        ['wall-clock-windows-valid', requireTimed ? timed.length > 0 && timed.every(item => item.timing.valid === true && item.timing.facts.capture.end >= item.timing.facts.capture.start) : timed.length === 0,
            requireTimed ? 'every timed read landed wholly inside its planned frame window, measured against retained wall-clock brackets rather than synthetic presentation time'
                : 'the suite has no timed stage: every observation is a static frame chosen by an explicit command, so no elapsed-time window is claimed'],
    ];
    return controls.map(([name, held, detail]) => ({ name, held, detail }));
}

/** Derive the effect verdict from the carrier stages; the carrier's own per-case result must agree. */
export function gradeEffect(report, contract = effectContract) {
    const cases = contract.carrierCases.map(caseId => {
        const stages = report.observations.filter(item => item.featureId.startsWith(`${caseId}.`));
        const carrier = report.caseResults.find(item => item.caseId === caseId);
        check(carrier && same(carrier.stages, stages.map(item => item.featureId)), `${caseId}: stage list differs`);
        const derived = caseOutcome(stages.map(item => item.result));
        check(carrier.result === derived, `${caseId}: carrier result ${carrier.result} differs from its stages (${derived})`);
        return { caseId, result: derived, stages: stages.map(stageSummary) };
    });
    return { contract, cases, outcome: effectOf(caseOutcome(cases.map(item => item.result))) };
}

const criteria = Object.freeze({
    termless: 'Declared Kitty graphics capability does not establish timed animation playback (always inconclusive, evidence legacy)',
    terminal: 'No timed frame playback or pixel readback for Kitty animation (always inconclusive, evidence none)',
});

/** Project one extracted, receipt-verified owned run. `archive` is the retained pinned-bundle copy. */
export async function kittyAnimationQualification(dir, source, archive) {
    const context = json(dir, 'context.json'), receipt = json(dir, 'cell-receipt.json'), raw = json(dir, 'raw.json'), stock = json(dir, 'summary.json');
    const observationBytes = readFileSync(join(dir, 'animation-observations.json')), report = JSON.parse(observationBytes);
    const validity = validityControls(report, receipt, context, observationBytes);
    check(validity.every(item => item.held), `validity control failed: ${validity.find(item => !item.held)?.name}`);
    check(context.upstreamBundleSha256 === upstreamBundleSha256 && raw.probeHash === '16a690f34c0e', 'upstream pin differs');
    const id = kittyAnimationQualificationIds[0], pinned = (await replayPinnedAnimationProbe(archive))[id], effect = gradeEffect(report);
    const stockCase = raw.observations.find(item => item.featureId === id);
    check(stockCase, `missing stock outcome: ${id}`);
    const termless = pinned.termless, terminal = pinned.terminal;
    check(stockCase.outcome === terminal.observation.outcome && stockCase.note === terminal.observation.note && stockCase.evidence === terminal.observation.evidence,
        `${id}: stock result differs from the pinned terminal callback`);
    const pinnedOutcome = termless.observation.outcome;
    const entry = { caseId: id, pinnedCriterion: criteria,
        pinned: { pass: termless.pass, note: termless.note, observation: termless.observation, termless, terminal },
        effectContract: { id: effect.contract.id, version: effect.contract.version, statement: effect.contract.statement, primary: effect.contract.primary, carrierCases: effect.contract.carrierCases, validity },
        contractResult: { outcome: effect.outcome, carrierCases: effect.cases.map(item => ({ caseId: item.caseId, result: item.result })) },
        observations: effect.cases, stock: stockCase,
        ...(pinnedOutcome === effect.outcome ? {} : { divergence: { kind: 'pinned-inconclusive-by-design', pinnedOutcome, contractOutcome: effect.outcome } }),
        conclusion: { outcome: effect.outcome, grading: 'pinned-callbacks-preserved-plus-owned-wall-clock-pixel-effect-v1', evidence: 'owned-controlled-pixels-paired-raw-render', note: termless.note } };
    const suite = { schemaVersion: 1, suiteId: kittyAnimationQualificationSuiteId, carrierSuiteId: kittyAnimationCarrierSuiteId, contractsVersion: kittyAnimationQualificationVersion, runId: context.runId,
        adapter: { id: context.adapter, upstreamBundleSha256: context.upstreamBundleSha256, pinnedSpans: pinnedSpanDigests },
        scope: 'One exact pinned terminfo.dev@4.0.0 case, extensions.kitty-graphics.animation. Both pinned callbacks (termless and terminal) are replayed from the retained pinned bundle and are always inconclusive. '
            + 'One separately identified effect contract composes the owned Kitty animation carrier: a retained 16x16 raw RGBA image with three solid frames held 5 s each, a stationary control, playback, '
            + 'loop and stop-freeze, plus half-transparent PNG frames and the root frame as a base and an edit target (chosen with a=a,c=, so no elapsed time is involved), over paired original offscreen PNGs, complete cell/mode reads, fresh raw fences and retained wall-clock brackets. '
            + 'Protocol acknowledgement and painted pixels are separate facts, and elapsed time is the App\'s own monotonic clock observed on a real desktop, not a synthetic presentation time. '
            + 'One image size, one gap length and one PNG encoding (8-bit RGBA, non-interlaced): no frame-rate or display-refresh cadence, other container formats or PNG encodings, a=c, snapshot/persistent-session replay, remote/mux, other platforms, common-harness or stock-score claim.',
        cases: [entry] };
    const summary = { schemaVersion: 1, measuredAt: stock.measuredAt, ownedRunId: context.runId, stockCollectorRunId: raw.runId, appRevision: source.appRevision, scriptsRevision: source.scriptsRevision,
        context, receipt, stockCollector: { counts: stock.counts, suiteComplete: stock.suiteComplete, rawSha256: stock.rawSha256, probeHash: raw.probeHash, sourceRevision: raw.sourceRevision },
        carrier: { suiteId: kittyAnimationCarrierSuiteId, policy: kittyAnimationCarrierPolicy, file: 'animation-observations.json', sha256: sha(observationBytes), counts: receipt.animationEvidence.counts,
            caseResults: report.caseResults.map(item => ({ caseId: item.caseId, result: item.result })) } };
    return { summary, report: suite };
}
