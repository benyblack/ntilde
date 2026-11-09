// Parent-side report builder for the selective-erasure qualification. Re-derives every conclusion from the retained parent
// journal and the independent contract grader, refuses any tampering, and requires the pinned probe's own
// selectiveEraseResult grading to agree: the pinned result is a pure function of the observed cells of its own fixture.
// The contract may never be more permissive than the pinned probe; any difference in verdict semantics is recorded with both
// verdicts. Unlike the pinned probe the contract also reads stored protection, so a lost P or Z with valid seeds is graded
// unsupported where the pinned probe is inconclusive (a recorded divergence).
import { selectiveEraseContracts, selectiveEraseCaseIds, selectiveEraseContractsVersion, selectiveEraseNoncePrefix, selectiveEraseSuiteId,
    gradeSelectiveEraseCase, selectiveEraseCellAt, selectiveEraseObservation, validateSelectiveEraseScreen,
    pinnedColumnsNote, pinnedSeedNote, pinnedControlNote, pinnedExpectedText } from './selective-erase-contracts.mjs';
import { validateColorEvidence, buildColorReport } from './sgr-color-grading-core.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { selectiveEraseAdapterId } from './selective-erase-grading-collector.mjs';
import { selectiveEraseRecordingContext, expectedPinnedFeeds } from './selective-erase-collector.mjs';

/** Selective-erase screens carry attributes, stored details (with overline and protection), resolved colors, buffer state and history together. */
const screenOf = value => {
    const screen = screenSnapshot(value, true, true, false, true, true, true, false, true);
    validateSelectiveEraseScreen(screen);
    return screen;
};

/** The suite's report spec: identities, pinned replay and scope. */
const spec = Object.freeze({ label: 'Selective-erase', prefix: 'sels', noncePrefix: selectiveEraseNoncePrefix,
    contracts: selectiveEraseContracts, caseIds: selectiveEraseCaseIds, suiteId: selectiveEraseSuiteId, version: selectiveEraseContractsVersion,
    adapterId: selectiveEraseAdapterId, derivedFrom: 'ntilde-owned-erase-chars-history-grading-v1',
    screenOf,
    observation: selectiveEraseObservation, grade: gradeSelectiveEraseCase,
    replayPinned: (contract, screens) => replaySelectiveErasePinned(contract, screens),
    recordingContext: selectiveEraseRecordingContext, replayFn: 'ntildeRunSelectiveEraseTermless',
    scope: 'Pinned terminfo.dev@4.0.0 selective-erasure termless criteria (erase.selective with DECSED CSI ? 2 J and editing.decsera with DECSERA CSI 1;1;1;5 $ {, '
            + 'both over the seed ESC[1;1H ESC[1"q P ESC[0"q ABCDZ and finalized with ESC[0"q) replayed byte-exactly on a disposable App and graded off '
            + 'parent-journaled readScreen stored cells, the cursor, stored attribute details, opt-in per-cell protection and buffer state, with controls for the '
            + 'unmodified seed (no-op), an ordinary erase of the same seed (protected text is not read-only), a protection-off control (DECSCA 0 changes later writes only), '
            + 'an overwrite control, a restoration control for the pinned finalizer, a full-screen fill control (accidental whole-screen clearing), an edge control, '
            + 'a bold-seeded attribute control (retained stored tuple required; background-color erase observed, not required), a main-screen history control, and '
            + 'rectangle-bounds controls for DECSERA (observed DECSED mode 0 and 1 for DECSED). Stored state only: the pinned terminal pixel paths only query the cursor and '
            + 'never carry a verdict and are not qualified; ASCII only; no stock grades.' });

/** Validate journal, run identity, binding stability and restoration. */
export const validateSelectiveEraseEvidence = (frames, run, runId, adapter) => validateColorEvidence(spec, frames, run, runId, adapter);

/** The pinned parserStateResult shape, byte for byte. */
function pinnedResult(pass, state, note) {
    const response = JSON.stringify(state);
    return { pass: pass === true, response, ...(note && { note }),
        observation: { outcome: pass === null ? 'inconclusive' : pass ? 'supported' : 'unsupported',
            ...(pass === null && { reason: 'insufficient-evidence' }), evidence: 'parser-state', ...(note && { note }) },
        ...(pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected: pinnedExpectedText, observed: response }] }) };
}
/** The pinned isBlank. */
const isBlank = char => char === '' || char === ' ';
/** The pinned `getCell(0, col).char` of the first six cells of row 0. */
const rowChars = screen => Array.from({ length: 6 }, (_, col) => selectiveEraseCellAt(screen, 0, col).char);

/** Re-derive the complete pinned selectiveEraseResult over the seed and erased screens of the pinned fixture, mirroring the pinned result shapes exactly. */
export function replaySelectiveErasePinned(contract, screens) {
    const seeded = screens[contract.setupRead], erased = screens[contract.pinnedRead];
    if (!Number.isSafeInteger(seeded.cols) || seeded.cols < 6) return pinnedResult(null, { cols: seeded.cols }, pinnedColumnsNote);
    const before = rowChars(seeded);
    if (before.join('') !== 'PABCDZ') return pinnedResult(null, { before }, pinnedSeedNote);
    const after = rowChars(erased);
    const controlsValid = after[0] === 'P' && (!contract.rectangular || after[5] === 'Z');
    const pass = controlsValid ? after.slice(1, contract.rectangular ? 5 : 6).every(isBlank) : null;
    return pinnedResult(pass, { before, after }, controlsValid ? undefined : pinnedControlNote);
}

/** Re-derive and cross-check every case; throw on any disagreement, including a pinned run that fed anything but its contracted bytes. */
export function buildSelectiveEraseGradingReport(frames, run, runId, adapter, options) {
    const report = buildColorReport(spec, frames, run, runId, adapter, options);
    for (const [index, item] of report.cases.entries()) {
        const contract = selectiveEraseContracts[index];
        const fed = run.cases[index].pinnedFed;
        if (!Array.isArray(fed) || JSON.stringify(fed) !== JSON.stringify(expectedPinnedFeeds(contract, item.pinned))) {
            throw new Error(`Pinned ${contract.caseId} did not replay exactly its contracted fixture.`);
        }
    }
    return report;
}
