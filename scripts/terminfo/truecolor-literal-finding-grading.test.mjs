import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildTruecolorFindingReport, validateTruecolorFindingEvidence } from './truecolor-literal-finding-grading.mjs';
import { truecolorFindingContracts, truecolorFindingSuiteId, truecolorFindingFeeds } from './truecolor-literal-finding-contracts.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { syntheticTruecolorFindingEvidence, findingCells, findingBinding } from './fixtures/truecolor-literal-finding-evidence.mjs';
import { canonicalPalette, paletteBinding } from './fixtures/sgr-palette-evidence.mjs';

const rebuild = e => buildTruecolorFindingReport(e.frames, e.run, e.runId);
const rehash = (evidence, index, mutate) => {
    const screen = structuredClone(evidence.frames[index].screen);
    mutate(screen);
    evidence.frames[index] = { ...evidence.frames[index], screen, sha256: snapshotHash(screen) };
};
/** Frame index of the nth case's read (0 before, 1 mutated, 2 restored). */
const frameOf = (position, phase) => 2 + position * 3 + phase;

test('rebuilds a complete report: ten supported conclusions, no pinned probe, no stock grade', () => {
    const evidence = syntheticTruecolorFindingEvidence();
    const report = rebuild(evidence);
    assert.equal(report.suiteId, truecolorFindingSuiteId);
    assert.equal(report.kind, 'ntilde-finding-not-a-terminfo-case');
    assert.equal(report.snapshots.length, 3 + 10 * 3);
    assert.equal(report.adapter, undefined);
    assert.ok(report.cases.every(item => item.conclusion.outcome === 'supported' && item.pinned === undefined));
    for (const item of report.cases) {
        const contract = truecolorFindingContracts.find(entry => entry.caseId === item.caseId);
        assert.deepEqual(item.feeds, truecolorFindingFeeds(contract));
        assert.deepEqual(item.conclusion.layers, { stored: 'supported', resolved: 'supported' });
        assert.deepEqual(item.bindings.map(binding => binding.paletteOverrideIndices), [[], [contract.index], []]);
        assert.equal(item.bindings[0].bindingSha256, item.bindings[2].bindingSha256, 'the palette is restored');
        assert.notEqual(item.bindings[0].bindingSha256, item.bindings[1].bindingSha256);
        assert.match(item.conclusion.scope, /rendered appearance is not claimed/);
    }
    assert.match(report.scope, /no stock grade/);
});

test('the pre-fix shape rebuilds as ten conclusive unsupported conclusions with recorded consequences', () => {
    const evidence = syntheticTruecolorFindingEvidence('a'.repeat(32), { cellsOf: contract => findingCells(contract, { aliased: true }) });
    const report = rebuild(evidence);
    assert.ok(report.cases.every(item => item.conclusion.outcome === 'unsupported'));
    for (const item of report.cases) {
        assert.equal(item.contractResult.findings.semicolon.changedByMutation, true);
        assert.equal(item.contractResult.findings.semicolon.initialMatchesRequested, false);
    }
});

test('rejects wrong fixture bytes, missing captures, reordered cases and a wrong suite', () => {
    const base = syntheticTruecolorFindingEvidence();
    const wrongBytes = structuredClone(base);
    wrongBytes.run.cases[0].feeds[1] = wrongBytes.run.cases[0].feeds[1].replace('38;5;4m', '38;5;3m');
    assert.throws(() => rebuild(wrongBytes), /do not match the pinned contract/);
    const wrongMutation = structuredClone(base);
    wrongMutation.run.cases[3].feeds[2] = '\x1b]4;7;rgb:ff/00/fe\x07';
    assert.throws(() => rebuild(wrongMutation), /do not match the pinned contract/);
    const missing = structuredClone(base);
    missing.frames.splice(4, 1);
    assert.throws(() => rebuild(missing), /Incomplete truecolor-finding snapshot sequence/);
    const missingCapture = structuredClone(base);
    missingCapture.run.cases[0].reads.pop();
    assert.throws(() => rebuild(missingCapture), /Invalid finding.truecolor-literal.fg.0-55-218 graded capture record/);
    const reordered = structuredClone(base);
    [reordered.run.cases[0], reordered.run.cases[1]] = [reordered.run.cases[1], reordered.run.cases[0]];
    assert.throws(() => rebuild(reordered), /case order differs/);
    const foreign = structuredClone(base);
    foreign.run.suiteId = 'ntilde-owned-sgr-truecolor-graded-v1';
    assert.throws(() => rebuild(foreign), /does not cover the contracted cases/);
    const probes = structuredClone(base);
    probes.run.caseIds = ['sgr.fg.truecolor', 'sgr.bg.truecolor'];
    assert.throws(() => rebuild(probes), /does not cover the contracted cases/);
});

