// Parent-side report builder for the App-observed underline-color resolution suite. Re-derives every
// conclusion from the retained parent journal and the independent contract grader and refuses tampering.
// There is no pinned probe to agree with: the suite is Ntilde's own and is never presented as a terminfo.dev
// grade. One theme binding is recorded per frame; across the run only the two contracted palette entries may
// differ, and only inside the contracted mutation reads. `appObserved` (the App's own value) and `parentExpected`
// (derived from stored state) are retained as separate fields of every cell.
import { validateGradedJournal, crossCheckGradedCapture, validateGradedCaptureRecord } from './graded-plan.mjs';
import { colorFrameCount } from './sgr-color-grading-core.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { underlineResolvedContracts, underlineResolvedCaseIds, underlineResolvedContractsVersion, underlineResolvedNoncePrefix,
    underlineResolvedSuiteId, gradeUnderlineResolvedCase, underlineResolvedObservation, validateUnderlineResolvedScreen } from './sgr-underline-color-resolved-contracts.mjs';

const label = 'underline-resolved';
const frameCount = colorFrameCount(underlineResolvedContracts);
/** Screens carry attributes, stored details (with overline), resolved colors and the opt-in resolved underline. */
export const underlineResolvedScreenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, false, true, true);
    validateUnderlineResolvedScreen(screen);
    return screen;
};
/** Everything in a binding except the palette and its override list. */
const themeKey = binding => JSON.stringify({ ...binding, ansiPaletteArgb: undefined, paletteOverrideIndices: undefined });

/** Validate journal, run identity, theme stability and restoration. */
export function validateUnderlineResolvedEvidence(frames, run, runId) {
    validateGradedJournal(frames, { frameCount, prefix: 'sgur', noncePrefix: underlineResolvedNoncePrefix, runId, label, screenOf: underlineResolvedScreenOf });
    if (run?.suiteId !== underlineResolvedSuiteId || run.contractsVersion !== underlineResolvedContractsVersion
        || JSON.stringify(run.caseIds) !== JSON.stringify([...underlineResolvedCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== underlineResolvedContracts.length) {
        throw new Error('underline-resolved run does not cover the contracted cases.');
    }
    const theme = themeKey(frames[0].screen.colorBinding);
    if (frames.some(frame => themeKey(frame.screen.colorBinding) !== theme)) {
        throw new Error('underline-resolved observation saw the theme change.');
    }
    if (colorBindingHash(frames[0].screen.colorBinding) !== colorBindingHash(frames.at(-1).screen.colorBinding)) {
        throw new Error('underline-resolved palette was not restored.');
    }
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildUnderlineResolvedReport(frames, run, runId, { contractsVersion = underlineResolvedContractsVersion } = {}) {
    validateUnderlineResolvedEvidence(frames, run, runId);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of underlineResolvedContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('underline-resolved case order differs from the contracts.');
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const screens = readFrames.map(frame => frame.screen);
        const observation = underlineResolvedObservation(contract, screens, frames[0].screen.colorBinding);
        const result = gradeUnderlineResolvedCase(contract, observation, contractsVersion);
        caseResults.push({ caseId: contract.caseId, kind: contract.kind, check: contract.check, criterion: contract.criterion,
            feeds: recorded.feeds, barriers: recorded.barriers, reads: recorded.reads.map(read => read.cursor),
            observed: contract.observed(observation),
            bindings: screens.map(screen => ({ themeName: screen.colorBinding.themeName, reverseVideo: screen.colorBinding.reverseVideo,
                paletteOverrideIndices: [...screen.colorBinding.paletteOverrideIndices], underlineStage: screen.colorBinding.underlineStage,
                entry4Argb: screen.colorBinding.ansiPaletteArgb[4], entry5Argb: screen.colorBinding.ansiPaletteArgb[5],
                bindingSha256: colorBindingHash(screen.colorBinding) })),
            contractResult: result,
            conclusion: { outcome: result.outcome, evidence: 'owned-stored-and-app-resolved-underline', grading: 'contract-v' + contractsVersion,
                layers: result.layers, ...(result.note ? { note: result.note } : {}), scope: contract.scope } });
    }
    return { schemaVersion: 1, suiteId: underlineResolvedSuiteId, runId, contractsVersion, kind: 'ntilde-extension-controls-not-terminfo-cases',
        scope: 'Ntilde extension controls for the opt-in App-reported resolved underline color (readScreen includeResolvedUnderlineColors), graded using '
            + 'parent-journaled stored underline style/palette index/literal bytes/default marker, the per-read recorded palette binding and the App-reported own '
            + 'resolved underline, against an independent expectation derived from stored state, the start binding of the run and contracted OSC 4 constants. '
            + 'Stage: theme resolution; minimum contrast, faint, foreground override, painting, compositing and rasterisation are excluded and rendered '
            + 'appearance is not claimed. No pinned terminfo.dev probe, no stock grade; the pinned underline-color qualifications keep their parent-derived evidence.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
