// Parent-side report builder for the XTGETTCAP qualification. Re-derives every conclusion from the retained parent journal
// (screens and the raw reply lists bound to their barriers) and the independent contract grader, refuses any tampering, and
// requires both pinned paths' own grading to agree: each pinned result is a pure function of the journaled replies of its own
// exchange. The pinned verdict is inconclusive by design (TN is deliberately refused); the separately identified effect contract
// carries the graded capability-report conclusion and a difference is recorded as a divergence, never absorbed.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement, validateGradedCaptureRecord,
    planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { validateXtgettcapExchange } from './xtgettcap-report.mjs';
import { xtgettcapContracts, xtgettcapCaseIds, xtgettcapContractsVersion, xtgettcapNoncePrefix, xtgettcapSuiteId,
    xtgettcapEffectContractId, gradeXtgettcapCase, xtgettcapObservation, validateXtgettcapScreen, replayXtgettcapPinned } from './xtgettcap-contracts.mjs';
import { xtgettcapAdapterId } from './xtgettcap-grading-collector.mjs';
import { xtgettcapRecordingContext } from './xtgettcap-collector.mjs';

const label = 'XTGETTCAP';
const derivedFrom = 'ntilde-owned-title-stack-grading-v1';
const scope = 'Pinned terminfo.dev@4.0.0 XTGETTCAP criterion (device.xtgettcap: DCS + q 544e ST answers DCS 1 + r 544e = <even-length hex> '
    + 'ST for TN; the deliberate DCS 0 + r ST refusal is inconclusive and preserved, never upgraded — TN\'s honest value is the session TERM, '
    + 'which VT does not know) replayed byte-exactly on a disposable App, both pinned paths over the journaled raw replies of its own '
    + 'fenced exchange, beside the separately identified effect contract ntilde-xtgettcap-capability-report-effect-v1 graded by an '
    + 'independent reply oracle over the same fence: the exact pinned request, the numeric Co/colors replies, the RGB/Tc flag replies, '
    + 'one string reply, multi-name reply order with per-name outcomes, the documented unavailable and unknown names, case-sensitivity, '
    + 'the uppercase echo, malformed lists (odd-length, non-hex, empty, mixed) with exactly one refusal, and verified recovery after '
    + 'each malformed request; every exchange must leave the complete screen, cursor, rendition, margins, modes and active screen '
    + 'unchanged. Capability-report replies only: an advertised flag or count proves no rendering, color accuracy or control-string '
    + 'execution; no Ms clipboard, X resource, remote-delivery or other-platform claim; no stock grades.';

/** XTGETTCAP screens carry attributes, stored details (with overline and protection), resolved colors and the App's stored buffer state. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true, false, true);
    validateXtgettcapScreen(screen);
    return screen;
};

/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = () => 3 + xtgettcapContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Validate journal, run identity, binding stability and restoration. */
export function validateXtgettcapEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: frameCount(), prefix: 'xgtc', noncePrefix: xtgettcapNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== xtgettcapAdapterId || run.adapterId !== xtgettcapAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== derivedFrom) {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...xtgettcapCaseIds]) || !Array.isArray(run.cases) || run.cases.length !== xtgettcapContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
    // One palette/theme/render state for the whole suite.
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) throw new Error(`${label} observation saw the palette or theme change.`);
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
        const journaled = validateXtgettcapExchange(frame.report), worker = validateXtgettcapExchange(read.report);
        if (JSON.stringify(journaled) !== JSON.stringify(worker) || journaled.barrier !== frame.barrier) {
            throw new Error(`${label} ${contract.caseId} replies differ from their read frame.`);
        }
        return journaled.replies;
    });
}
/** The journaled exchange of one read (already validated against the worker record and its barrier). */
const exchangeOf = (readFrames, index) => (readFrames[index].report === undefined ? null : validateXtgettcapExchange(readFrames[index].report));

/** The pinned verdict is inconclusive by design (the refused TN name): any other pinned outcome is an error; a decided effect
 *  verdict is a recorded difference, never absorbed. */
