// Shared parent-side report machinery of the layered SGR color qualifications
// (palette, truecolor). Re-derives every conclusion from the retained parent
// journal and the independent contract grader, refuses any tampering, and requires
// the pinned probe's own grading to agree. The contract may never be more
// permissive than the pinned probe; any difference in verdict semantics is
// recorded with both verdicts. Each suite supplies a `spec` with its contracts,
// identities, pinned replay and scope text.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement,
    requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';

/** Total read groups across a suite's contracts, plus start, bind and finish frames. */
export const colorFrameCount = contracts => 3 + contracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Color screens carry attributes, stored details (with overline) and resolved colors together. */
export const colorScreenOf = validate => value => {
    const screen = screenSnapshot(value, true, true, false, true, false, true);
    validate(screen);
    return screen;
};

/** Validate journal, run identity, binding stability and restoration. */
export function validateColorEvidence(spec, frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount: colorFrameCount(spec.contracts), prefix: spec.prefix, noncePrefix: spec.noncePrefix,
        runId, label: spec.label, screenOf: spec.screenOf });
    if (!adapter || adapter.id !== spec.adapterId || run.adapterId !== spec.adapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== spec.derivedFrom) {
        throw new Error(`Invalid ${spec.label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...spec.caseIds])
        || !Array.isArray(run.cases) || run.cases.length !== spec.contracts.length) {
        throw new Error(`${spec.label} run does not cover the contracted cases.`);
    }
    // One palette/theme/render state for the whole suite: a palette or theme change between
    // reads would resolve cells against different values and voids the run.
    // A suite with a contracted palette mutation supplies its own check; every other suite must see one binding throughout.
    if (spec.bindingStable) {
        spec.bindingStable(frames);
        return;
    }
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) {
        throw new Error(`${spec.label} observation saw the palette or theme change.`);
    }
}

export const sameRgb = (a, b) => a.r === b.r && a.g === b.g && a.b === b.b;
/** The pinned sameCellColor: undefined-aware, null-aware color equality. */
export const sameCellColor = (a, b) => {
    if (a === undefined || b === undefined) return false;
    if (a === null || b === null) return a === b;
    return sameRgb(a, b);
};

/** The pinned parserStateResult shape of a null verdict. */
export function incomplete(response, note) {
    return { pass: false, response, note, assertions: [],
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}
/** The pinned parserStateResult shape of a decided verdict. */
export function decided(pass, contract, response) {
    return { pass, response, observation: { outcome: pass ? 'supported' : 'unsupported', evidence: 'parser-state' },
        assertions: [{ kind: pass ? 'positive' : 'negative', expected: contract.pinnedExpected, observed: response }] };
}

/** Compare the pinned and contract verdicts. The contract is the finer grader: it
 * may grade unsupported where the pinned probe is inconclusive, may be stricter
 * than a pinned supported, and may be more cautious than a pinned unsupported
 * (an invalid extra control); it must never grade supported where the pinned
 * probe did not. Every difference is recorded. */
export function reconcile(contract, pinnedOutcome, contractResult) {
    if (pinnedOutcome === contractResult.outcome) return null;
    if (contractResult.outcome === 'supported') {
        throw new Error(`Contract ${contract.caseId} grading is more permissive than the pinned probe.`);
    }
    const kind = pinnedOutcome === 'inconclusive' ? 'contract-measured-mismatch'
        : pinnedOutcome === 'supported' ? 'contract-stricter-than-pinned' : 'contract-more-cautious-than-pinned';
    if (kind === 'contract-measured-mismatch' && contractResult.outcome !== 'unsupported') {
        throw new Error(`Pinned and contract ${contract.caseId} grading disagree.`);
    }
    return { pinnedOutcome, contractOutcome: contractResult.outcome, kind };
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildColorReport(spec, frames, run, runId, adapter, { contractsVersion = spec.version, replayModule = null } = {}) {
    validateColorEvidence(spec, frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of spec.contracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${spec.label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, spec.label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, spec.label);
        const screens = readFrames.map(frame => frame.screen);
        const observation = spec.observation(contract, screens, frames[0].screen.colorBinding);
        const contractResult = spec.grade(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || !(spec.pinnedEvidence ?? ['parser-state']).includes(pinned.observation?.evidence)) {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        requirePinnedAgreement(contract, pinned, spec.replayPinned(contract, screens), spec.label);
        const divergence = (spec.reconcile ?? reconcile)(contract, pinned.observation.outcome, contractResult);
        if (replayModule) {
            const replay = replayModule[spec.replayFn](contract.caseId, spec.recordingContext(contract, screens));
            requireReplayAgreement(contract, replay, pinned);
        }
        const theme = screens[0].colorBinding;
        caseResults.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion,
            pinnedFeeds: contract.pinnedFeeds,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: contract.observed(observation),
            colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName,
                foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
                minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices],
                bindingSha256: colorBindingHash(theme) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null,
                observation: pinned.observation, assertions: pinned.assertions ?? null },
            contractResult, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: contractResult.outcome, evidence: spec.evidence ?? 'owned-stored-and-resolved-cells',
                grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(contractResult.layers ? { layers: contractResult.layers } : {}),
                ...(contractResult.note ? { note: contractResult.note } : {}),
                scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: spec.suiteId, runId, contractsVersion,
        adapter, scope: spec.scope, initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
