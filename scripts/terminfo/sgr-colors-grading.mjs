// Parent-side report builder for the SGR reset/standard-color qualification
// batch. Re-derives every conclusion from the retained parent journal and the
// independent contract grader, refuses any tampering, and requires the pinned
// probe's own grading to agree — as a measured outcome for the reset cases,
// and as its own indistinguishable-inconclusive verdict for the named-color
// cases, whose RGB criterion stored observations deliberately cannot expose.
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { sgrColorsContracts, sgrColorsCaseIds, sgrColorsContractsVersion, sgrColorsNoncePrefix,
    sgrColorsSuiteId, sgrColorsFeeds, sgrColorsReads, gradeSgrColorsCase,
    validateSgrColorsFeeds, sgrColorsCellAt } from './sgr-colors-contracts.mjs';
import { sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';

const readCount = sgrColorsContracts.reduce((sum, contract) => sum + sgrColorsReads(contract), 0);
const frameCount = 3 + readCount;

function expectedKind(index) {
    if (index === 0) return 'sgcolors-start';
    if (index === 1) return 'sgcolors-bind';
    if (index === frameCount - 1) return 'sgcolors-finish';
    return 'sgcolors-read';
}

function validateFrame(frame, index, baseline) {
    screenSnapshot(frame.screen, true);
    if (frame.index !== index || frame.kind !== expectedKind(index) || frame.sha256 !== snapshotHash(frame.screen)
        || frame.paneId !== baseline.paneId
        || frame.screen.rows !== baseline.screen.rows || frame.screen.cols !== baseline.screen.cols) {
        throw new Error('Changed SGR-colors snapshot evidence.');
    }
    if (index === 0 ? frame.barrier !== null : !/^\x1b\[\?[\d;]+c$/.test(frame.barrier ?? '')) {
        throw new Error('Missing SGR-colors ordering barrier evidence.');
    }
}

/** Validate journal, run identity, exact inputs, controls and restoration. */
export function validateSgrColorsEvidence(frames, run, runId, adapter) {
    if (!Array.isArray(frames) || frames.length !== frameCount) throw new Error('Incomplete SGR-colors snapshot sequence.');
    for (const [index, frame] of frames.entries()) validateFrame(frame, index, frames[0]);
    const binding = frames[1].screen;
    if (!/^[a-f\d]{32}$/.test(runId ?? '') || binding.lines[0] !== sgrColorsNoncePrefix + runId
        || binding.cursorRow !== 0 || binding.cursorCol !== (sgrColorsNoncePrefix + runId).length) {
        throw new Error('SGR-colors evidence is not bound to this run.');
    }
    if (frames[0].sha256 !== frames.at(-1).sha256) throw new Error('SGR-colors fixture was not restored.');
    if (!adapter || adapter.id !== sgrColorsAdapterId || run.adapterId !== sgrColorsAdapterId
        || !/^[a-f\d]{64}$/.test(adapter.bundleSha256 ?? '') || !/^[a-f\d]{64}$/.test(adapter.upstreamBundleSha256 ?? '')
        || adapter.derivedFromAdapterId !== 'ntilde-owned-ascii-sgr-grading-v1') {
        throw new Error('Invalid SGR-colors adapter identity.');
    }
    if (JSON.stringify(run.probeIds) !== JSON.stringify([...sgrColorsCaseIds])
        || !Array.isArray(run.cases) || run.cases.length !== sgrColorsContracts.length) {
        throw new Error('SGR-colors run does not cover the contracted cases.');
    }
}

/** Re-derive the complete pinned termless result from observed screens,
 * mirroring the pinned parserStateResult shapes exactly. */
export function replaySgrColorsPinned(contract, screens) {
    const cell = (row, col) => sgrColorsCellAt(screens[0], row, col);
    const isReset = contract.pinnedExpectation === 'measured';
    if (isReset) {
        const baseline = cell(0, 0), before = cell(0, 1), after = cell(0, 2);
        const state = { baseline, before, after };
        const response = JSON.stringify(state);
        const expected = `${contract.caseId}: ${contract.pinnedCriterion}`;
        if (baseline.char !== 'C' || before.char !== 'X' || after.char !== 'Y') {
            return incomplete(response, 'Baseline, styled, or reset cell was not exposed');
        }
        // measuredAttribute semantics: undefined -> null, boolean -> value.
        const attrValue = (cellValue, name) => {
            const value = cellValue[name];
            return value === undefined ? null : typeof value === 'boolean' ? value : null;
        };
        const attributes = contractAttributes(contract);
        const baselineOk = attributes.every(name => attrValue(baseline, name) === false);
        const beforeOk = attributes.every(name => attrValue(before, name) === true);
        const afterMeasured = attributes.every(name => attrValue(after, name) !== null);
        if (!baselineOk || !beforeOk || !afterMeasured) {
            return incomplete(response, contract.pinnedControlNote);
        }
        const cleared = contractSet(contract).every(name => attrValue(after, name) === false);
        const retained = contractPreserve(contract).every(name => attrValue(after, name) === true);
        const pass = cleared && retained;
        return { pass, response,
            observation: { outcome: pass ? 'supported' : 'unsupported', evidence: 'parser-state' },
            assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] };
    }
    const baseline = cell(0, 0), first = cell(0, 1), second = cell(0, 2);
    const state = { baseline, first, second };
    const response = JSON.stringify(state);
    if (baseline.char !== 'C' || first.char !== 'X' || second.char !== 'Y') {
        return incomplete(response, 'Neutral control or named-color cells were not exposed');
    }
    // Stored observations expose no RGB for palette colors, so the pinned
    // criterion's own indistinguishable verdict applies deterministically.
    return incomplete(response, contract.pinnedControlNote);
}

function incomplete(response, note) {
    return { pass: false, response, note, assertions: [],
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}

/** The attribute names the pinned reset probe sets or preserves. */
function contractAttributes(contract) {
    return [...new Set([...contractSet(contract), ...contractPreserve(contract)])];
}

const contractSet = contract => contract.caseId === 'sgr.selective-reset.bold' ? ['bold', 'dim']
    : contract.caseId === 'sgr.selective-reset.italic' ? ['italic']
        : contract.caseId === 'sgr.reset' ? ['bold', 'italic', 'underline'] : [];
const contractPreserve = contract => contract.caseId === 'sgr.selective-reset.bold' ? ['italic']
    : contract.caseId === 'sgr.selective-reset.italic' ? ['bold'] : [];

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildSgrColorsGradingReport(frames, run, runId, adapter, replayModule = null) {
    validateSgrColorsEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of sgrColorsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('SGR-colors case order differs from the contracts.');
        validateSgrColorsFeeds(contract, recorded.feeds);
        const expectedFeeds = sgrColorsFeeds(contract);
        if (!Array.isArray(recorded.barriers) || recorded.barriers.length !== expectedFeeds.length
            || recorded.barriers.some(barrier => !/^\x1b\[\?[\d;]+c$/.test(barrier ?? ''))
            || !Array.isArray(recorded.reads) || recorded.reads.length !== sgrColorsReads(contract)) {
            throw new Error(`Invalid ${contract.caseId} graded capture record.`);
        }
        for (const [step, read] of contract.plan.filter(item => item.read).entries()) {
            if (recorded.reads[step].kind !== read.read) throw new Error(`Wrong ${contract.caseId} read kind.`);
        }
        // Every read frame must carry the barrier and cursor of its feed.
        let feedSteps = 0, readSteps = 0;
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        for (const step of contract.plan) {
            if (step.feed) feedSteps++;
            else {
                const frame = readFrames[readSteps];
                if (frame.barrier !== recorded.barriers[feedSteps - 1]) {
                    throw new Error(`SGR-colors ${contract.caseId} barrier differs from its read frame.`);
                }
                if (JSON.stringify(recorded.reads[readSteps].cursor) !== JSON.stringify([frame.screen.cursorRow, frame.screen.cursorCol])) {
                    throw new Error(`SGR-colors ${contract.caseId} cursor differs from its read frame.`);
                }
                readSteps++;
            }
        }
        const screens = readFrames.map(frame => frame.screen);
        const projected = [0, 1, 2].map(col => sgrColorsCellAt(screens[0], 0, col));
        const contractResult = gradeSgrColorsCase(contract, projected);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        // The complete pinned result is a pure function of the observed
        // screens; the retained run-log values must match that derivation.
        const replayed = replaySgrColorsPinned(contract, screens);
        const present = value => (value?.length ? value : null);
        if (pinned.pass !== replayed.pass || pinned.response !== replayed.response
            || (pinned.note ?? null) !== (replayed.note ?? null)
            || JSON.stringify(present(pinned.assertions)) !== JSON.stringify(present(replayed.assertions))
            || JSON.stringify(pinned.observation) !== JSON.stringify(replayed.observation)) {
            throw new Error(`Pinned ${contract.caseId} grading does not match its observed evidence.`);
        }
        if (contract.pinnedExpectation === 'measured') {
            if (pinned.observation.outcome !== contractResult.outcome) {
                throw new Error(`Pinned and contract ${contract.caseId} grading disagree.`);
            }
        } else if (pinned.observation.outcome !== 'inconclusive'
            || pinned.observation.note !== contract.pinnedControlNote) {
            throw new Error(`Pinned ${contract.caseId} verdict is not the expected inapplicable-inconclusive.`);
        }
        if (replayModule) {
            const replay = replayModule.ntildeRunSgrColorsTermless(contract.caseId,
                { feed() {}, getText() { throw new Error('unexpected'); }, getCell: (row, col) => sgrColorsCellAt(screens[0], row, col) });
            if (replay.pass !== pinned.pass || replay.response !== pinned.response
                || replay.observation.outcome !== pinned.observation.outcome
                || JSON.stringify(replay.assertions ?? null) !== JSON.stringify(pinned.assertions ?? null)) {
                throw new Error(`Pinned ${contract.caseId} replay does not reproduce the live grading.`);
            }
        }
        caseResults.push({ caseId: contract.caseId, pinnedCriterion: contract.pinnedCriterion,
            pinnedExpectation: contract.pinnedExpectation,
            terminalFixture: contract.terminalFixture, feeds: recorded.feeds, barriers: recorded.barriers,
            reads: recorded.reads.map(read => read.cursor), observed: contract.observed(projected),
            pinned: { pass: pinned.pass, response: pinned.response, note: pinned.note ?? null,
                observation: pinned.observation, assertions: pinned.assertions ?? null },
            contractResult, conclusion: { outcome: contractResult.outcome, evidence: 'owned-cells',
                grading: 'pinned-termless-plus-contract-v' + sgrColorsContractsVersion,
                ...(contractResult.note ? { note: contractResult.note } : {}),
                ...(contract.appearanceScope ? { scope: contract.appearanceScope }
                    : { scope: 'stored style flags and default color markers; no glyph appearance or theme RGB' }) } });
    }
    return { schemaVersion: 1, suiteId: sgrColorsSuiteId, runId, contractsVersion: sgrColorsContractsVersion,
        adapter, scope: 'Pinned terminfo.dev@4.0.0 termless criteria replayed byte-exactly on a disposable App and graded from parent-journaled readScreen stored attributes and palette indices. '
            + 'Named-color conclusions cover stored indices and markers, never theme-resolved RGB. Not stock collector output.',
        initialSnapshot: 0, restoredSnapshot: frames.length - 1, cases: caseResults, snapshots: frames };
}
