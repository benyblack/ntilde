// Parent-side report builder for the ASCII/SGR qualification batch. Re-derives
// every conclusion from the retained parent journal and the contract grader,
// refuses any tampering, and requires the pinned probe's own grading to agree
// before a conclusive outcome is published.
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { asciiSgrContracts, asciiSgrCaseIds, asciiSgrContractsVersion, asciiSgrNoncePrefix,
    asciiSgrSuiteId, contractFeeds, contractReads, gradeRecordedCase, validateRecordedFeeds, expandScreenFlags } from './ascii-sgr-contracts.mjs';
import { recordingContext } from './ascii-sgr-collector.mjs';
import { gradingAdapterId } from './grading-collector.mjs';

const cellAt = (screen, row, col) => {
    const flags = expandScreenFlags(screen)[row * screen.cols + col] ?? 0;
    return { char: (screen.lines[row] ?? ' ')[col] ?? ' ', flags,
        bold: (flags & 0x0001) !== 0, italic: (flags & 0x0004) !== 0 };
};

/** Re-derive the complete pinned termless result from observed screens.
 * Mirrors the pinned parserStateResult pass/response/assertions/note shapes
 * exactly, so retained run-log claims cannot diverge from the journal. */
export function replayPinnedFromScreens(contract, screens) {
    const graded = gradeRecordedCase(contract, screens);
    const response = contract.caseId === 'text.basic'
        ? JSON.stringify({ before: screens[0].lines.join('\n'), after: screens[1].lines.join('\n') })
        : contract.caseId === 'text.overwrite'
            ? JSON.stringify({ before: [cellAt(screens[0], 0, 0), cellAt(screens[0], 0, 1)],
                first: cellAt(screens[1], 0, 0), second: cellAt(screens[1], 0, 1) })
            : JSON.stringify(cellAt(screens[0], 0, 0));
    const expected = contract.caseId.startsWith('sgr.')
        ? `${contract.caseId}: attribute applied to the rendered X cell`
        : contract.pinnedCriterion;
    const inconclusive = graded.outcome === 'inconclusive';
    return { pass: graded.outcome === 'supported', response,
        note: inconclusive ? graded.note : undefined,
        assertions: inconclusive ? []
            : [{ kind: graded.outcome === 'supported' ? 'positive' : 'negative', expected, observed: response }],
        observation: inconclusive
            ? { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note: graded.note }
            : { outcome: graded.outcome, evidence: 'parser-state' } };
}

const readCount = asciiSgrContracts.reduce((sum, contract) => sum + contractReads(contract), 0);
const frameCount = 3 + readCount;

