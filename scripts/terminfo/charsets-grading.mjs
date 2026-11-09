// Parent-side report builder for the charset qualification. Re-derives every conclusion from the retained parent
// journal and the independent contract grader, refuses any tampering, and requires the pinned probes' own grading to
// agree: each pinned result is a pure function of the observed physical cells of its own fixture. The contract may
// never be more permissive than the pinned probe; any difference in verdict semantics is recorded with both verdicts.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement, requireReplayAgreement,
    validateGradedCaptureRecord, planReads } from './graded-plan.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { reconcile } from './sgr-color-grading-core.mjs';
import { charsetsContracts, charsetsCaseIds, charsetsContractsVersion, charsetsNoncePrefix, charsetsSuiteId,
    gradeCharsetsCase, charsetsCellAt, charsetsObservation, validateCharsetsScreen } from './charsets-contracts.mjs';
import { charsetsAdapterId } from './charsets-grading-collector.mjs';
import { charsetsRecordingContext, requirePinnedReplay } from './charsets-collector.mjs';

const label = 'Charsets';
/** Total read groups across the contracts, plus start, bind and finish frames. */
const frameCount = 3 + charsetsContracts.reduce((sum, contract) => sum + planReads(contract), 0);

/** Charset screens carry stored cell details only (attribute strings omit wide continuations), and every row must cohere with them. */
const screenOf = value => {
    const screen = screenSnapshot(value, false, true, false, false, false, false);
    validateCharsetsScreen(screen);
    return screen;
};

/** Validate journal, run identity and restoration. */
export function validateCharsetsEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount, prefix: 'chrs', noncePrefix: charsetsNoncePrefix, runId, label, screenOf });
    if (!adapter || adapter.id !== charsetsAdapterId || run.adapterId !== charsetsAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== 'ntilde-owned-erase-display-grading-v1') {
        throw new Error(`Invalid ${label} adapter identity.`);
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...charsetsCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== charsetsContracts.length) {
        throw new Error(`${label} run does not cover the contracted cases.`);
    }
}

/** The pinned parserStateResult shape, mirrored exactly. */
export function parserStateResult(pass, expected, state, note) {
    const response = JSON.stringify(state);
    return {
        pass: pass === true,
        response,
        ...note && { note },
        observation: {
            outcome: pass === null ? 'inconclusive' : pass ? 'supported' : 'unsupported',
            ...pass === null && { reason: 'insufficient-evidence' },
            evidence: 'parser-state',
            ...note && { note },
        },
        ...pass !== null && { assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: JSON.stringify(state) }] },
    };
}

/** The pinned probes read `getCell(row, col).char` of physical cells; the projection mirrors that. */
const cellsOf = (screen, count) => Array.from({ length: count }, (_, col) => charsetsCellAt(screen, 0, col).char);
const cursorOf = screen => ({ x: screen.cursorCol, y: screen.cursorRow });

/** Re-derive the complete pinned termless result of one case over the observed screens, mirroring the pinned code path by path. */
export function replayCharsetsPinned(contract, screens) {
    const screenAt = role => screens[contract.roles.indexOf(role)];
    const { cols } = screenAt('initial');
    const expected = contract.pinnedExpected;
    const need = { 'charsets.dec-special': 3, 'charsets.dec-line-drawing': 7, 'charsets.g0-g1-switching': 5, 'charsets.utf8': 5 }[contract.caseId];
    if (!Number.isSafeInteger(cols) || cols < need) return parserStateResult(null, expected, { cols }, contract.pinnedGeometryNote);
    const before = cellsOf(screenAt('seed'), need);
    const seedText = { 'charsets.dec-special': 'qqq', 'charsets.dec-line-drawing': 'jklmqxj', 'charsets.g0-g1-switching': 'AlllZ', 'charsets.utf8': 'Axy?Z' }[contract.caseId];
    if (before.join('') !== seedText) return parserStateResult(null, expected, { before }, contract.pinnedFixtureNote);
    if (contract.caseId === 'charsets.utf8') return replayUtf8(contract, screenAt, expected, before);
    const first = cursorOf(screenAt('cup'));
    const setup = [{ x: first.x, y: first.y }];
    const startX = contract.caseId === 'charsets.dec-line-drawing' ? 0 : 1;
    const message = { 'charsets.dec-special': 'Target CUP did not reach DEC graphics cell', 'charsets.dec-line-drawing': 'Target CUP did not reach line-drawing start cell',
        'charsets.g0-g1-switching': 'Target CUP did not reach G0/G1 switching cells' }[contract.caseId];
    if (first.x !== startX || first.y !== 0) return parserStateResult(null, expected, { before, setup }, message);
    const after = cellsOf(screenAt('target'), need);
    if (contract.caseId === 'charsets.dec-special') {
        const controlsValid = after[0] === 'q' && after[2] === 'q';
        return parserStateResult(controlsValid ? after[1] === '─' : null, expected, { before, setup, after },
            controlsValid ? undefined : 'ASCII flank control changed after DEC designation');
    }
    if (contract.caseId === 'charsets.dec-line-drawing') {
        const controlsValid = after[6] === 'j';
        return parserStateResult(controlsValid ? after.slice(0, 6).join('') === '┘┐┌└─│' : null, expected, { before, setup, after },
            controlsValid ? undefined : 'Trailing ASCII j control changed after DEC line drawing');
    }
    const controlsValid = after[0] === 'A' && after[4] === 'Z';
    return parserStateResult(controlsValid ? after[1] === 'l' && after[2] === '┌' && after[3] === 'l' : null, expected, { before, setup, after },
        controlsValid ? undefined : 'ASCII A/Z control changed after G0/G1 switching');
}

