// Parent-side report builder for the device-query qualification. Re-derives every conclusion from the retained parent journal (screens and the raw reply
// lists bound to their barriers) and the independent contract grader, refuses any tampering, and requires both pinned paths' own grading to agree: each pinned
// result is a pure function of the journaled replies of its own exchange. The pinned verdict is the replayed pinned code's own (supported for XTGETXRES, CSI 20 t
// and CSI 21 t; inconclusive for XTREPORTCOLORS, whose pinned pattern has no ? marker and cannot match xterm's reply); the separately identified effect contract
// carries the graded conclusion and any difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateDevqExchange } from './devq-report.mjs';
import { devqContracts, devqCaseIds, devqContractsVersion, devqNoncePrefix, devqPrefix, devqSuiteId, devqEffectContractId, gradeDevqCase, devqObservation,
    validateDevqScreen, replayDevqPinned } from './devq-contracts.mjs';
import { devqAdapterId } from './devq-grading-collector.mjs';
import { devqRecordingContext } from './devq-collector.mjs';

const label = 'device-query';
const derivedFrom = 'ntilde-owned-left-right-margins-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 device-query criteria (device.xtgetxres: DCS + Q 7465726d4e616d65 ST answers DCS 1 + R <name> = <hex> ST; device.xtreportcolors: CSI # R answers '
    + 'CSI <digits;> # Q with no ? marker, which xterm\'s own CSI ? depth ; deepest # Q cannot match; device.xtwinops-20 and device.xtwinops-21: after OSC 1;test-icon / OSC 2;test-title '
    + 'the exact label as OSC L / OSC l) replayed byte-exactly on a disposable App whose initial settings turn on AllowTitleReport (off by default), both pinned paths over the journaled raw '
    + 'replies of their own fenced exchange, beside the separately identified effect contract ntilde-device-query-report-effect-v1 graded by an independent oracle written from xterm\'s '
    + 'control-sequence text and source over the same fence: XTGETXRES termName (the documented session TERM as uppercase hex, echoed spelling, case-sensitive unknown names, multi-name '
    + 'shape and stop rule, unanswered malformed requests with recovery), the xterm palette stack (depth and deepest reports, pushes and pops of the background and one palette entry '
    + 'read back through OSC 11 and OSC 4 replies, numbered slots, empty and out-of-range operations, the ten-slot bound, independence from the kitty stack) and the title and icon label '
    + 'reports (distinct state, OSC 0, empty and semicolon labels, title-stack interplay, stability); every exchange must leave the complete screen, cursor, rendition, margins, modes and '
    + 'active screen unchanged. Policy groups record Ntilde choices xterm does not make and never decide a verdict. device.term-features is documented only (no request, no case). Raw '
    + 'terminal state, not host presentation; the default-off title gate is unit-tested, not measured; termName is the documented session TERM, not read from the child; no stock grades.';

/** Device-query screens carry attributes, stored details (with overline and protection), the stored cursor mode, resolved colors, the App's stored buffer state and the DECLRMM margins. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, true, true, true, true, false, true, true);
    validateDevqScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + devqContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** The case whose controls recolor the pane: its reads legitimately carry another colour binding, restored by the finish. */
const recoloringCase = 'device.xtreportcolors';

/** Validate journal, run identity, binding stability and restoration. */
export function validateDevqEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: devqPrefix, noncePrefix: devqNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== devqAdapterId || run.adapterId !== devqAdapterId || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '') || adapter.derivedFromAdapterId !== derivedFrom) {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...devqCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== devqContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    // One palette/theme/render state for the whole suite except inside the recoloring case, whose own restoration the finish frame proves.
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    let index = 2;
    for (const contract of devqContracts) {
        const reads = planReads(contract);
        for (let offset = 0; offset < reads; offset++) {
            if (contract.caseId !== recoloringCase && colorBindingHash(frames[index + offset].screen.colorBinding) !== binding) throw new Error(`${label} observation saw the palette or theme change.`);
        }
        index += reads;
    }
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
        const journaled = validateDevqExchange(frame.report), worker = validateDevqExchange(read.report);
        if (JSON.stringify(journaled) !== JSON.stringify(worker) || journaled.barrier !== frame.barrier) throw new Error(`${label} ${contract.caseId} replies differ from their read frame.`);
        return journaled.replies;
    });
}
/** The journaled exchange of one read (already validated against the worker record and its barrier). */
const exchangeOf = (readFrames, index) => (readFrames[index].report === undefined ? null : validateDevqExchange(readFrames[index].report));

/** A difference between the pinned verdict and the effect verdict is a recorded divergence, never absorbed. */
function reconcile(pinnedOutcome, effect) {
    return pinnedOutcome === effect.outcome ? null : { pinnedOutcome, contractOutcome: effect.outcome, kind: `pinned-${pinnedOutcome}-effect-${effect.outcome}` };
}

/** Verify and replay one case's pinned results over its journaled replies; returns the verified pair. */
function verifyPinnedReplays(contract, recorded, readFrames, replies, { replayModule = null } = {}) {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || termless.observation?.evidence !== 'query' || terminal?.probeId !== contract.caseId || terminal.observation?.evidence !== 'query') {
        throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    }
    requirePinnedAgreement(contract, termless, replayDevqPinned(contract.caseId, replies[contract.pinnedRead].join('')), label);
    const pinnedExchange = exchangeOf(readFrames, contract.pinnedRead);
    requirePinnedAgreement(contract, terminal, replayDevqPinned(contract.caseId, pinnedExchange === null ? '' : pinnedExchange.replies.join('') + pinnedExchange.barrier), label);
    if (replayModule) {
        const context = devqRecordingContext(contract, { barrier: '', replies: replies[contract.pinnedRead] });
        requireReplayAgreement(contract, replayModule.ntildeRunDevqTermless(contract.caseId, context), termless);
        if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} replay fed a different number of pinned writes.`);
    }
    return { termless, terminal };
}

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveDevqCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, divergence, contractsVersion }) {
    const theme = readFrames[0].screen.colorBinding;
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds, terminalFixture: contract.terminalFixture,
        feeds: recorded.feeds, barriers: recorded.barriers,
        reads: recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, ...(read.kind === 'report' ? { replies: replies[index] } : {}) })),
        observed: contract.observed(observation),
        colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
            minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
        pinned: { termless: { pass: termless.pass, response: termless.response, note: termless.note ?? null, observation: termless.observation, assertions: termless.assertions ?? null },
            terminal: { pass: terminal.pass, response: terminal.response, note: terminal.note ?? null, observation: terminal.observation, assertions: terminal.assertions ?? null } },
        effectContract: { id: devqEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-unchanged-state', grading: 'pinned-termless-replayed', note: termless.observation.note ?? null,
            reason: termless.observation.reason ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildDevqGradingReport(frames, run, runId, adapter, { contractsVersion = devqContractsVersion, replayModule = null } = {}) {
    validateDevqEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of devqContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = devqObservation(contract, readFrames.map(frame => frame.screen), replies, devqNoncePrefix + runId);
        const effect = gradeDevqCase(contract, observation, contractsVersion);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, readFrames, replies, { replayModule });
        const divergence = reconcile(termless.observation.outcome, effect);
        cases.push(deriveDevqCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, divergence, contractsVersion }));
    }
    return { schemaVersion: 1, suiteId: devqSuiteId, runId, contractsVersion, adapter, scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
