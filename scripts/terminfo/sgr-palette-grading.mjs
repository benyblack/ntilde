// Parent-side report builder for the SGR palette-color qualification. Re-derives
// every conclusion from the retained parent journal and the independent
// contract grader, refuses any tampering, and requires the pinned probe's own
// grading to agree: the pinned result is a pure function of the observed screen
// of its own fixture. The contract may never be more permissive than the pinned
// probe; any difference in verdict semantics is recorded with both verdicts.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement,
    requireReplayAgreement, validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { sgrPaletteContracts, sgrPaletteCaseIds, sgrPaletteContractsVersion, sgrPaletteNoncePrefix, sgrPaletteSuiteId,
    gradeSgrPaletteCase, sgrPaletteCellAt, sgrPaletteObservation, validateSgrPaletteScreen,
    pinnedNamedNotExposedNote, pinnedNamedIndistinguishableNote, pinnedDefaultNotExposedNote, pinnedDefaultSetupNote,
    pinnedIndexedNotExposedNote, pinnedIndexedUncalibratedNote, cubeReferences } from './sgr-palette-contracts.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { sgrPaletteAdapterId } from './sgr-palette-grading-collector.mjs';
import { sgrPaletteRecordingContext } from './sgr-palette-collector.mjs';

/** Total read groups across the suite's contracts. */
const readCount = sgrPaletteContracts.reduce((sum, contract) => sum + planReads(contract), 0);
/** Start, bind, every read group, finish. */
const frameCount = 3 + readCount;

/** Palette screens carry attributes, stored details (with overline) and resolved colors together. */
const paletteScreen = value => {
    const screen = screenSnapshot(value, true, true, false, true, false, true);
    validateSgrPaletteScreen(screen);
    return screen;
};

/** Validate journal, run identity, binding stability and restoration. */
export function validateSgrPaletteEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount, prefix: 'sgpal', noncePrefix: sgrPaletteNoncePrefix,
        runId, label: 'SGR-palette', screenOf: paletteScreen });
    if (!adapter || adapter.id !== sgrPaletteAdapterId || run.adapterId !== sgrPaletteAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== 'ntilde-owned-sgr-attributes-grading-v1') {
        throw new Error('Invalid SGR-palette adapter identity.');
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...sgrPaletteCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== sgrPaletteContracts.length) {
        throw new Error('SGR-palette run does not cover the contracted cases.');
    }
    // One palette/theme/render state for the whole suite: a palette or theme change between
    // reads would resolve cells against different values and voids the run.
    const binding = colorBindingHash(frames[0].screen.colorBinding);
    if (frames.some(frame => colorBindingHash(frame.screen.colorBinding) !== binding)) {
        throw new Error('SGR-palette observation saw the palette or theme change.');
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

/** Re-derive the pinned namedColorProbe termless result over its three cells. */
function replayNamed(contract, cells) {
    const [baseline, first, second] = cells;
    const response = JSON.stringify({ baseline, first, second });
    if (baseline.char !== 'C' || first.char !== 'X' || second.char !== 'Y') return incomplete(response, pinnedNamedNotExposedNote);
    const neutral = baseline[contract.channel], a = first[contract.channel], b = second[contract.channel];
    if (neutral !== undefined && a && b && !sameCellColor(neutral, a) && !sameRgb(a, b)) return decided(true, contract, response);
    return incomplete(response, pinnedNamedIndistinguishableNote);
}

/** Re-derive the pinned defaultColorProbe termless result over its three cells. */
function replayDefault(contract, cells) {
    const [baseline, before, after] = cells;
    const response = JSON.stringify({ baseline, before, after });
    if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'R') return incomplete(response, pinnedDefaultNotExposedNote);
    const baseColor = baseline[contract.channel], changed = before[contract.channel], resetColor = after[contract.channel];
    if (!changed || sameCellColor(baseColor, changed) || resetColor === undefined) return incomplete(response, pinnedDefaultSetupNote);
    return decided(sameCellColor(baseColor, resetColor), contract, response);
}

/** Re-derive the pinned indexedColorProbe termless result over its five cells. */
function replayIndexed(contract, cells) {
    const [baseline, directFirst, directSecond, indexedFirst, indexedSecond] = cells;
    const response = JSON.stringify({ baseline, directFirst, directSecond, indexedFirst, indexedSecond });
    if (baseline.char !== 'C' || directFirst.char !== 'A' || directSecond.char !== 'B'
        || indexedFirst.char !== 'X' || indexedSecond.char !== 'Y') return incomplete(response, pinnedIndexedNotExposedNote);
    const [first, second] = cubeReferences;
    const a = directFirst[contract.channel], b = directSecond[contract.channel];
    const x = indexedFirst[contract.channel], y = indexedSecond[contract.channel];
    if (a && b && x && y && sameRgb(a, first.rgb) && sameRgb(b, second.rgb) && sameRgb(x, a) && sameRgb(y, b)) {
        return decided(true, contract, response);
    }
    return incomplete(response, pinnedIndexedUncalibratedNote);
}

const replays = Object.freeze({ named: replayNamed, default: replayDefault, indexed: replayIndexed });

/** Re-derive the complete pinned termless result from the observed screen of the
 * pinned fixture, mirroring the pinned code's shapes exactly. */
export function replaySgrPalettePinned(contract, screens) {
    const replay = replays[contract.pinnedKind];
    if (!replay) throw new Error(`Unknown pinned kind for ${contract.caseId}.`);
    const cells = Array.from({ length: contract.fixtureCells }, (_, col) => sgrPaletteCellAt(screens[contract.pinnedRead], 0, col));
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
export function buildSgrPaletteGradingReport(frames, run, runId, adapter, { contractsVersion = sgrPaletteContractsVersion, replayModule = null } = {}) {
    validateSgrPaletteEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of sgrPaletteContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('SGR-palette case order differs from the contracts.');
        validateGradedCaptureRecord(contract, recorded, 'SGR-palette');
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, 'SGR-palette');
        const screens = readFrames.map(frame => frame.screen);
        const observation = sgrPaletteObservation(contract, screens);
        const contractResult = gradeSgrPaletteCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        requirePinnedAgreement(contract, pinned, replaySgrPalettePinned(contract, screens), 'SGR-palette');
        const divergence = reconcile(contract, pinned.observation.outcome, contractResult);
        if (replayModule) {
            const replay = replayModule.ntildeRunSgrPaletteTermless(contract.caseId, sgrPaletteRecordingContext(contract, screens));
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
    return { schemaVersion: 1, suiteId: sgrPaletteSuiteId, runId, contractsVersion,
        adapter, scope: 'Pinned terminfo.dev@4.0.0 bright, 256-color and default-color termless criteria replayed byte-exactly on a disposable App '
            + 'and graded from parent-journaled readScreen stored palette indices, default markers and literal bytes together with '
            + 'App-resolved RGB bound to one recorded palette/theme state, with controls for the unaffected channel, unrelated '
            + 'attributes, DECSCNM, bold-versus-bright, boundary indices and reset restoration. '
            + 'Rendered glyph appearance is not claimed; no stock grades.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
