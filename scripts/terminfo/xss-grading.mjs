// Parent-side report builder for the xterm SGR-stack qualification. Re-derives every conclusion from the retained parent journal (screens and the raw reply lists bound to their barriers) and the
// independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: each pinned result is a pure function of the journaled bytes of its own exchange and the
// cursor of its read. Both pinned probes can only return inconclusive (they measure DA1 and cursor responsiveness, never stack state); the separately identified effect contract carries the graded
// conclusion and any difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateXssExchange } from './xss-report.mjs';
import { xssContracts, xssCaseIds, xssContractsVersion, xssNoncePrefix, xssPrefix, xssSuiteId, xssEffectContractId, gradeXssCase, xssObservation,
    validateXssScreen, replayXssPinned } from './xss-contracts.mjs';
import { xssAdapterId } from './xss-grading-collector.mjs';
import { xssRecordingContext, xssExpectedCalls } from './xss-collector.mjs';

const label = 'xterm-sgr-stack';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.xtpushsgr (CSI # {) and modes.xtpopsgr (CSI # }), which can only return inconclusive (they measure DA1 and cursor responsiveness, not '
    + 'saved/restored stack state): both pinned paths replayed byte-exactly over the journaled raw bytes of their own fenced exchange and the cursor of its read, beside the separately identified effect '
    + 'contract ntilde-xterm-sgr-stack-effect-v1 graded by an independent oracle written from xterm\'s control-sequence text over the same fence: silence of both sequences, and the stored bold, faint, '
    + 'italic, underline, blink, inverse, hidden, strikethrough, foreground and background of a probe character\'s cell after pops, the selection of each documented push parameter alone, the '
    + 'argument-less push saving all of them, LIFO order, snapshot copy semantics, ten levels holding and the baseline pen restored; every exchange must leave the complete screen outside the probe row, '
    + 'the cursor, margins, modes and active screen unchanged. Policy groups (the eleventh push, a pop with nothing saved, a value the text does not list, the overline and the underline color) record '
    + 'Ntilde behavior xterm\'s text does not specify and never decide a verdict. Stored cell attributes, not host presentation or painted pixels; no XTREPORTSGR claim; no stock grades.';
const refuse = message => { throw new Error(message); };
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);

/** Screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
function screenOf(value) {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateXssScreen(screen);
    return screen;
}

/** Validate journal, run identity, binding stability and restoration. */
export function validateXssEvidence(frames, run, runId, adapter) {
    const total = 3 + xssContracts.reduce((sum, contract) => sum + planReads(contract), 0);
    validateGradedJournal(frames, { frameCount: total, prefix: xssPrefix, noncePrefix: xssNoncePrefix, runId, label, screenOf });
    const identified = adapter?.id === xssAdapterId && run.adapterId === xssAdapterId && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256) && adapter.derivedFromAdapterId === derivedFrom;
    if (!identified) refuse(`Invalid ${label} adapter identity.`);
    if (!same(run.probeIds, [...xssCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== xssContracts.length) refuse(`${label} run does not cover the contracted cases.`);
    // The pen and the stack are the only state a case changes: the palette, theme and render state are proven the same before the first request, after the bind and after the finish.
    const edges = [frames[0], frames[1], frames.at(-1)];
    if (edges.some(edge => edge.report !== undefined)) refuse(`Unexpected ${label} report frame.`);
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (edges.some(edge => colorBindingHash(edge.screen.colorBinding) !== binding)) refuse(`${label} observation saw the palette or theme change.`);
}

/** The retained exchange of one report read: the journaled frame, the worker's record and the frame's own barrier must agree. A cells read carries none. */
function exchangeOf(contract, read, frame) {
    if (read.kind !== 'report') return frame.report === undefined && read.report === undefined ? null : refuse(`Unexpected ${contract.caseId} report on a cells read.`);
    const journaled = validateXssExchange(frame.report);
    if (!same(journaled, validateXssExchange(read.report)) || journaled.barrier !== frame.barrier) refuse(`${label} ${contract.caseId} replies differ from their read frame.`);
    return journaled;
}

/** The pinned results of one case, each checked against the replay of its own journaled exchange and the cursor of its read (and, given the derived bundle, the pinned code itself). */
function verifyPinned(contract, recorded, exchanges, frame, replayModule) {
    const { termless, terminal } = recorded.pinned ?? {};
    const exchange = exchanges[contract.pinnedRead];
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) refuse(`Missing pinned ${contract.caseId} grading results.`);
    if (exchange === null) refuse(`Pinned ${contract.caseId} exchange is missing.`);
    const cursor = [frame.screen.cursorRow, frame.screen.cursorCol];
    requirePinnedAgreement(contract, termless, replayXssPinned(contract.caseId, 'termless', { replies: exchange.replies.join(''), barrier: exchange.barrier }), label);
    requirePinnedAgreement(contract, terminal, replayXssPinned(contract.caseId, 'terminal', { position: { row: cursor[0] + 1, col: cursor[1] + 1 } }), label);
    if (replayModule) {
        const context = xssRecordingContext(contract, 'termless', exchange, cursor);
        requireReplayAgreement(contract, replayModule.ntildeRunXssTermless(contract.caseId, context), termless);
        if (context.callCount !== xssExpectedCalls(contract, 'termless', exchange)) refuse(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
}

/** One pinned path as published. */
const pinnedPath = result => ({ pass: result.pass, response: result.response ?? null, note: result.note ?? null, observation: result.observation, assertions: result.assertions ?? null });
/** The recorded theme binding of a case. */
const bindingOf = theme => ({ reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
    minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) });

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildXssGradingReport(frames, run, runId, adapter, { contractsVersion = xssContractsVersion, replayModule = null } = {}) {
    validateXssEvidence(frames, run, runId, adapter);
    let next = 2;
    const cases = xssContracts.map((contract, position) => {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) refuse(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[next++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const exchanges = recorded.reads.map((read, index) => exchangeOf(contract, read, readFrames[index]));
        const replies = exchanges.map(exchange => exchange?.replies ?? []);
        const observation = xssObservation(contract, readFrames.map(frame => frame.screen), replies, xssNoncePrefix + runId);
        const effect = gradeXssCase(contract, observation, contractsVersion);
        const { termless, terminal } = verifyPinned(contract, recorded, exchanges, readFrames[contract.pinnedRead], replayModule);
        const divergence = termless.observation.outcome === effect.outcome ? null
            : { pinnedOutcome: termless.observation.outcome, contractOutcome: effect.outcome, kind: `pinned-${termless.observation.outcome}-effect-${effect.outcome}` };
        const observed = contract.observed(observation);
        return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedScript: observed.pinnedScript, terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, ...(read.kind === 'report' ? { replies: replies[index] } : {}) })), observed,
            colorBinding: bindingOf(readFrames[0].screen.colorBinding), pinned: { termless: pinnedPath(termless), terminal: pinnedPath(terminal) },
            effectContract: { id: xssEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}), attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
            contractResult: effect, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-stored-cells', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
                reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
    });
    return { schemaVersion: 1, suiteId: xssSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
