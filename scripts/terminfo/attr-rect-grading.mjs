// Parent-side report builder for the attribute-rectangle qualification. Re-derives every conclusion from the retained parent journal and
// the independent contract grader, refuses any tampering, and requires the pinned probes' own grading to agree: each pinned result is a
// pure function of the observed screens of its own fixture. The effect contract is only ever stricter than the pinned probe, EXCEPT for
// two preserved cases: editing.decsace (inconclusive by design) and the pinned DECCARA/DECRARA fixtures when the terminal follows the
// VT510 default stream extent (their pinned control requires the flank column to stay untouched, which the stream does not do). There
// the pinned verdict is preserved as the case conclusion and the effect contract is a separately identified verdict recorded beside it,
// never attributed to the pinned probe. The operation cases also record a labeled experiment: the same pinned code over the same pinned
// bytes taken with the rectangle extent selected first. It is never a pinned grade.
import { attrRectContracts, attrRectCaseIds, attrRectContractsVersion, attrRectNoncePrefix, attrRectSuiteId, attrRectEffectContractId,
    gradeAttrRectCase, attrRectObservation, attrRectObserved, validateAttrRectScreen, pinnedNotes, attrRectScreenText } from './attr-rect-contracts.mjs';
import { validateColorEvidence, incomplete, decided } from './sgr-color-grading-core.mjs';
import { validateGradedCaptureRecord, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement } from './graded-plan.mjs';
import { colorBindingHash } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { projectSavedResetCells } from './saved-reset-contracts.mjs';
import { attrRectAdapterId } from './attr-rect-grading-collector.mjs';
import { attrRectRecordingContext } from './attr-rect-collector.mjs';

/** Journal frames carry the observed buffer state and history as well as attributes, stored details and resolved colors. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true);
    validateAttrRectScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Attribute-rectangle', prefix: 'atrc', noncePrefix: attrRectNoncePrefix,
    contracts: attrRectContracts, caseIds: attrRectCaseIds, suiteId: attrRectSuiteId, version: attrRectContractsVersion,
    adapterId: attrRectAdapterId, derivedFrom: 'ntilde-owned-erase-attrs-region-grading-v1', screenOf,
    scope: 'Pinned terminfo.dev@4.0.0 termless criteria for editing.deccara (`ESC[1;1;3;5;7$r` over aaaaaZ/bbbbbY/cccccX), editing.decrara (`ESC[1;1;3;5;7$t` over '
            + 'the inverse-seeded rows) and editing.decsace (`ESC[1;1H ESC[2*x`, inconclusive by design in the pinned probe) replayed byte-exactly on a disposable App and graded on '
            + 'parent-journaled readScreen stored cells, cursor, attribute details and buffer facts. The pinned DECCARA/DECRARA fixtures run at the default extent, which the VT510 '
            + 'reference makes the stream, so their right-flank control is expected to fail there; those pinned verdicts are preserved and the separately identified effect contracts ('
            + attrRectEffectContractId + ') are graded from an independent oracle: stream and rectangle extents on the pinned fixtures, set-versus-replace, clear, toggle and '
            + 'restoration, selector tables, inclusive bounds, DECOM and margins, the unchanged cursor and SGR pen, and a restoration witness. The same pinned code over the pinned '
            + 'bytes with the rectangle extent selected first is recorded as a labeled experiment. The DECSACE setting is never read directly. Stored state only: no resolved-color, '
            + 'rendered-appearance, protection or pixel claim; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateAttrRectEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned getCell projection of the first `count` columns of the first rows, as the pinned code reads it. */
const cellsOf = (cellAt, rows, cols) => Array.from({ length: rows }, (_, row) => Array.from({ length: cols }, (_, col) => cellAt(row, col)));
/** The pinned getCursor shape of one screen. */
const cursorOf = screen => ({ x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible });

