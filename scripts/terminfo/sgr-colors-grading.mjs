// Parent-side report builder for the SGR reset/standard-color qualification
// batch. Re-derives every conclusion from the retained parent journal and the
// independent contract grader, refuses any tampering, and requires the pinned
// probe's own grading to agree — as a measured outcome for the reset cases,
// and as its own indistinguishable-inconclusive verdict for the named-color
// cases, whose RGB criterion stored observations deliberately cannot expose.
// Shared journal validation and pinned-comparison logic live in graded-plan.mjs.
import { validateGradedJournal, crossCheckGradedCapture, requirePinnedAgreement,
    requireReplayAgreement, planFeeds, planReads, validateExactFeeds } from './graded-plan.mjs';
import { sgrColorsContracts, sgrColorsCaseIds, sgrColorsContractsVersion, sgrColorsNoncePrefix,
    sgrColorsSuiteId, gradeSgrColorsCase, sgrColorsCellAt } from './sgr-colors-contracts.mjs';
import { sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';

const readCount = sgrColorsContracts.reduce((sum, contract) => sum + planReads(contract), 0);
const frameCount = 3 + readCount;

/** Validate journal, run identity, exact inputs, controls and restoration. */
export function validateSgrColorsEvidence(frames, run, runId, adapter) {
    validateGradedJournal(frames, { frameCount, prefix: 'sgcolors', noncePrefix: sgrColorsNoncePrefix,
        runId, label: 'SGR-colors' });
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

/** The response the pinned parserStateResult stringifies for one read group. */
function pinnedResponse(contract, cells) {
    if (contract.pinnedExpectation === 'measured') {
        return JSON.stringify({ baseline: cells[0], before: cells[1], after: cells[2] });
    }
    return JSON.stringify({ baseline: cells[0], first: cells[1], second: cells[2] });
}

/** Re-derive the complete pinned termless result from observed screens,
 * mirroring the pinned parserStateResult shapes exactly. */
export function replaySgrColorsPinned(contract, screens) {
    const cells = [0, 1, 2].map(col => sgrColorsCellAt(screens[0], 0, col));
    const response = pinnedResponse(contract, cells);
    const charsExposed = cells[0].char === 'C' && cells[1].char === 'X' && cells[2].char === 'Y';
    if (contract.pinnedExpectation === 'measured') {
        const expected = `${contract.caseId}: ${contract.pinnedCriterion}`;
        if (!charsExposed) return incomplete(response, 'Baseline, styled, or reset cell was not exposed');
        // measuredAttribute semantics: undefined -> null, boolean -> value.
        const attrValue = (cell, name) => {
            const value = cell[name];
            return value === undefined ? null : typeof value === 'boolean' ? value : null;
        };
        const attributes = [...new Set([...contract.set, ...contract.preserve])];
        const measured = attributes.every(name => typeof cells[2][name] === 'boolean');
        const baselineOk = attributes.every(name => attrValue(cells[0], name) === false);
        const beforeOk = attributes.every(name => attrValue(cells[1], name) === true);
        if (!measured || !baselineOk || !beforeOk) return incomplete(response, contract.pinnedControlNote);
        const cleared = contract.set.every(name => attrValue(cells[2], name) === false);
        const retained = contract.preserve.every(name => attrValue(cells[2], name) === true);
        const pass = cleared && retained;
        return { pass, response,
            observation: { outcome: pass ? 'supported' : 'unsupported', evidence: 'parser-state' },
            assertions: [{ kind: pass ? 'positive' : 'negative', expected, observed: response }] };
    }
    if (!charsExposed) return incomplete(response, 'Neutral control or named-color cells were not exposed');
    // Stored observations expose no RGB for palette colors, so the pinned
    // criterion's own indistinguishable verdict applies deterministically.
    return incomplete(response, contract.pinnedControlNote);
}

function incomplete(response, note) {
    return { pass: false, response, note, assertions: [],
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'parser-state', note } };
}

/** Validate one case's recorded capture record shape. */
function validateCaptureRecord(contract, recorded) {
    validateExactFeeds(contract, recorded.feeds, 'contract');
    if (!Array.isArray(recorded.barriers) || recorded.barriers.length !== planFeeds(contract).length
        || recorded.barriers.some(barrier => !/^\x1b\[\?[\d;]+c$/.test(barrier ?? ''))
        || !Array.isArray(recorded.reads) || recorded.reads.length !== planReads(contract)) {
        throw new Error(`Invalid ${contract.caseId} graded capture record.`);
    }
    for (const [step, read] of contract.plan.filter(item => item.read).entries()) {
        if (recorded.reads[step].kind !== read.read) throw new Error(`Wrong ${contract.caseId} read kind.`);
    }
}

/** Re-derive and cross-check every case; throw on any disagreement. */
export function buildSgrColorsGradingReport(frames, run, runId, adapter, replayModule = null) {
    validateSgrColorsEvidence(frames, run, runId, adapter);
    let frameIndex = 2;
    const caseResults = [];
    for (const [position, contract] of sgrColorsContracts.entries()) {
        const recorded = run.cases[position];
        if (recorded.caseId !== contract.caseId) throw new Error('SGR-colors case order differs from the contracts.');
        validateCaptureRecord(contract, recorded);
        const readFrames = recorded.reads.map(() => frames[frameIndex++]);
        crossCheckGradedCapture(contract, recorded, readFrames, 'SGR-colors');
        const screens = readFrames.map(frame => frame.screen);
        const projected = [0, 1, 2].map(col => sgrColorsCellAt(screens[0], 0, col));
        const contractResult = gradeSgrColorsCase(contract, projected);
        const pinned = recorded.pinned;
        if (pinned?.probeId !== contract.caseId || pinned.observation?.evidence !== 'parser-state') {
            throw new Error(`Missing pinned ${contract.caseId} grading result.`);
        }
        // The complete pinned result is a pure function of the observed
        // screens; the retained run-log values must match that derivation.
        requirePinnedAgreement(contract, pinned, replaySgrColorsPinned(contract, screens), 'SGR-colors');
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
            requireReplayAgreement(contract, replay, pinned);
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