/** The pinned charsets.utf8 path: two target CUPs and two Unicode samples; it never reads the wide continuation. */
function replayUtf8(contract, screenAt, expected, before) {
    const first = cursorOf(screenAt('cup1'));
    const setup = [{ x: first.x, y: first.y }];
    if (first.x !== 1 || first.y !== 0) return parserStateResult(null, expected, { before, setup }, 'First target CUP did not reach Unicode sample cell');
    const second = cursorOf(screenAt('cup2'));
    setup.push({ x: second.x, y: second.y });
    if (second.x !== 2 || second.y !== 0) return parserStateResult(null, expected, { before, setup }, 'Second target CUP did not reach Unicode sample cell');
    const after = cellsOf(screenAt('target'), 5);
    const controlsValid = after[0] === 'A' && after[4] === 'Z';
    return parserStateResult(controlsValid ? after[1] === 'é' && after[2] === '世' : null, expected, { before, setup, after },
        controlsValid ? undefined : 'ASCII control changed after Unicode samples');
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildCharsetsGradingReport(frames, run, runId, adapter, { contractsVersion = charsetsContractsVersion, replayModule = null } = {}) {
    validateCharsetsEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of charsetsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error(`${label} case order differs from the contracts.`);
        validateGradedCaptureRecord(contract, recorded, label);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, label);
        const screens = readFrames.map(frame => frame.screen);
        const observation = charsetsObservation(contract, screens);
        const contractResult = gradeCharsetsCase(contract, observation, contractsVersion);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        requirePinnedAgreement(contract, pinned, replayCharsetsPinned(contract, screens), label);
        const divergence = reconcile(contract, pinned.observation.outcome, contractResult);
        if (replayModule) {
            const context = charsetsRecordingContext(contract, screens);
            const replay = replayModule.ntildeRunCharsetsTermless(contract.caseId, context);
            requireReplayAgreement(contract, replay, pinned);
            requirePinnedReplay(contract, context, replay);
        }
        caseResults.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion, pinnedFeeds: contract.pinnedFeeds,
            pinnedFinally: contract.pinnedFinally, overlaps: contract.overlaps, terminalFixture: contract.terminalFixture,
            feeds: recorded.feeds, barriers: recorded.barriers, reads: recorded.reads.map(read => read.cursor), observed: contract.observed(observation),
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null, observation: pinned.observation, assertions: pinned.assertions ?? null },
            contractResult, ...(divergence ? { divergence } : {}),
            conclusion: { outcome: contractResult.outcome, evidence: 'owned-stored-physical-cells',
                grading: 'pinned-termless-plus-contract-v' + contractsVersion,
                ...(contractResult.note ? { note: contractResult.note } : {}), scope: contract.appearanceScope } });
    }
    return { schemaVersion: 1, suiteId: charsetsSuiteId, runId, contractsVersion, adapter,
        scope: 'Pinned terminfo.dev@4.0.0 charset termless criteria (charsets.utf8, charsets.dec-special, charsets.dec-line-drawing and charsets.g0-g1-switching) replayed '
            + 'byte-exactly (UTF-8 text, escape designations and shift controls untouched) on a disposable App and graded from parent-journaled stored cell details and text: '
            + 'decoded physical cells (narrow, wide, wide continuation), the cursor and neutral stored attributes, with controls for the ASCII seed using the same source characters, '
            + 'designation without invocation, invocation without designation, direct G0 and G1 routes, an uppercase control, persistence of the G1 designation past SI, a wide-alone and a '
            + 'precomposed control, and lowercase restoration witnesses (an uppercase marker never proves ASCII restoration). The pinned terminal pixel capture and cursor-query paths are '
            + 'never verdicts and are not qualified; no glyph, pixel, font fallback, keyboard, locale, normalization or platform claim; no stock grades; the Unicode scope is this fixture\'s two samples only.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
