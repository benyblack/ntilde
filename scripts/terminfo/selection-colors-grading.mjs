// Parent-side report builder for the selection-color qualification. Re-derives every conclusion from the retained parent journal
// (screens, exact query replies bound to their barriers, render details and the original PNGs) and the independent contract grader,
// refuses any tampering, and requires the pinned termless probes' own grading to agree: each set-case pinned result is a pure function
// of the one journaled default-state reply of its own pinned experiment, and each reset-case pinned result of the journaled cell (0,0)
// of its own pinned experiment. The contract may never be more permissive than the pinned probe; every difference in verdict semantics
// is recorded with both verdicts (the set cases' pinned query cannot decide against an unset default; the reset cases' pinned probes are
// inconclusive by construction), and the pinned terminal paths are recorded beside them, never run as verdicts.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads, checkFencedQueryReplies } from './graded-plan.mjs';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { stableColorContext, pristineColorTheme } from './color-capture.mjs';
import { selectionColorsContracts, selectionColorsCaseIds, selectionColorsContractsVersion, selectionColorsNoncePrefix, selectionColorsSuiteId, selectionColorsReadPlan,
    gradeSelectionColorsCase, selectionColorsObservation, validateSelectionColorsScreen, selectionColorsFixture } from './selection-colors-contracts.mjs';
import { selectionColorsAdapterId } from './selection-colors-grading-collector.mjs';
import { selectionColorsRecordingContext, pinnedReplies, pinnedScreen, pinnedCellAt } from './selection-colors-collector.mjs';
import { retainedSelectionColorsCapture } from './selection-colors-capture.mjs';
import { regionPixels, countColorCoverage } from './pixels.mjs';
import { argbAt, solidColor, dominantInk } from './color-reset-grading.mjs';
import { opaqueArgb, parseColorSpec } from './selection-colors-model.mjs';

const label = 'Selection-color';
const derivedFrom = 'ntilde-owned-osc-title-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 selection-color criteria (extensions.osc17-highlight-bg and extensions.osc19-highlight-fg, one BEL-spelled query each; '
    + 'extensions.osc117-reset-highlight-bg and extensions.osc119-reset-highlight-fg, the whole pinned registration objects replayed byte-exactly) on a disposable App, '
    + 'graded from exact fenced replies (an unset selection source demands the explicit absence of a reply behind the completed ordering barrier, never an invented RGB value), '
    + 'the App-resolved colors and override metadata, the stored default markers, palette-indexed and literal cells, the cursor and the controlled offscreen render with the '
    + 'established synthetic selection (2,4)..(4,10): an independent selection-color model demands, at every stage, the replies, the readScreen binding, the render theme '
    + 'sources and every explicit cell the bytes written so far leave behind over the measured baseline. Controls cover a second distinguishable value against stale replies, '
    + 'the BEL and ST spellings, each override surviving the other channel\'s reset in both orders, an unrelated palette slot and the ordinary dynamic colors, and the exact '
    + 'baseline restoration. The synthetic render phase is fixed (clock 0, cursor hidden, scale 1); no real-time cadence, desktop appearance, physical mouse selection, native '
    + 'window appearance, pointer color, clipboard behavior, other themes or platforms, alternate-screen or DECSTR clearing claim; no stock grades.';

/** Selection-color screens carry attributes, stored details (with overline), the cursor mode and resolved colors. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, false, true);
    validateSelectionColorsScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + selectionColorsContracts.reduce((sum, contract) => sum + planReads(contract), 0);

// ---------------------------------------------------------------------------------------------------------------------------
// The pinned replay: a pure function of the pinned experiment's journaled evidence, the pinned result byte for byte in behavior.

/** The pinned oscReplyResult over one captured raw reply: supported iff a complete reply is present (the fence only ever delivers a
 * complete ST reply or explicit absence), inconclusive (`invalid-reply`) when the raw carries the prefix without a complete reply,
 * inconclusive (`no-response`) otherwise. Key order is the pinned code's. */
function replayOscReplyResult(raw, frame, expected, prefixText) {
    if (frame) {
        return {
            pass: true,
            response: raw,
            observation: { outcome: 'supported', evidence: 'query' },
            assertions: [{ kind: 'positive', expected, observed: frame }]
        };
    }
    return {
        pass: false,
        response: raw,
        observation: {
            outcome: 'inconclusive',
            reason: raw.includes(prefixText) ? 'invalid-reply' : 'no-response',
            evidence: 'query'
        }
    };
}

/** The pinned set-case result over its one reply (a reply is `{ raw }` or null), key order included. */
export function replaySelectionColorQuery(contract, replies) {
    const reply = replies[0], raw = reply?.raw ?? '';
    const complete = typeof raw === 'string' && raw.length > 0 && raw.toLowerCase().startsWith(`\x1b]${contract.code};rgb:`)
        && (raw.endsWith('\x07') || raw.endsWith('\x1b\\'));
    return replayOscReplyResult(raw, complete ? raw : null, `Complete OSC ${contract.code} color reply`, `\x1b]${contract.code};`);
}

