// Parent-side report builder for the SGR truecolor qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed screen
// of its own fixture. The contract may never be more permissive than the pinned
// probe; any difference in verdict semantics is recorded with both verdicts.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement,
    requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { sgrTruecolorContracts, sgrTruecolorCaseIds, sgrTruecolorContractsVersion, sgrTruecolorNoncePrefix, sgrTruecolorSuiteId,
    gradeSgrTruecolorCase, sgrTruecolorCellAt, sgrTruecolorObservation, validateSgrTruecolorScreen,
    pinnedTruecolorNotExposedNote, pinnedTruecolorReadbackNote, pinnedTruecolorMimicNote,
    pinnedTruecolorCalibrationNote } from './sgr-truecolor-contracts.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { sgrTruecolorAdapterId } from './sgr-truecolor-grading-collector.mjs';
import { sgrTruecolorRecordingContext } from './sgr-truecolor-collector.mjs';

/** Total read groups across the suite's contracts. */
const readCount = sgrTruecolorContracts.reduce((sum, contract) => sum + planReads(contract), 0);
/** Start, bind, every read group, finish. */
const frameCount = 3 + readCount;

/** Palette screens carry attributes, stored details (with overline) and resolved colors together. */
const paletteScreen = value => {
    const screen = screenSnapshot(value, true, true, false, true, false, true);
    validateSgrTruecolorScreen(screen);
    return screen;
};

/** Validate journal, run identity, binding stability and restoration. */
export function validateSgrTruecolorEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount, prefix: 'sgtc', noncePrefix: sgrTruecolorNoncePrefix,
        runId, label: 'SGR-truecolor', screenOf: paletteScreen });
    if (!adapter || adapter.id !== sgrTruecolorAdapterId || run.adapterId !== sgrTruecolorAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== 'ntilde-owned-sgr-palette-grading-v1') {
        throw new Error('Invalid SGR-truecolor adapter identity.');
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...sgrTruecolorCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== sgrTruecolorContracts.length) {
        throw new Error('SGR-truecolor run does not cover the contracted cases.');
    }
    // One palette/theme/render state for the whole suite: a palette or theme change between
    // reads would resolve cells against different values and voids the run.
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) {
        throw new Error('SGR-truecolor observation saw the palette or theme change.');
    }
}

const sameRgb = (a, b) => a.r === b.r && a.g === b.g && a.b === b.b;
/** The pinned sameCellColor: undefined-aware, null-aware color equality. */
const sameCellColor = (a, b) => {
    if (a === undefined || b === undefined) return false;
    if (a === null || b === null) return a === b;
    return sameRgb(a, b);
};

/** The pinned parserStateResult shape of a null verdict. */
function incomplete(response, note) {
    return { pass: false, response, note, assertions: [],
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}
/** The pinned parserStateResult shape of a decided verdict. */
function decided(pass, contract, response) {
    return { pass, response, observation: { outcome: pass ? 'supported' : 'unsupported', evidence: 'parser-state' },
        assertions: [{ kind: pass ? 'positive' : 'negative', expected: contract.pinnedExpected, observed: response }] };
}

/** Re-derive the pinned truecolorProbe termless result over its five cells. */
function replayTruecolor(contract, cells) {
    const [baseline, controlFirst, controlSecond, targetFirst, targetSecond] = cells;
    const response = JSON.stringify({ baseline, controlFirst, controlSecond, targetFirst, targetSecond });
    if (baseline.char !== 'C' || controlFirst.char !== 'A' || controlSecond.char !== 'B'
        || targetFirst.char !== 'X' || targetSecond.char !== 'Y') return incomplete(response, pinnedTruecolorNotExposedNote);
    const x = targetFirst[contract.channel], y = targetSecond[contract.channel];
    if (!x || !y) return incomplete(response, pinnedTruecolorReadbackNote);
    const preceding = controlSecond[contract.channel];
    if (!preceding || sameRgb(preceding, contract.first)) return incomplete(response, pinnedTruecolorMimicNote);
    if (sameRgb(x, contract.first) && sameRgb(y, contract.second)) return decided(true, contract, response);
    const a = controlFirst[contract.channel], b = controlSecond[contract.channel];
    if (a && b && !sameRgb(a, b)) return decided(false, contract, response);
    return incomplete(response, pinnedTruecolorCalibrationNote);
}

const replays = Object.freeze({ truecolor: replayTruecolor });

/** Re-derive the complete pinned termless result from the observed screen of the
 * pinned fixture, mirroring the pinned code's shapes exactly. */
export function replaySgrTruecolorPinned(contract, screens) {
    const replay = replays[contract.pinnedKind];
    if (!replay) throw new Error(`Unknown pinned kind for ${contract.caseId}.`);
    const cells = Array.from({ length: contract.fixtureCells }, (_, col) => sgrTruecolorCellAt(screens[contract.pinnedRead], 0, col));
    return replay(contract, cells);
}

/** Compare the pinned and contract verdicts. The contract is the finer grader: it
 * may grade unsupported where the pinned probe is inconclusive, may be stricter
 * than a pinned supported, and may be more cautious than a pinned unsupported
 * (an invalid extra control); it must never grade supported where the pinned
 * probe did not. Every difference is recorded. */
function reconcile(contract, pinnedOutcome, contractResult) {
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
export function buildSgrTruecolorGradingReport(frames, run, runId, adapter, { contractsVersion = sgrTruecolorContractsVersion, replayModule = null } = {}) {
    validateSgrTruecolorEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of sgrTruecolorContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('SGR-truecolor case order differs from the contracts.');
        validateGradedCaptureRecord(contract, recorded, 'SGR-truecolor');
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, 'SGR-truecolor');
        const screens = readFrames.map(frame => frame.screen);
        const observation = sgrTruecolorObservation(contract, screens);
        const contractResult = gradeSgrTruecolorCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        requirePinnedAgreement(contract, pinned, replaySgrTruecolorPinned(contract, screens), 'SGR-truecolor');
        const divergence = reconcile(contract, pinned.observation.outcome, contractResult);
        if (replayModule) {
            const replay = replayModule.ntildeRunSgrTruecolorTermless(contract.caseId, sgrTruecolorRecordingContext(contract, screens));
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
            conclusion: { outcome: contractResult.outcome, evidence: 'owned-stored-and-resolved-cells',
                grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(contractResult.layers ? { layers: contractResult.layers } : {}),
                ...(contractResult.note ? { note: contractResult.note } : {}),
                scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: sgrTruecolorSuiteId, runId, contractsVersion,
        adapter, scope: 'Pinned terminfo.dev@4.0.0 truecolor foreground and background termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored literal bytes (palette index and default marker cleared) together with '
            + 'App-resolved RGB bound to one recorded palette/theme state, with controls for the unaffected channel, unrelated '
            + 'attributes, DECSCNM, literal-versus-palette storage with equal appearance, boundary components and reset restoration. Only the semicolon 38/48;2;r;g;b form is qualified. '
            + 'Rendered glyph appearance is not claimed; no stock grades.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
