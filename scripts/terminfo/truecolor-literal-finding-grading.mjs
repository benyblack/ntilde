// Parent-side report builder for the truecolor-literal finding suite. Re-derives every
// conclusion from the retained parent journal and the independent contract grader and
// refuses tampering. There is no pinned probe to agree with: the finding is Ntilde's
// own and is never presented as a terminfo.dev grade. One palette/theme binding is
// recorded per frame; across the run only the aliased palette entry may differ, and
// only inside the contracted mutation reads.
import { validateGradedJournal, crossCheckGradedCapture, validateGradedCaptureRecord } from './graded-plan.mjs';
import { colorScreenOf, colorFrameCount } from './sgr-color-grading-core.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { truecolorFindingContracts, truecolorFindingCaseIds, truecolorFindingContractsVersion, truecolorFindingNoncePrefix,
    truecolorFindingSuiteId, gradeTruecolorFindingCase, truecolorFindingObservation, sgrColorCellAtScreenCheck } from './truecolor-literal-finding-contracts.mjs';

const label = 'truecolor-finding';
const frameCount = colorFrameCount(truecolorFindingContracts);
const screenOf = colorScreenOf(sgrColorCellAtScreenCheck);
/** Everything in a binding except the palette and its override list. */
const themeKey = binding => JSON.stringify({ ...binding, ansiPaletteArgb: undefined, paletteOverrideIndices: undefined });

/** Validate journal, run identity, theme stability and restoration. */
export function validateTruecolorFindingEvidence(frames, run, runId) {
    validateGradedJournal(frames, { frameCount, prefix: 'sgtf', noncePrefix: truecolorFindingNoncePrefix, runId, label, screenOf });
    if (run?.suiteId !== truecolorFindingSuiteId || run.contractsVersion !== truecolorFindingContractsVersion
        || JSON.stringify(run.caseIds) !== JSON.stringify([...truecolorFindingCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== truecolorFindingContracts.length) {
        throw new Error('truecolor-finding run does not cover the contracted cases.');
    }
    const theme = themeKey(frames[0].screen.colorBinding);
    if (frames.some(frame => themeKey(frame.screen.colorBinding) !== theme)) {
        throw new Error('truecolor-finding observation saw the theme change.');
    }
    // The run begins and ends on the same recorded palette (also implied by the restoration hash).
    if (colorBindingHash(frames[0].screen.colorBinding) !== colorBindingHash(frames.at(-1).screen.colorBinding)) {
        throw new Error('truecolor-finding palette was not restored.');
    }
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildTruecolorFindingReport(frames, run, runId, { contractsVersion = truecolorFindingContractsVersion } = {}) {
    validateTruecolorFindingEvidence(frames, run, runId);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of truecolorFindingContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('truecolor-finding case order differs from the contracts.');
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const screens = readFrames.map(frame => frame.screen);
        const observation = truecolorFindingObservation(contract, screens, frames[0].screen.colorBinding);
        const result = gradeTruecolorFindingCase(contract, observation, contractsVersion);
        caseResults.push({ caseId: contract.caseId, criterion: contract.criterion, channel: contract.channel,
            requested: contract.requested, aliasedIndex: contract.index, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: contract.observed(observation),
            bindings: screens.map(screen => ({ themeName: screen.colorBinding.themeName, reverseVideo: screen.colorBinding.reverseVideo,
                paletteOverrideIndices: [...screen.colorBinding.paletteOverrideIndices], entryArgb: screen.colorBinding.ansiPaletteArgb[contract.index],
                bindingSha256: colorBindingHash(screen.colorBinding) })),
            contractResult: result,
            conclusion: { outcome: result.outcome, evidence: 'owned-stored-and-resolved-cells', grading: 'contract-v' + contractsVersion,
                layers: result.layers, ...(result.note ? { note: result.note } : {}), scope: contract.scope } });
    }
    return { schemaVersion: 1, suiteId: truecolorFindingSuiteId, runId, contractsVersion, kind: 'ntilde-finding-not-a-terminfo-case',
        scope: 'Ntilde finding about five exact direct-RGB values (0,55,218; 58,150,221; 12,12,12; 204,204,204; 242,242,242) on foreground and '
            + 'background, graded from parent-journaled readScreen stored literal bytes, palette indices and default markers together with '
            + 'App-resolved RGB bound to the per-frame recorded palette, with an explicit indexed positive control, an OSC 4 mutation of the '
            + 'aliased entry and its restoration. Semicolon, colon-omitted-colorspace and colon-explicit-colorspace forms. No pinned terminfo.dev '
            + 'probe, no stock grade, rendered glyph appearance not claimed.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
