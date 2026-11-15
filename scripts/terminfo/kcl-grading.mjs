// Parent-side report builder for the kitty color-control qualification. Re-derives every conclusion from the retained parent journal (screens and the raw reply lists bound to their barriers) and the
// independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: each pinned result is a pure function of the journaled replies of its own exchange (the terminal
// path also sees the DA barrier that follows them). The pinned verdict is the replayed pinned code's own (supported when the foreground reply carries one valid value); the separately identified effect
// contract carries the graded conclusion and any difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateKclExchange } from './kcl-report.mjs';
import { kclContracts, kclCaseIds, kclContractsVersion, kclNoncePrefix, kclPrefix, kclSuiteId, kclEffectContractId, gradeKclCase, kclObservation,
    validateKclScreen, replayKclPinned } from './kcl-contracts.mjs';
import { kclAdapterId } from './kcl-grading-collector.mjs';
import { kclRecordingContext } from './kcl-collector.mjs';

const label = 'kitty-color';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const scope = 'Pinned terminfo.dev@4.0.0 criterion for extensions.osc21-kitty-color (OSC 21;foreground=? ST answered by an OSC 21 frame holding exactly one foreground field that is empty or rgb:h{1,4}/h{1,4}/h{1,4}; '
    + 'the probe can only return supported or inconclusive): both pinned paths replayed byte-exactly over the journaled raw replies of their own fenced exchange, beside the separately identified effect contract '
    + 'ntilde-kitty-color-control-effect-v1 graded by an independent oracle written from kitty\'s color-control text over the same fence: the pinned request, every modeled key (foreground, background, cursor, '
    + 'selection colors and palette entries 5 and 200) read back through OSC 21 and the legacy OSC 4/10/11/12/17/19 replies on one shared state, reply order and the ST terminator, the numeric encodings, '
    + 'bare-key resets and restoration of the host default, unknown keys with unpadded Base64; every exchange must leave the complete screen, cursor, rendition, margins, modes and active screen unchanged. '
    + 'Policy groups (the modeled key set, ignored empty/named/alpha values, the sequence caps, kitty\'s short #RGB reading) record Ntilde choices and never decide a verdict. Raw terminal state, not host '
    + 'presentation or painted pixels; no stock grades.';

/** Kitty color screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateKclScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + kclContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. */
export function validateKclEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: kclPrefix, noncePrefix: kclNoncePrefix, runId, label, screenOf });
    const identity = adapter && adapter.id === kclAdapterId && run.adapterId === kclAdapterId && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256)
        && adapter.derivedFromAdapterId === derivedFrom;
    if (!identity) throw new Error(`Invalid ${label} adapter identity.`);
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...kclCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== kclContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    // The case recolors the pane (that is what it measures), so its reads legitimately carry another color binding; the palette, theme and render state is proven the same before the first request,
    // after the bind and after the finish (every color reset back to the host's).
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if ([frames[1], frames.at(-1)].some(edge => colorBindingHash(edge.screen.colorBinding) !== binding)) throw new Error(`${label} observation saw the palette or theme change.`);
    // Only report reads carry a reply list; start, bind and finish never do.
    if ([frames[0], frames[1], frames.at(-1)].some(edge => edge.report !== undefined)) throw new Error(`Unexpected ${label} report frame.`);
}

/** The reply lists of one case's reads, in plan order: a cells read carries no report; a report read's journaled replies must equal the worker's record and its own barrier. */
function repliesOf(contract, recorded, readFrames) {
    return contract.plan.filter(step => step.read).map((step, index) => {
        const frame = readFrames[index], read = recorded.reads[index];
        if (step.read !== 'report') {
            if (frame.report !== undefined || read.report !== undefined) throw new Error(`Unexpected ${contract.caseId} report on a cells read.`);
            return [];
        }
        const journaled = validateKclExchange(frame.report), worker = validateKclExchange(read.report);
        if (JSON.stringify(journaled) !== JSON.stringify(worker) || journaled.barrier !== frame.barrier) throw new Error(`${label} ${contract.caseId} replies differ from their read frame.`);
        return journaled.replies;
    });
}

/** A difference between the pinned verdict and the effect verdict is a recorded divergence, never absorbed. */
function reconcile(pinnedOutcome, effect) {
    return pinnedOutcome === effect.outcome ? null : { pinnedOutcome, contractOutcome: effect.outcome, kind: `pinned-${pinnedOutcome}-effect-${effect.outcome}` };
}

/** Verify and replay one case's pinned results over its journaled exchange; returns the verified pair. */
function verifyPinnedReplays(contract, recorded, exchange, { replayModule = null } = {}) {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    const replies = exchange.replies.join('');
    requirePinnedAgreement(contract, termless, replayKclPinned(contract.caseId, 'termless', { replies }), label);
    requirePinnedAgreement(contract, terminal, replayKclPinned(contract.caseId, 'terminal', { replies, barrier: exchange.barrier }), label);
    if (replayModule) {
        const context = kclRecordingContext(contract, exchange);
        requireReplayAgreement(contract, replayModule.ntildeRunKclTermless(contract.caseId, context), termless);
        if (context.fedCount !== contract.pinnedFeeds.length || context.askedCount !== 0) throw new Error(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
}

const resultOf = pinned => ({ pass: pinned.pass, response: pinned.response ?? null, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null });

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveKclCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }) {
    const theme = readFrames[0].screen.colorBinding, divergence = reconcile(termless.observation.outcome, effect), observed = contract.observed(observation);
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedScript: observed.pinnedScript, terminalFixture: contract.terminalFixture,
        feeds: recorded.feeds, barriers: recorded.barriers,
        reads: recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, ...(read.kind === 'report' ? { replies: replies[index] } : {}) })),
        observed,
        colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
            minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
        pinned: { termless: resultOf(termless), terminal: resultOf(terminal) },
        effectContract: { id: kclEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-unchanged-state', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildKclGradingReport(frames, run, runId, adapter, { contractsVersion = kclContractsVersion, replayModule = null } = {}) {
    validateKclEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of kclContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = kclObservation(contract, readFrames.map(frame => frame.screen), replies, kclNoncePrefix + runId);
        const effect = gradeKclCase(contract, observation, contractsVersion);
        const exchange = validateKclExchange(readFrames[contract.pinnedRead].report);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, exchange, { replayModule });
        cases.push(deriveKclCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }));
    }
    return { schemaVersion: 1, suiteId: kclSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