/** Re-derive the pinned DECCARA/DECRARA result over the screens its feeds were read from (seed, cursor, edited); `reads` selects the experiment. */
export function replayAttrRectOperation(contract, screens, reads = contract.pinnedReads) {
    const [seedScreen, cursorScreen, editedScreen] = reads.map(index => screens[index]);
    const notes = contract.notes, name = contract.family === 'reverse' ? 'DECRARA' : 'DECCARA';
    const cols = seedScreen.cols, rows = seedScreen.rows;
    if (!Number.isSafeInteger(cols) || cols < 6 || !Number.isSafeInteger(rows) || rows < 3) return incomplete(JSON.stringify({ cols, rows }), notes.geometry);
    const seedAt = projectSavedResetCells(seedScreen);
    const before = cellsOf(seedAt, 3, 6);
    const text = before.map(line => line.map(cell => cell.char).join('')).join('|');
    const seedBad = contract.family === 'reverse'
        ? before.some(line => !line.slice(0, 5).every(cell => cell.inverse) || line[5]?.inverse)
        : before.some(line => line.some(cell => cell.inverse));
    if (text !== 'aaaaaZ|bbbbbY|cccccX' || seedBad) return incomplete(JSON.stringify({ before }), notes.seed);
    const setup = cursorOf(cursorScreen);
    if (setup.x !== 0 || setup.y !== 0) return incomplete(JSON.stringify({ before, setup }), notes.cursor);
    const after = cellsOf(projectSavedResetCells(editedScreen), 3, 6);
    const response = JSON.stringify({ before, setup, after });
    const controlsValid = after.every((line, row) => line.every((cell, col) => cell.char === before[row]?.[col]?.char) && !line[5]?.inverse);
    if (!controlsValid) return incomplete(response, notes.control);
    const expectation = contract.family === 'reverse'
        ? after.every(line => line.slice(0, 5).every(cell => !cell.inverse)) : after.every(line => line.slice(0, 5).every(cell => cell.inverse));
    return decided(expectation, contract, response, name);
}

/** The pinned DECSACE probe, over the one read after its consumed feed: always the pinned inconclusive. */
export function replayAttrRectDecsace(contract, screens) {
    const text = attrRectScreenText(screens[contract.pinnedReads[0]]);
    return { pass: false, response: JSON.stringify({ text }), note: 'Sequence consumption does not measure attribute-change extent',
        observation: { outcome: 'inconclusive', evidence: 'consumed', reason: 'insufficient-evidence' } };
}

/** Re-derive the complete pinned termless result over the pinned fixture's observed screens, mirroring the pinned result shapes exactly. */
export const replayAttrRectPinned = (contract, screens, reads) => (contract.family === 'extent' ? replayAttrRectDecsace(contract, screens)
    : replayAttrRectOperation(contract, screens, reads));

/** Compare the pinned and effect verdicts. See the module comment for the two preserved cases; everything else follows the shared rule: the
 * contract is the finer grader and never more permissive than a pinned verdict that decided. */
export function reconcileAttrRect(contract, pinnedOutcome, effect, pinned) {
    if (contract.pinnedAlwaysInconclusive) {
        if (pinnedOutcome !== 'inconclusive') throw new Error(`Pinned ${contract.caseId} must be inconclusive by design.`);
        if (effect.outcome === 'inconclusive') return null;
        return { pinnedOutcome, contractOutcome: effect.outcome, kind: 'pinned-inconclusive-by-design' };
    }
    if (pinnedOutcome === effect.outcome) return null;
    if (pinnedOutcome === 'inconclusive' && effect.outcome === 'supported') {
        if (effect.defaultExtent === 'stream' && pinned.note === contract.notes.control) {
            return { pinnedOutcome, contractOutcome: effect.outcome, kind: 'pinned-fixture-assumes-rectangle-extent' };
        }
        throw new Error(`Contract ${contract.caseId} grading is more permissive than the pinned probe.`);
    }
    if (effect.outcome === 'supported') throw new Error(`Contract ${contract.caseId} grading is more permissive than the pinned probe.`);
    const kind = pinnedOutcome === 'inconclusive' ? 'contract-measured-mismatch' : pinnedOutcome === 'supported' ? 'contract-stricter-than-pinned' : 'contract-more-cautious-than-pinned';
    if (kind === 'contract-measured-mismatch' && effect.outcome !== 'unsupported') throw new Error(`Pinned and contract ${contract.caseId} grading disagree.`);
    return { pinnedOutcome, contractOutcome: effect.outcome, kind };
}