/** The pinned reset-case result over its journaled cell (0,0), key order included: `notTestedResult`, whatever the cell holds. */
export function replaySelectionColorResetTermless(contract, screen) {
    const cell = pinnedCellAt(screen, 0, 0);
    return {
        pass: false,
        response: JSON.stringify({ cell }),
        note: contract.code === 117 ? 'highlight background color' : 'highlight foreground color',
        notTested: { reason: 'no-semantic-observable', noObservable: contract.code === 117 ? 'highlight background color' : 'highlight foreground color' }
    };
}

// ---------------------------------------------------------------------------------------------------------------------------
// The colors the controlled render drew, read from the original PNG. The pixel helpers (argbAt, solidColor,
// dominantInk) are imported from the runtime-color reset grading module, which has the identical definitions.

/** The scene areas the samples read: an untouched default-background cell, the unselected explicit-RGB control row, the selected blank
 * row and the selected default-colored text row. */
const sampleAreas = Object.freeze({
    background: { row: 8, col: 50, rows: 1, cols: 1 },
    control: { row: 1, col: 4, rows: 1, cols: 7 },
    selectionBlank: { row: 2, col: 5, rows: 1, cols: 5 },
    selectedInk: { row: 3, col: 4, rows: 1, cols: 7 },
});

const rgba = argb => [(argb >>> 16) & 255, (argb >>> 8) & 255, argb & 255, argb >>> 24];

/** What one controlled render drew, against the baseline render: the untouched background cell, whether the unselected control region is
 * byte-identical to the baseline's, the selected blank region (its solid color, if any, and whether its bytes equal the baseline's) and
 * the selected text ink (its dominant non-fill color, whether that dominant ink equals the baseline's, and how many pixels carry each of
 * the suite's two distinguishable override values). The ink region's fill is the selection paint, so a background override legitimately
 * changes the region's bytes; the ink itself is what must stay or follow the model. */
export function renderSamples(image, details, baselineImage) {
    const read = area => regionPixels(image, details.grid, area);
    const readBaseline = area => regionPixels(baselineImage, details.grid, area);
    const blank = read(sampleAreas.selectionBlank), ink = read(sampleAreas.selectedInk);
    const baselineBlank = readBaseline(sampleAreas.selectionBlank), baselineInk = readBaseline(sampleAreas.selectedInk);
    const fill = solidColor(blank), baselineFill = solidColor(baselineBlank);
    const dominant = fill === null ? null : dominantInk(ink, fill);
    const baselineDominant = baselineFill === null ? null : dominantInk(baselineInk, baselineFill);
    const firstValue = opaqueArgb(parseColorSpec(selectionColorsFixture.overrideA)), secondValue = opaqueArgb(parseColorSpec(selectionColorsFixture.overrideB));
    const coverage = argb => fill === null ? 0 : countColorCoverage(ink, rgba(argb), rgba(fill));
    return {
        background: solidColor(read(sampleAreas.background)),
        controlUnchanged: read(sampleAreas.control).equals(readBaseline(sampleAreas.control)),
        selectionBlank: { solid: fill, matchesBaseline: blank.equals(baselineBlank) },
        selectedInk: { dominant, matchesBaseline: ink.equals(baselineInk), dominantMatchesBaseline: dominant !== null && dominant === baselineDominant,
            firstValueCoverage: coverage(firstValue), secondValueCoverage: coverage(secondValue) },
    };
}

// ---------------------------------------------------------------------------------------------------------------------------
// Journal validation.

/** One frame's render: the original PNG bytes against the frame's screen and the shared render context. */
function validateCaptures(frames, captureOf) {
    const context = stableColorContext(frames[0].pixel.renderDetails);
    let total = 0;
    for (const [index, frame] of frames.entries()) {
        if (frame.afterSha256 !== frame.sha256 || snapshotHash(screenOf(frame.afterScreen)) !== frame.sha256) throw new Error(`${label} bracketing state changed around a capture.`);
        if (frame.pixel?.file !== `selc-pixels/frame-${String(index).padStart(3, '0')}.png` || frame.pixel.presentationTimeMs !== 0
            || frame.pixel.scale !== 1 || frame.pixel.downscaled !== false || frame.pixel.inlineOmitted !== false
            || snapshotHash(frame.pixel.syntheticSelection ?? null) !== snapshotHash(selectionColorsFixture.selectionColorsSelection)) {
            throw new Error(`Invalid ${label} capture binding.`);
        }
        const capture = captureOf(frame);
        if (snapshotHash(capture.details) !== snapshotHash(frame.pixel.renderDetails) || stableColorContext(frame.pixel.renderDetails) !== context) throw new Error(`Changed ${label} render context.`);
        total += frame.pixel.width * frame.pixel.height;
    }
    if (total > 64_000_000) throw new Error(`${label} pixel budget exhausted.`);
}

