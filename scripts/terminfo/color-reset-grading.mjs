// Parent-side report builder for the runtime-color reset qualification. Re-derives every conclusion from the retained parent journal
// (screens, exact query replies bound to their barriers, render details and the original PNGs) and the independent contract grader,
// refuses any tampering, and requires the pinned termless probes' own grading to agree: each pinned result is a pure function of the
// three journaled replies of its own pinned experiment. The contract may never be more permissive than the pinned probe; every difference
// in verdict semantics is recorded with both verdicts, and the pinned terminal path (inconclusive by design) is recorded beside them.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { reconcile } from './sgr-color-grading-core.mjs';
import { validateQueries, stableColorContext, pristineColorTheme } from './color-capture.mjs';
import { colorResetContracts, colorResetCaseIds, colorResetContractsVersion, colorResetNoncePrefix, colorResetSuiteId, colorResetReadPlan,
    gradeColorResetCase, colorResetObservation, validateColorResetScreen } from './color-reset-contracts.mjs';
import { colorResetAdapterId } from './color-reset-grading-collector.mjs';
import { colorResetRecordingContext, pinnedReplies } from './color-reset-collector.mjs';
import { retainedColorResetCapture } from './color-reset-capture.mjs';

const label = 'Runtime-color reset';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 runtime-color reset termless criteria (OSC 4;0 and 104;0, OSC 10 and 110, OSC 11 and 111, OSC 12 and 112, each set to rgb:aa/bb/cc and reset with the exact BEL '
    + 'spelling) replayed byte-exactly on a disposable App, graded from exact fenced replies, the App-resolved colors and override metadata, the stored default markers, palette-indexed and '
    + 'literal cells, the cursor and the controlled offscreen render: an independent runtime-color model demands, at every stage, the replies, the readScreen binding, the render theme '
    + 'and every explicit cell the bytes written so far leave behind over the measured baseline. Controls cover individual and whole-palette resets, the other overrides surviving a '
    + 'dynamic-color reset, SGR 39/49 versus the runtime defaults and a hidden steady-beam cursor. The synthetic render phase is fixed (clock 0, scale 1); no real-time cadence, desktop '
    + 'appearance, selection or pointer color, Kitty named color, alternate-screen or DECSTR clearing claim; no stock grades.';

/** Runtime-color screens carry attributes, stored details (with overline), the cursor mode and resolved colors. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, false, true);
    validateColorResetScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + colorResetContracts.reduce((sum, contract) => sum + planReads(contract), 0);

// ---------------------------------------------------------------------------------------------------------------------------
// The pinned replay: a pure function of the three replies, the pinned result byte for byte in behavior.

/** The first complete `rgb:` group of one OSC color report in `raw` for this set code and index, or undefined: the pinned unanchored, case-insensitive pattern
 * (`rgb:` and one to four hex digits per channel, then BEL or ST). Written without a regex so no control character sits in a pattern. */
function replyColor(raw, setCode, indexPart) {
    const head = `\x1b]${setCode};${indexPart}rgb:`, lowered = raw.toLowerCase(), hex = '0123456789abcdef';
    for (let at = lowered.indexOf(head); at >= 0; at = lowered.indexOf(head, at + 1)) {
        let cursor = at + head.length, ok = true;
        for (let channel = 0; channel < 3 && ok; channel++) {
            const begin = cursor;
            while (cursor < raw.length && cursor - begin <= 4 && hex.includes(lowered[cursor])) cursor++;
            ok = cursor - begin >= 1 && cursor - begin <= 4 && (channel === 2 || lowered[cursor] === '/');
            if (channel < 2) cursor++;
        }
        const terminated = lowered[cursor] === '\x07' || (lowered[cursor] === '\x1b' && lowered[cursor + 1] === '\\');
        if (ok && terminated) return raw.slice(at + head.length - 4, cursor);
    }
    return undefined;
}

/** The pinned sameRgb2: channels scaled to sixteen bits and compared. */
const sixteenBit = color => color.slice(4).split('/').map(channel => Math.round(Number.parseInt(channel, 16) / (16 ** channel.length - 1) * 65535));
const sameRgb2 = (left, right) => {
    const actual = sixteenBit(left), expected = sixteenBit(right);
    return actual.every((channel, index) => channel === expected[index]);
};
const probeForeground = before => (sameRgb2(before, 'rgb:aa/bb/cc') ? 'rgb:12/34/56' : 'rgb:aa/bb/cc');

/** The pinned colorResetProbe result over three replies (a reply is `{ raw }` or null), key order included. */
export function replayColorResetPinned(contract, replies) {
    const { setCode, resetCode } = contract;
    const indexPart = setCode === 4 ? '0;' : '';
    const raws = replies.map(reply => reply?.raw ?? '');
    const [originalRaw, changedRaw, restoredRaw] = raws;
    const original = replyColor(originalRaw, setCode, indexPart);
    if (!original) {
        const invalid = originalRaw.includes(`\x1b]${setCode};`);
        return { pass: false, response: originalRaw, observation: { outcome: 'inconclusive', reason: invalid ? 'invalid-reply' : 'no-response', evidence: 'query' } };
    }
    const requested = probeForeground(original);
    const changed = replyColor(changedRaw, setCode, indexPart), restored = replyColor(restoredRaw, setCode, indexPart);
    const response = JSON.stringify({ originalRaw, changedRaw, restoredRaw });
    if (!changed || !restored || !sameRgb2(changed, requested)) {
        return { pass: false, response, observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query', note: 'Color mutation or readback control was not established' } };
    }
    const pass = sameRgb2(restored, original);
    return { pass, response, observation: { outcome: pass ? 'supported' : 'unsupported', evidence: 'behavior' },
        assertions: [{ kind: pass ? 'positive' : 'negative', expected: `OSC ${resetCode} restores the prior OSC ${setCode} color after a verified change`, observed: response }] };
}

// ---------------------------------------------------------------------------------------------------------------------------
// Journal validation.

/** One frame's render: the original PNG bytes against the frame's screen and the shared render context. */
function validateCaptures(frames, captureOf) {
    const context = stableColorContext(frames[0].pixel.renderDetails);
    let total = 0;
    for (const [index, frame] of frames.entries()) {
        if (frame.afterSha256 !== frame.sha256 || snapshotHash(screenOf(frame.afterScreen)) !== frame.sha256) throw new Error(`${label} bracketing state changed around a capture.`);
        if (frame.pixel?.file !== `crst-pixels/frame-${String(index).padStart(3, '0')}.png` || frame.pixel.presentationTimeMs !== 0
            || frame.pixel.scale !== 1 || frame.pixel.downscaled !== false || frame.pixel.inlineOmitted !== false) throw new Error(`Invalid ${label} capture binding.`);
        const capture = captureOf(frame);
        if (snapshotHash(capture.details) !== snapshotHash(frame.pixel.renderDetails) || stableColorContext(frame.pixel.renderDetails) !== context) throw new Error(`Changed ${label} render context.`);
        total += frame.pixel.width * frame.pixel.height;
    }
    if (total > 64_000_000) throw new Error(`${label} pixel budget exhausted.`);
}

/** Validate journal, run identity, binding stability, query bindings and restoration. */
export function validateColorResetEvidence(frames, run, runId, adapter, captureOf) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: 'crst', noncePrefix: colorResetNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== colorResetAdapterId || run.adapterId !== colorResetAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...colorResetCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== colorResetContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    validateCaptures(frames, captureOf);
    // The theme identity (name, reverse video, minimum contrast) cannot change; the colors may, by design, between experiments' stages.
    const { themeName, reverseVideo, minimumContrast } = frames[0].screen.colorBinding;
    if (reverseVideo !== false || frames.some(frame => frame.screen.colorBinding.themeName !== themeName || frame.screen.colorBinding.reverseVideo !== reverseVideo
        || frame.screen.colorBinding.minimumContrast !== minimumContrast)) throw new Error(`${label} observation saw the theme identity change.`);
    // Runtime colors are session-wide: the suite starts from a pristine private theme and finishes back at the same render theme and PNG.
    if (!pristineColorTheme(frames[0].pixel.renderDetails.theme) || snapshotHash(frames[0].pixel.renderDetails.theme) !== snapshotHash(frames.at(-1).pixel.renderDetails.theme)
        || frames[0].pixel.sha256 !== frames.at(-1).pixel.sha256) throw new Error(`${label} runtime colors were not restored.`);
    // Only reads carry replies.
    if ([frames[0], frames[1], frames.at(-1)].some(frame => frame.queries.length || frame.caseId !== null)) throw new Error(`Unexpected ${label} reply frame.`);
}

/** Each read frame's journaled replies must equal the worker's record, and each reply must sit at the barrier of the exchange that asked it. */
function checkReplies(contract, recorded, readFrames) {
    let feedIndex = 0, readIndex = 0, asked = 0;
    for (const step of contract.plan) {
        if (step.feed !== undefined) {
            if (step.query) {
                const item = recorded.reads[readIndex].queries[asked];
                if (!item || item.barrier !== recorded.barriers[feedIndex] || snapshotHash(item.spec) !== snapshotHash(step.query)) throw new Error(`${label} ${contract.caseId} reply differs from its exchange.`);
                asked++;
            }
            feedIndex++;
        } else {
            const frame = readFrames[readIndex], read = recorded.reads[readIndex], plan = colorResetReadPlan.find(item => item.caseId === contract.caseId
                && `${item.experimentId}/${item.stageId}` === contract.roles[readIndex]);
            if (snapshotHash(frame.queries) !== snapshotHash(read.queries) || frame.queries.length !== asked) throw new Error(`${label} ${contract.caseId} replies differ from their read frame.`);
            validateQueries(frame.queries, plan.queries);
            readIndex++; asked = 0;
        }
    }
}

/** The one pinned result a case's three pinned replies give, replayed: the pure port, then (when supplied) the live pinned module. */
function checkPinned(contract, recorded, readFrames, replayModule) {
    const replies = pinnedReplies(contract, recorded.reads);
    const pinned = recorded.pinned;
    if (pinned?.probeId !== contract.caseId || !['query', 'behavior'].includes(pinned.observation?.evidence)) throw new Error(`Missing pinned ${contract.caseId} grading result.`);
    requirePinnedAgreement(contract, pinned, replayColorResetPinned(contract, replies), label);
    if (replayModule) {
        const replay = replayModule.ntildeRunColorResetTermless(contract.caseId, colorResetRecordingContext(contract, replies));
        requireReplayAgreement(contract, replay, pinned);
    }
    return pinned;
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildColorResetGradingReport(frames, run, runId, adapter, { contractsVersion = colorResetContractsVersion, replayModule = null, output = null, captureOf = null } = {}) {
    const load = captureOf ?? (output === null ? null : frame => retainedColorResetCapture(output, frame));
    if (load === null) throw new Error(`${label} evidence needs its retained captures.`);
    validateColorResetEvidence(frames, run, runId, adapter, load);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of colorResetContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        checkReplies(contract, recorded, readFrames);
        for (const frame of readFrames) if (frame.caseId !== contract.caseId) throw new Error(`${label} ${contract.caseId} read frame belongs to another case.`);
        const observation = colorResetObservation(contract, readFrames.map(frame => ({ screen: frame.screen, queries: frame.queries,
            details: frame.pixel.renderDetails, pixelSha256: frame.pixel.sha256 })));
        const contractResult = gradeColorResetCase(contract, observation, contractsVersion);
        const pinned = checkPinned(contract, recorded, readFrames, replayModule);
        const divergence = reconcile(contract, pinned.observation.outcome, contractResult);
        const base = frames[0].screen.colorBinding;
        cases.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds, terminalFixture: contract.terminalFixture,
            feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => ({ cursor: read.cursor, queries: read.queries.map(query => ({ query: query.spec, barrier: query.barrier, reply: query.reply?.raw ?? null })) })),
            observed: contract.observed(observation),
            colorBinding: { themeName: base.themeName, minimumContrast: base.minimumContrast, reverseVideo: base.reverseVideo, foregroundArgb: base.foregroundArgb, backgroundArgb: base.backgroundArgb,
                cursorArgb: frames[0].pixel.renderDetails.theme.cursorArgb, paletteSha256: snapshotHash(base.ansiPaletteArgb) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null },
            pinnedTerminal: { outcome: 'inconclusive', evidence: 'none', byDesign: true, note: contract.terminalFixture.note },
            contractResult, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: contractResult.outcome, evidence: contract.evidence, grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                pinnedTermless: pinned.observation.outcome, pinnedTerminal: 'inconclusive-by-design',
                ...(contractResult.note ? { note: contractResult.note } : {}), scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: colorResetSuiteId, runId, contractsVersion, adapter, scope,
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