/** The conclusion's note: the pinned note when the pinned verdict is preserved, else the effect note when there is one. */
function conclusionNote(preserved, pinned, effect) {
    if (preserved) return { note: pinned.note ?? null };
    return effect.note ? { note: effect.note } : {};
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildAttrRectGradingReport(frames, run, runId, adapter, { contractsVersion = spec.version, replayModule = null } = {}) {
    validateAttrRectEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of spec.contracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${spec.label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, spec.label);
        if (!Number.isSafeInteger(recorded.pinnedFed) || recorded.pinnedFed < 0 || recorded.pinnedFed > contract.pinnedFeeds.length) throw new Error(`Invalid ${contract.caseId} pinned feed count.`);
        const experiment = contract.rectanglePinnedReads !== null;
        if (experiment !== (recorded.pinnedRectangle !== undefined) || (experiment && (!Number.isSafeInteger(recorded.pinnedRectangleFed)
            || recorded.pinnedRectangleFed < 0 || recorded.pinnedRectangleFed > contract.pinnedFeeds.length))) {
            throw new Error(`Invalid ${contract.caseId} rectangle-extent experiment record.`);
        }
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, spec.label);
        const screens = readFrames.map(frame => frame.screen);
        const observation = attrRectObservation(contract, screens);
        const effect = gradeAttrRectCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== (contract.family === 'extent' ? 'consumed' : 'parser-state')) throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        requirePinnedAgreement(contract, pinned, replayAttrRectPinned(contract, screens), spec.label);
        let rectangleExperiment = null;
        if (experiment) {
            const rectangle = recorded.pinnedRectangle;
            if (rectangle?.probeId !== contract.caseId) throw new Error(`Missing pinned ${contract.caseId} rectangle-extent experiment result.`);
            requirePinnedAgreement(contract, rectangle, replayAttrRectPinned(contract, screens, contract.rectanglePinnedReads), `${spec.label} rectangle experiment`);
            rectangleExperiment = { policy: 'labeled experiment, not a pinned grade: the same pinned code over the same pinned bytes taken after `ESC[2*x`; the stream is restored with `ESC[0*x` afterwards',
                outcome: rectangle.observation.outcome, pass: rectangle.pass, response: rectangle.response, note: rectangle.note ?? null };
        }
        const divergence = reconcileAttrRect(contract, pinned.observation.outcome, effect, pinned);
        if (replayModule) {
            const context = attrRectRecordingContext(contract, screens);
            const replay = replayModule.ntildeRunAttrRectTermless(contract.caseId, context);
            requireReplayAgreement(contract, replay, pinned);
            if (context.fedCount !== recorded.pinnedFed) throw new Error(`Pinned ${contract.caseId} replay fed a different number of pinned writes.`);
            if (experiment) {
                const second = attrRectRecordingContext(contract, screens, contract.rectanglePinnedReads);
                requireReplayAgreement(contract, replayModule.ntildeRunAttrRectTermless(contract.caseId, second), recorded.pinnedRectangle);
                if (second.fedCount !== recorded.pinnedRectangleFed) throw new Error(`Pinned ${contract.caseId} rectangle experiment fed a different number of pinned writes.`);
            }
        }
        const theme = screens[0].colorBinding;
        const preserved = contract.pinnedAlwaysInconclusive || divergence?.kind === 'pinned-fixture-assumes-rectangle-extent';
        caseResults.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds,
            pinnedFed: recorded.pinnedFed, terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: attrRectObserved(contract, observation),
            colorBinding: { reverseVideo: theme.reverseVideo, themeName: theme.themeName, foregroundArgb: theme.foregroundArgb, backgroundArgb: theme.backgroundArgb,
                minimumContrast: theme.minimumContrast, paletteOverrideIndices: [...theme.paletteOverrideIndices], bindingSha256: colorBindingHash(theme) },
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null },
            ...(rectangleExperiment ? { rectangleExperiment } : {}),
            effectContract: { id: attrRectEffectContractId, version: contractsVersion, outcome: effect.outcome,
                ...(effect.defaultExtent ? { defaultExtent: effect.defaultExtent } : {}), ...(effect.note ? { note: effect.note } : {}),
                attribution: preserved ? 'separately identified Ntilde effect contract; not the pinned probe' : 'finer grader over the pinned fixture' },
            contractResult: effect, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: preserved ? pinned.observation.outcome : effect.outcome, evidence: 'owned-stored-cells',
                grading: contract.pinnedAlwaysInconclusive ? 'pinned-termless-preserved-by-design' : preserved ? 'pinned-termless-preserved-extent-default' : 'pinned-termless-plus-contract-v' + contractsVersion,
                ...conclusionNote(preserved, pinned, effect), scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: spec.suiteId, runId, contractsVersion, adapter, scope: spec.scope, initialSnapshot: 0,
        restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
export { pinnedNotes };