/** Validate journal, run identity, binding stability, query bindings and restoration. */
export function validateSelectionColorsEvidence(frames, run, runId, adapter, captureOf) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: 'selc', noncePrefix: selectionColorsNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== selectionColorsAdapterId || run.adapterId !== selectionColorsAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...selectionColorsCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== selectionColorsContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    validateCaptures(frames, captureOf);
    // The theme identity (name, reverse video, minimum contrast) cannot change; the colors may, by design, between experiments' stages.
    const { themeName, reverseVideo, minimumContrast } = frames[0].screen.colorBinding;
    if (reverseVideo !== false || frames.some(frame => frame.screen.colorBinding.themeName !== themeName || frame.screen.colorBinding.reverseVideo !== reverseVideo
        || frame.screen.colorBinding.minimumContrast !== minimumContrast)) throw new Error(`${label} observation saw the theme identity change.`);
    // Selection colors are session-wide: the suite starts from a pristine private theme and finishes back at the same render theme and PNG.
    if (!pristineColorTheme(frames[0].pixel.renderDetails.theme) || snapshotHash(frames[0].pixel.renderDetails.theme) !== snapshotHash(frames.at(-1).pixel.renderDetails.theme)
        || frames[0].pixel.sha256 !== frames.at(-1).pixel.sha256) throw new Error(`${label} selection colors were not restored.`);
    // Only reads carry replies.
    if ([frames[0], frames[1], frames.at(-1)].some(frame => frame.queries.length || frame.caseId !== null)) throw new Error(`Unexpected ${label} reply frame.`);
}

/** Each read frame's journaled replies must equal the worker's record, and each reply must sit at the barrier of the exchange that asked it. */
function checkReplies(contract, recorded, readFrames) {
    return checkFencedQueryReplies({ contracts: selectionColorsContracts, readPlan: selectionColorsReadPlan, label }, contract, recorded, readFrames);
}

/** The one pinned result a case's pinned evidence gives, replayed: the pure port, then (when supplied) the live pinned module.
 * The reset cases' pinned code reads the journaled screen of its own pinned experiment; the set cases' its one journaled reply.
 * The reset cases' pinned result has no `observation` (it is a `notTestedResult`), so its replay agreement is checked
 * field by field here; the shared `requireReplayAgreement` assumes an observation and is used for the set cases only. */
function checkPinned(contract, recorded, readFrames, replayModule) {
    const pinned = recorded.pinned;
    const pinnedScreens = readFrames.map(frame => frame.screen);
    if (contract.isResetCase) {
        if (pinned?.probeId !== contract.caseId || pinned?.notTested?.reason !== 'no-semantic-observable') throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        const screen = pinnedScreen(contract, pinnedScreens);
        const replay = replaySelectionColorResetTermless(contract, screen);
        requirePinnedAgreement(contract, pinned, replay, label);
        if (replayModule) {
            const live = replayModule.ntildeRunSelectionColorsTermless(contract.caseId, selectionColorsRecordingContext(contract, [], screen));
            if (live.pass !== pinned.pass || live.response !== pinned.response || (live.note ?? null) !== (pinned.note ?? null)
                || JSON.stringify(live.notTested) !== JSON.stringify(pinned.notTested)) {
                throw new Error(`Pinned ${contract.caseId} replay does not reproduce the live grading.`);
            }
        }
        return pinned;
    }
    if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'query') throw new Error(`Missing pinned ${contract.caseId} grading result.`);
    requirePinnedAgreement(contract, pinned, replaySelectionColorQuery(contract, pinnedReplies(contract, recorded.reads)), label);
    if (replayModule) {
        const replay = replayModule.ntildeRunSelectionColorsTermless(contract.caseId, selectionColorsRecordingContext(contract, pinnedReplies(contract, recorded.reads), null));
        requireReplayAgreement(contract, replay, pinned);
    }
    return pinned;
}

/** Compare the pinned and contract verdicts. The pinned set-case query cannot decide against an unset default; the pinned reset-case
 * probes are inconclusive by construction. The contract must never grade supported where a pinned probe decided unsupported, and every
 * other difference is recorded with both verdicts. */