function reconcile(contract, pinnedOutcome, effect) {
    if (pinnedOutcome !== 'inconclusive') throw new Error(`Pinned ${contract.caseId} must be inconclusive by design.`);
    return effect.outcome === 'inconclusive' ? null : { pinnedOutcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' };
}

/** Verify and replay one case's pinned results over its journaled replies; returns the verified pair and its replay data. */
function verifyPinnedReplays(contract, recorded, readFrames, replies, { replayModule = null } = {}) {
    const { termless, terminal } = recorded.pinned ?? {};
    if (termless?.probeId !== contract.caseId || termless.observation?.evidence !== 'query'
        || terminal?.probeId !== contract.caseId || terminal.observation?.evidence !== 'query') {
        throw new Error(`Missing pinned ${contract.caseId} grading results.`);
    }
    requirePinnedAgreement(contract, termless, replayXtgettcapPinned(replies[contract.pinnedRead].join('')), label);
    const pinnedExchange = exchangeOf(readFrames, contract.pinnedRead);
    requirePinnedAgreement(contract, terminal,
        replayXtgettcapPinned(pinnedExchange === null ? '' : pinnedExchange.replies.join('') + pinnedExchange.barrier), label);
    if (replayModule) {
        const context = xtgettcapRecordingContext(contract, { barrier: '', replies: replies[contract.pinnedRead] });
        requireReplayAgreement(contract, replayModule.ntildeRunXtgettcapTermless(contract.caseId, context), termless);
        if (context.fedCount !== contract.pinnedFeeds.length) throw new Error(`Pinned ${contract.caseId} replay fed a different number of pinned writes.`);
    }
    return { termless, terminal };
}

/** Derive one case's published record from its journaled frames, worker record and the graded effect. */
function deriveXtgettcapCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, divergence, contractsVersion }) {
    const theme = readFrames[0].screen.colorBinding;
    return { caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds,
        terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
        reads: recorded.reads.map((read, index) => ({ kind: read.kind, cursor: read.cursor, ...(read.kind === 'report' ? { replies: replies[index] } : {}) })),
        observed: contract.observed(observation),
        colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
            minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
        pinned: { termless: { pass: termless.pass, response: termless.response, note: termless.note ?? null, observation: termless.observation, assertions: termless.assertions ?? null },
            terminal: { pass: terminal.pass, response: terminal.response, note: terminal.note ?? null, observation: terminal.observation, assertions: terminal.assertions ?? null } },
        effectContract: { id: xtgettcapEffectContractId, version: contractsVersion, outcome: effect.outcome, ...(effect.note ? { note: effect.note } : {}),
            attribution: 'separately identified Ntilde effect contract; not the pinned probe' },
        contractResult: effect, ...(divergence ? { divergence } : {}),
        conclusion: { outcome: termless.observation.outcome, evidence: 'owned-raw-replies-and-unchanged-state', grading: 'pinned-termless-preserved-by-design',
            note: termless.observation.note ?? null, scope: contract.appearanceScope } };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildXtgettcapGradingReport(frames, run, runId, adapter, { contractsVersion = xtgettcapContractsVersion, replayModule = null } = {}) {
    validateXtgettcapEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const cases = [];
    for (const [position, contract] of xtgettcapContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const replies = repliesOf(contract, recorded, readFrames);
        const observation = xtgettcapObservation(contract, readFrames.map(frame => frame.screen), replies, xtgettcapNoncePrefix + runId);
        const effect = gradeXtgettcapCase(contract, observation, contractsVersion);
        const { termless, terminal } = verifyPinnedReplays(contract, recorded, readFrames, replies, { replayModule });
        const divergence = reconcile(contract, termless.observation.outcome, effect);
        cases.push(deriveXtgettcapCase(contract, recorded, readFrames, { replies, observation, effect, termless, terminal, divergence, contractsVersion }));
    }
    return { schemaVersion: 1, suiteId: xtgettcapSuiteId, runId, contractsVersion, adapter, scope,
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases, snapshots: frames };
}