function validateFrame(frame, index, baseline) {
    screenSnapshot(frame.screen, true);
    if (frame.index !== index || frame.kind !== expectedKind(index) || frame.sha256 !== snapshotHash(frame.screen)
        || frame.paneId !== baseline.paneId
        || frame.screen.rows !== baseline.screen.rows || frame.screen.cols !== baseline.screen.cols) {
        throw new Error('Changed graded snapshot evidence.');
    }
    if (index === 0 ? frame.barrier !== null : !/^\x1b\[\?[\d;]+c$/.test(frame.barrier ?? '')) {
        throw new Error('Missing graded ordering barrier evidence.');
    }
}

function expectedKind(index) {
    if (index === 0) return 'grading-start';
    if (index === 1) return 'grading-bind';
    if (index === frameCount - 1) return 'grading-finish';
    return 'grading-read';
}

/** Validate journal, run identity, exact inputs, controls and restoration. */
export function validateGradingEvidence(frames, run, runId, adapter) {
    if (!Array.isArray(frames) || frames.length !== frameCount) throw new Error('Incomplete graded snapshot sequence.');
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, frames[0]);
    const binding = frames[1].screen;
    if (!/^[a-f\d]{32}$/.test(runId ?? '') || binding.lines[0] !== asciiSgrNoncePrefix + runId
        || binding.cursorRow !== 0 || binding.cursorCol !== (asciiSgrNoncePrefix + runId).length) {
        throw new Error('Graded evidence is not bound to this run.');
    }
    if (frames[0].sha256 !== frames.at(-1).sha256) throw new Error('Graded fixture was not restored.');
    if (!adapter || adapter.id !== gradingAdapterId || run.adapterId !== gradingAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== 'ntilde-owned-macos-v1') {
        throw new Error('Invalid grading adapter identity.');
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...asciiSgrCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== asciiSgrContracts.length) {
        throw new Error('Graded run does not cover the contracted cases.');
    }
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildAsciiSgrGradingReport(frames, run, runId, adapter, replayModule = null) {
    validateGradingEvidence(frames, run, runId, adapter);
    let frameIndex = 2, caseResults = [];
    for (const [position, contract] of asciiSgrContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('Graded case order differs from the contracts.');
        validateRecordedFeeds(contract, recorded.feeds);
        const expectedFeeds = contractFeeds(contract);
        if (!Array.isArray(recorded.barriers) || recorded.barriers.length !== expectedFeeds.length
            || recorded.barriers.some(barrier => !/^\x1b\[\?[\d;]+c$/.test(barrier ?? ''))
            || !Array.isArray(recorded.reads) || recorded.reads.length !== contractReads(contract)) {
            throw new Error(`Invalid ${contract.caseId} graded capture record.`);
        }
        for (const [step, read] of contract.plan.filter(item => item.read).entries()) {
            if (recorded.reads[step].kind !== read.read) throw new Error(`Wrong ${contract.caseId} read kind.`);
        }
        // Every read frame must carry the barrier of the feed that preceded it
        // and the cursor the run log claims for it.
        let feedSteps = 0, readSteps = 0;
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        for (const step of contract.plan) {
            if (step.feed) feedSteps++;
            else {
                const frame = readFrames[readSteps];
                if (frame.barrier !== recorded.barriers[feedSteps - 1]) {
                    throw new Error(`Graded ${contract.caseId} barrier differs from its read frame.`);
                }
                if (JSON.stringify(recorded.reads[readSteps].cursor) !== JSON.stringify([frame.screen.cursorRow, frame.screen.cursorCol])) {
                    throw new Error(`Graded ${contract.caseId} cursor differs from its read frame.`);
                }
                readSteps++;
            }
        }
        const screens = readFrames.map(frame => frame.screen);
        const contractResult = gradeRecordedCase(contract, screens);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        // The pinned result is a pure function of the observed screens; the
        // retained run-log values must match that derivation, including the
        // exact observation object shape.
        const replayedPair = replayPinnedFromScreens(contract, screens);
        if (pinned.pass !== replayedPair.pass || pinned.response !== replayedPair.response
            || (pinned.note ?? null) !== (replayedPair.note ?? null)
            || JSON.stringify(pinned.assertions ?? null) !== JSON.stringify(replayedPair.assertions.length ? replayedPair.assertions : null)
            || JSON.stringify(pinned.observation) !== JSON.stringify(replayedPair.observation)) {
            throw new Error(`Pinned ${contract.caseId} grading does not match its observed evidence.`);
        }
        if (pinned.observation.outcome !== contractResult.outcome
            || (contractResult.note && pinned.observation.note !== contractResult.note)) {
            throw new Error(`Pinned and contract ${contract.caseId} grading disagree.`);
        }
        if (replayModule) {
            const replay = replayModule.ntildeRunTermless(contract.caseId, recordingContext(contract, screens));
            if (replay.pass !== pinned.pass || replay.response !== pinned.response
                || replay.observation.outcome !== pinned.observation.outcome
                || JSON.stringify(replay.assertions ?? null) !== JSON.stringify(pinned.assertions ?? null)) {
                throw new Error(`Pinned ${contract.caseId} replay does not reproduce the live grading.`);
            }
        }
        caseResults.push({ caseId: contract.caseId, pinnedPath: 'termless', pinnedCriterion: contract.pinnedCriterion,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: contract.observed(screens),
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null,
                observation: pinned.observation, assertions: pinned.assertions ?? null },
            contractResult, conclusion: { outcome: contractResult.outcome, evidence: 'owned-cells',
                grading: 'pinned-termless-plus-contract-v' + asciiSgrContractsVersion,
                ...(contractResult.note ? { note: contractResult.note } : {}),
                ...(contract.appearanceScope ? { scope: contract.appearanceScope } : {}) } });
    }
    return { schemaVersion: 1, suiteId: asciiSgrSuiteId, runId, contractsVersion: asciiSgrContractsVersion,
        adapter, scope: 'Pinned terminfo.dev@4.0.0 termless criteria replayed byte-exactly on a disposable App and graded from parent-journaled readScreen cells/flags. '
            + 'SGR conclusions cover stored flags, never glyph appearance. Not stock collector output.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