export function reconcileSelectionColors(contract, pinnedOutcome, contractResult) {
    if (pinnedOutcome === contractResult.outcome) return null;
    if (contractResult.outcome === 'supported') {
        if (pinnedOutcome !== 'inconclusive') throw new Error(`Contract ${contract.caseId} grading is more permissive than the pinned probe.`);
        return { pinnedOutcome, contractOutcome: contractResult.outcome,
            kind: contract.isResetCase ? 'pinned-inconclusive-by-design' : 'pinned-query-only-unset-default' };
    }
    if (pinnedOutcome === 'inconclusive' && contractResult.outcome === 'unsupported') {
        return { pinnedOutcome, contractOutcome: contractResult.outcome, kind: 'contract-measured-mismatch' };
    }
    throw new Error(`Pinned and contract ${contract.caseId} grading disagree.`);
}

/** The report entry of one case, after every cross-check of its journal slice. */
function caseReport({ contract, recorded, readFrames, frames, samples, contractsVersion, replayModule }) {
    validateGradedCaptureRecord(contract, recorded, label);
    crossCheckGradedCapture(contract, recorded, readFrames, label);
    checkReplies(contract, recorded, readFrames);
    for (const frame of readFrames) if (frame.caseId !== contract.caseId) throw new Error(`${label} ${contract.caseId} read frame belongs to another case.`);
    const observation = selectionColorsObservation(contract, readFrames.map(frame => ({ screen: frame.screen, queries: frame.queries,
        details: frame.pixel.renderDetails, pixelSha256: frame.pixel.sha256, render: samples.get(frame) })));
    const contractResult = gradeSelectionColorsCase(contract, observation, contractsVersion);
    const pinned = checkPinned(contract, recorded, readFrames, replayModule);
    const divergence = reconcileSelectionColors(contract, pinned.observation?.outcome ?? (pinned.notTested ? 'inconclusive' : undefined), contractResult);
    const base = frames[0].screen.colorBinding;
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds, terminalFixture: contract.terminalFixture,
        feeds: recorded.feeds, barriers: recorded.barriers,
        reads: recorded.reads.map(read => ({ cursor: read.cursor, queries: read.queries.map(query => ({ query: query.spec, barrier: query.barrier, reply: query.reply?.raw ?? null })) })),
        observed: contract.observed(observation),
        colorBinding: { themeName: base.themeName, minimumContrast: base.minimumContrast, reverseVideo: base.reverseVideo, foregroundArgb: base.foregroundArgb, backgroundArgb: base.backgroundArgb,
            cursorArgb: frames[0].pixel.renderDetails.theme.cursorArgb, paletteSha256: snapshotHash(base.ansiPaletteArgb) },
        pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, notTested: pinned.notTested ?? null,
            observation: pinned.observation ?? null, assertions: pinned.assertions ?? null },
        pinnedTerminal: { outcome: 'inconclusive', evidence: contract.isResetCase ? 'consumed' : 'query', byDesign: contract.isResetCase, note: contract.terminalFixture.note },
        contractResult, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: contractResult.outcome, evidence: contract.evidence, grading: 'pinned-termless-plus-contract-v' + contractsVersion,
            pinnedTermless: pinned.observation?.outcome ?? 'inconclusive', pinnedTerminal: contract.isResetCase ? 'inconclusive-by-design' : 'inconclusive-default-state',
            ...(contractResult.note ? { note: contractResult.note } : {}), scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildSelectionColorsGradingReport(frames, run, runId, adapter, { contractsVersion = selectionColorsContractsVersion, replayModule = null, output = null, captureOf = null } = {}) {
    const load = captureOf ?? (output === null ? null : (frame => retainedSelectionColorsCapture(output, frame)));
    if (load === null) throw new Error(`${label} evidence needs its retained captures.`);
    validateSelectionColorsEvidence(frames, run, runId, adapter, load);
    // Render samples compare each read frame against its own experiment's baseline read (the first read of the same
    // experiment): the start/bind frames show the pristine host without the scene, so they are not render baselines.
    const baselineFrameOf = new Map();
    frames.forEach((frame, index) => {
        if (index < 2 || index >= frames.length - 1) return;
        const entry = selectionColorsReadPlan[index - 2];
        const key = `${entry.caseId}/${entry.experimentId}`;
        if (!baselineFrameOf.has(key)) baselineFrameOf.set(key, frame);
    });
    const samples = new Map(frames.map(frame => {
        const index = frames.indexOf(frame);
        const entry = index >= 2 && index < frames.length - 1 ? selectionColorsReadPlan[index - 2] : null;
        const baseline = entry ? baselineFrameOf.get(`${entry.caseId}/${entry.experimentId}`) : frames[0];
        return [frame, renderSamples(load(frame).image, frame.pixel.renderDetails, load(baseline).image)];
    }));
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of selectionColorsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        cases.push(caseReport({ contract, recorded, readFrames, frames, samples, contractsVersion, replayModule }));
    }
    return { schemaVersion: 1, suiteId: selectionColorsSuiteId, runId, contractsVersion, adapter, scope,
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}

/** The outcome counts of the case conclusions. */
export function selectionColorsCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}