test('rejects tampered observations, stale resolved colors, hashes, barriers and cursors', () => {
    const base = syntheticTruecolorFindingEvidence();
    const flags = structuredClone(base);
    flags.frames[frameOf(0, 0)].screen.attributeRuns[2].flags |= 0x0001;
    assert.throws(() => rebuild(flags), /Changed truecolor-finding snapshot evidence|Contradictory|coheren|Invalid/i);
    const stale = structuredClone(base);
    rehash(stale, frameOf(0, 1), screen => { screen.resolvedColors[1].fg.argb = 0xff123456; });
    assert.throws(() => rebuild(stale), /Invalid resolved-color|coheren|disagree|Contradictory/i);
    const staleLiteral = structuredClone(base);
    rehash(staleLiteral, frameOf(0, 0), screen => { screen.resolvedColors[2].fg.argb = 0xff010203; });
    assert.throws(() => rebuild(staleLiteral), /Invalid resolved-color|coheren|disagree|Contradictory/i);
    const hash = structuredClone(base);
    hash.frames[frameOf(0, 0)].sha256 = '0'.repeat(64);
    assert.throws(() => rebuild(hash), /Changed truecolor-finding snapshot evidence/);
    const barrier = structuredClone(base);
    barrier.frames[frameOf(1, 1)].barrier = null;
    assert.throws(() => rebuild(barrier), /Missing truecolor-finding ordering barrier evidence/);
    const drift = structuredClone(base);
    drift.frames[frameOf(1, 2)].barrier = '\x1b[?62;4;23c';
    assert.throws(() => rebuild(drift), /barrier differs from its read frame/);
    const cursor = structuredClone(base);
    cursor.run.cases[2].reads[1].cursor = [4, 4];
    assert.throws(() => rebuild(cursor), /cursor differs from its read frame/);
});

test('a consistently rewritten read screen cannot hide an aliased literal', () => {
    const base = syntheticTruecolorFindingEvidence();
    const forged = structuredClone(base);
    const contract = truecolorFindingContracts[0];
    for (const phase of [0, 1, 2]) {
        const aliased = syntheticTruecolorFindingEvidence('a'.repeat(32), { cellsOf: () => findingCells(contract, { aliased: true }) });
        rehash(forged, frameOf(0, phase), screen => {
            const source = aliased.frames[frameOf(0, phase)].screen;
            screen.attributeRuns = source.attributeRuns; screen.resolvedColors = source.resolvedColors; screen.cellDetails = source.cellDetails;
        });
    }
    // Stored/resolved data stay internally coherent, so the report rebuilds and honestly grades the finding.
    assert.equal(rebuild(forged).cases[0].conclusion.outcome, 'unsupported');
});

test('a palette change outside the contracted mutation voids the run; a theme change always does', () => {
    const base = syntheticTruecolorFindingEvidence();
    const unrestored = structuredClone(base);
    rehash(unrestored, unrestored.frames.length - 1, screen => { screen.colorBinding = findingBinding(truecolorFindingContracts[0], 'mutated'); });
    assert.throws(() => rebuild(unrestored), /fixture was not restored|palette was not restored/);
    const theme = structuredClone(base);
    rehash(theme, frameOf(4, 0), screen => { screen.colorBinding = { ...screen.colorBinding, themeName: 'Another Theme' }; });
    assert.throws(() => rebuild(theme), /saw the theme change/);
    // A stray palette change in a read that must be pristine does not throw but cannot grade conclusively.
    const stray = syntheticTruecolorFindingEvidence('a'.repeat(32), { bindingOf: (contract, phase) => {
        if (contract.caseId !== truecolorFindingContracts[0].caseId || phase !== 'before') return findingBinding(contract, phase);
        const palette = canonicalPalette(); palette[100] = 0xff112233; return paletteBinding({ palette });
    } });
    assert.equal(rebuild(stray).cases[0].conclusion.outcome, 'inconclusive');
});

test('an unrelated palette entry changed in every read of a case grades inconclusive through the report builder', () => {
    const persistent = syntheticTruecolorFindingEvidence('a'.repeat(32), { bindingOf: (contract, phase) => {
        const base = findingBinding(contract, phase);
        if (contract.caseId !== truecolorFindingContracts[4].caseId) return base;
        const palette = [...base.ansiPaletteArgb]; palette[100] = 0xff112233;
        return { ...base, ansiPaletteArgb: palette };
    } });
    const report = rebuild(persistent);
    assert.equal(report.cases[4].conclusion.outcome, 'inconclusive');
    assert.match(report.cases[4].conclusion.note, /baseline palette/);
    assert.equal(report.cases[3].conclusion.outcome, 'supported');
});

test('rejects mismatched run identities', () => {
    const base = syntheticTruecolorFindingEvidence();
    assert.throws(() => buildTruecolorFindingReport(base.frames, base.run, 'b'.repeat(32)), /not bound to this run/);
    assert.throws(() => validateTruecolorFindingEvidence(base.frames, base.run, 'xyz'), /not bound to this run/);
});

test('missing stored evidence refuses instead of grading', () => {
    const base = syntheticTruecolorFindingEvidence();
    for (const field of ['resolvedColors', 'cellDetails', 'attributeRuns']) {
        const missing = structuredClone(base);
        delete missing.frames[frameOf(0, 0)].screen[field];
        missing.frames[frameOf(0, 0)].sha256 = snapshotHash(missing.frames[frameOf(0, 0)].screen);
        assert.throws(() => rebuild(missing), /./, field);
    }
});
