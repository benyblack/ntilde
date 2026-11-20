// Parent-side report builder for the xterm pointer-color qualification. Re-derives every conclusion from the retained parent journal (screens and the raw reply lists bound to their barriers) and the
// independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: the termless result is a pure function of the journaled replies of its own exchange, the
// terminal result of the journaled cursor of the same read. The pinned verdict is the replayed pinned code's own (inconclusive on both paths: no reply to the OSC 13 / OSC 14 query, and a consumed
// reset with an unmeasured effect); the separately identified effect contract carries the graded conclusion (unsupported: every pointer-color sequence ignored) and any difference is recorded as a
// divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { suiteReplyLists, reconcilePinnedWithEffect, suitePinnedResult } from './probe-suite-shared.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validatePclExchange } from './pcl-report.mjs';
import { pclContracts, pclCaseIds, pclContractsVersion, pclNoncePrefix, pclPrefix, pclSuiteId, pclEffectContractId, gradePclCase, pclObservation,
    validatePclScreen, replayPclTermless, replayPclTerminal } from './pcl-contracts.mjs';
import { pclAdapterId } from './pcl-grading-collector.mjs';
import { pclRecordingContext } from './pcl-collector.mjs';

const label = 'pointer-color';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const scope = 'Pinned terminfo.dev@4.0.0 criteria for extensions.osc113-reset-pointer-fg and extensions.osc114-reset-pointer-bg (colorResetProbe(13, 113) / (14, 114): the termless path queries OSC 13 / OSC 14 and is '
    + 'inconclusive (no-response) without a complete rgb reply; the terminal path writes the reset, asks the cursor position and returns unverifiedEffect, inconclusive by construction): both pinned paths replayed '
    + 'byte-exactly over the journaled raw reply and cursor of their own fenced exchange, beside the separately identified effect contract ntilde-pointer-color-unsupported-effect-v1 graded by independent '
    + 'literals written from the seeded colors and the xterm chain order: every OSC 13 / OSC 14 query (BEL and ST), set (rgb: and #), OSC 113 / OSC 114 reset (bare, ST and with an argument) and a chained OSC 12 set '
    + 'through the pointer slots is ignored and never answered, leaves the complete screen, cursor, rendition, margins, modes and active screen unchanged, and leaves the five colors the pointer sequences could '
    + 'be mistaken for (foreground, background, cursor, selection background and selection foreground) reading back exactly as seeded, with the chain consuming the pointer slots so the highlight background '
    + 'did not shift and a cursor-color change read back as a control. Raw terminal replies and state, not the operating system pointer image, the OSC 22 shape or painted pixels; no stock grades.';

/** Feature-reporting screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validatePclScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + pclContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. The case changes no color, so every frame must carry the same binding, and only report reads carry a reply list; start, bind and
 *  finish never do. */
export function validatePclEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: pclPrefix, noncePrefix: pclNoncePrefix, runId, label, screenOf });
    const adapterIdentityOk = adapter !== null && adapter !== undefined && adapter.id === pclAdapterId && run.adapterId === pclAdapterId
        && isDigest(adapter.bundleSha256) && isDigest(adapter.upstreamBundleSha256) && adapter.derivedFromAdapterId === derivedFrom;
    if (!adapterIdentityOk) throw new Error(`Invalid ${label} adapter identity.`);
    const coversCases = JSON.stringify(run.probeIds) === JSON.stringify([...pclCaseIds]) && Array.isArray(run.cases) && run.cases.length === pclContracts.length;
    if (!coversCases) throw new Error(`${label} run does not cover the contracted cases.`);
    const edgeBinding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== edgeBinding)) throw new Error(`${label} observation saw the palette or theme change.`);
    for (const edge of [frames[0], frames[1], frames.at(-1)]) {
        if (edge.report !== undefined) throw new Error(`Unexpected ${label} report frame.`);
    }
}

const repliesOf = (contract, recorded, readFrames) =>
    suiteReplyLists({ label, contract, recorded, readFrames, validateExchange: validatePclExchange });
const reconcile = reconcilePinnedWithEffect;
const verifyPinnedReplays = (contract, recorded, exchange, { replayModule = null } = {}) => {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || terminal?.probeId !== contract.caseId || !termless.observation || !terminal.observation) throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    const cursor = recorded.reads[contract.pinnedRead].cursor;
    requirePinnedAgreement(contract, termless, replayPclTermless(contract.caseId, exchange.replies.join('')), label);
    requirePinnedAgreement(contract, terminal, replayPclTerminal(contract.caseId, cursor), label);
    if (replayModule) {
        const context = pclRecordingContext(contract, exchange);
        requireReplayAgreement(contract, replayModule.ntildeRunPclTermless(contract.caseId, context), termless);
        if (context.fedCount !== contract.pinnedFeeds.length || context.askedCount !== 0) throw new Error(`Pinned ${contract.caseId} replay made a different number of calls.`);
    }
    return { termless, terminal };
};
const resultOf = suitePinnedResult;

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function derivePclCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }) {
    const theme = readFrames[0].screen.colorBinding, observed = contract.observed(observation);
    const divergence = reconcile(termless.observation.outcome, effect);
    const reads = recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, ...(read.kind === 'report' ? { replies: replies[index] } : {}) }));
    const binding = { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
        minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) };
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedScript: observed.pinnedScript, terminalFixture: contract.terminalFixture,
        feeds: recorded.feeds, barriers: recorded.barriers,
        reads,
        observed,
        colorBinding: binding,
        pinned: { termless: resultOf(termless), terminal: resultOf(terminal) },
        effectContract: { id: pclEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-unchanged-state', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildPclGradingReport(frames, run, runId, adapter, { contractsVersion = pclContractsVersion, replayModule = null } = {}) {
    validatePclEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of pclContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = pclObservation(contract, readFrames.map(frame => frame.screen), replies, pclNoncePrefix + runId);
        const effect = gradePclCase(contract, observation, contractsVersion);
        const exchange = validatePclExchange(readFrames[contract.pinnedRead].report);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, exchange, { replayModule });
        cases.push(derivePclCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, contractsVersion }));
    }
    return { schemaVersion: 1, suiteId: pclSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
