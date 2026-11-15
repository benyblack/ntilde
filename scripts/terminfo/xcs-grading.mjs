// Parent-side report builder for the xterm color-stack qualification. Re-derives every conclusion from the retained parent journal (screens and the raw reply lists bound to their barriers) and the
// independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: each pinned result is a pure function of the journaled bytes of its own exchange and the
// cursor of its read. Both pinned probes can only return inconclusive (they measure DA1 and cursor responsiveness, never stack state); the separately identified effect contract carries the graded
// conclusion and any difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateXcsExchange } from './xcs-report.mjs';
import { xcsContracts, xcsCaseIds, xcsContractsVersion, xcsNoncePrefix, xcsPrefix, xcsSuiteId, xcsEffectContractId, gradeXcsCase, xcsObservation,
    validateXcsScreen, replayXcsPinned } from './xcs-contracts.mjs';
import { xcsAdapterId } from './xcs-grading-collector.mjs';
import { xcsRecordingContext, xcsExpectedCalls } from './xcs-collector.mjs';

const label = 'xterm-color-stack';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 criteria for modes.xtpushcolors (CSI # P) and modes.xtpopcolors (CSI # Q), which can only return inconclusive (they measure DA1 and cursor responsiveness, not '
    + 'saved/restored stack state): both pinned paths replayed byte-exactly over the journaled raw bytes of their own fenced exchange and the cursor of its read, beside the separately identified effect '
    + 'contract ntilde-xterm-color-stack-effect-v1 graded by an independent oracle written from xterm\'s control-sequence text over the same fence: silence of both sequences, snapshot and restoration of the '
    + 'foreground, background and cursor colors and ANSI palette entry 5 read back through OSC 10/11/12 and OSC 4 replies, restoration of the untouched host default, LIFO order, snapshot copy semantics '
    + 'and numbered slots; every exchange must leave the complete screen, cursor, rendition, margins, modes and active screen unchanged. Policy groups (palette entry 200, a pop with nothing stored) record '
    + 'Ntilde behavior xterm\'s text does not specify and never decide a verdict. Raw terminal state, not host presentation or painted pixels; no depth-report claim; no stock grades.';

/** Screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateXcsScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + xcsContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. */
export function validateXcsEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: xcsPrefix, noncePrefix: xcsNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== xcsAdapterId || run.adapterId !== xcsAdapterId || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') || adapter.derivedFromAdapterId !== derivedFrom) {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...xcsCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== xcsContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    // Every case recolors the pane (that is what it measures), so its reads legitimately carry another color binding; the palette, theme and render state is proven the same before the first
    // request, after the bind and after the finish (every color reset back to the host's).
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (colorBindingHash(frames[1].screen.colorBinding) !== binding || colorBindingHash(frames.at(-1).screen.colorBinding) !== binding) {
        throw new Error(`${label} observation saw the palette or theme change.`);
    }
    // Only report reads carry a reply list; start, bind and finish never do.
    if ([frames[0], frames[1], frames.at(-1)].some(frame => frame.report !== undefined)) throw new Error(`Unexpected ${label} report frame.`);
}

/** The reply lists of one case's reads: each report read's journaled replies must equal the worker's record and its own barrier. */
function repliesOf(contract, recorded, readFrames) {
    return contract.plan.filter(step => step.read).map((step, index) => {
        const frame = readFrames[index], read = recorded.reads[index];
        if (step.read !== 'report') {
            if (frame.report !== undefined || read.report !== undefined) throw new Error(`Unexpected ${contract.caseId} report on a cells read.`);
            return [];
        }
        const journaled = validateXcsExchange(frame.report), worker = validateXcsExchange(read.report);
        if (JSON.stringify(journaled) !== JSON.stringify(worker) || journaled.barrier !== frame.barrier) throw new Error(`${label} ${contract.caseId} replies differ from their read frame.`);
        return journaled.replies;
    });
}
/** The journaled exchange of one read (already validated against the worker record and its barrier). */
const exchangeOf = (readFrames, index) => (readFrames[index].report === undefined ? null : validateXcsExchange(readFrames[index].report));

/** A difference between the pinned verdict and the effect verdict is a recorded divergence, never absorbed. */
function reconcile(pinnedOutcome, effect) {
    return pinnedOutcome === effect.outcome ? null : { pinnedOutcome, contractOutcome: effect.outcome, kind: `pinned-${pinnedOutcome}-effect-${effect.outcome}` };
}

/** Verify and replay one case's pinned results over its journaled exchange and read cursor; returns the verified pair. */
function verifyPinnedReplays(contract, recorded, readFrames, replies, { replayModule = null } = {}) {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    const exchange = exchangeOf(readFrames, contract.pinnedRead);
    if (exchange === null) throw new Error(`Pinned ${contract.caseId} exchange is missing.`);
    const cursor = [readFrames[contract.pinnedRead].screen.cursorRow, readFrames[contract.pinnedRead].screen.cursorCol];
    const position = { row: cursor[0] + 1, col: cursor[1] + 1 };
    requirePinnedAgreement(contract, termless, replayXcsPinned(contract.caseId, 'termless', { replies: replies[contract.pinnedRead].join(''), barrier: exchange.barrier }), label);
    requirePinnedAgreement(contract, terminal, replayXcsPinned(contract.caseId, 'terminal', { position }), label);
    if (replayModule) {
        const context = xcsRecordingContext(contract, 'termless', exchange, cursor);
        requireReplayAgreement(contract, replayModule.ntildeRunXcsTermless(contract.caseId, context), termless);
        if (context.callCount !== xcsExpectedCalls(contract, 'termless', exchange)) throw new Error(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
}

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveXcsCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, divergence, contractsVersion }) {
    const theme = readFrames[0].screen.colorBinding;
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedScript: contract.observed(observation).pinnedScript, terminalFixture: contract.terminalFixture,
        feeds: recorded.feeds, barriers: recorded.barriers,
        reads: recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, ...(read.kind === 'report' ? { replies: replies[index] } : {}) })),
        observed: contract.observed(observation),
        colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
            minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
        pinned: { termless: { pass: termless.pass, response: termless.response ?? null, note: termless.note ?? null, observation: termless.observation, assertions: termless.assertions ?? null },
            terminal: { pass: terminal.pass, response: terminal.response ?? null, note: terminal.note ?? null, observation: terminal.observation, assertions: terminal.assertions ?? null } },
        effectContract: { id: xcsEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-unchanged-state', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildXcsGradingReport(frames, run, runId, adapter, { contractsVersion = xcsContractsVersion, replayModule = null } = {}) {
    validateXcsEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of xcsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = xcsObservation(contract, readFrames.map(frame => frame.screen), replies, xcsNoncePrefix + runId);
        const effect = gradeXcsCase(contract, observation, contractsVersion);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, readFrames, replies, { replayModule });
        const divergence = reconcile(termless.observation.outcome, effect);
        cases.push(deriveXcsCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, divergence, contractsVersion }));
    }
    return { schemaVersion: 1, suiteId: xcsSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
