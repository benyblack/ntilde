import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { gzipSync, gunzipSync } from 'node:zlib';
import { benchmarkSuites, loadPublication, pinnedVerdictOf, effectVerdictOf } from './qualification-inventory.mjs';
import { aggregateCaseIds, aggregateEvidenceSets, aggregatePinnedNotes } from './shell-integration-aggregates-contracts.mjs';
import { aggregateSourceDigests, replayAggregatePinned, shellIntegrationAggregatesPublication, upstreamBundleSha256 } from './shell-integration-aggregates-publication.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-shell-integration-aggregates/', import.meta.url));
const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = (root, name) => readFileSync(join(root, name));
const json = (root, name) => JSON.parse(read(root, name));

test('the projection independently rederives both carrier bindings and the retained report and summary', async () => {
    const retained = { summary: json(dir, 'summary.json'), report: json(dir, 'report.json') };
    const projected = await shellIntegrationAggregatesPublication();
    assert.deepEqual(projected.summary, retained.summary);
    assert.deepEqual(projected.report, retained.report);
    // Each carrier binding is pinned to the committed bytes of the admitted publication it names.
    const [osc133, osc633] = retained.summary.projection.carriers;
    assert.equal(hash(read(join(qa, '2026-11-11-osc133/'), 'summary.json')), osc133.summarySha256);
    assert.equal(hash(read(join(qa, '2026-11-16-osc633-implementation/'), 'summary.json')), osc633.summarySha256);
    assert.equal(osc633.archiveSha256, json(join(qa, '2026-11-16-osc633-implementation/'), 'manifest.json').archives['owned-run.zip']);
    assert.equal(osc133.receiptSchemaVersion, 45);
    assert.equal(osc633.receiptSchemaVersion, 69);
    // Every admitted outcome is pinned to the exact carrier bytes it was read from.
    const evidence = Object.entries(osc133.admittedEvidence).concat(Object.entries(osc633.admittedEvidence));
    assert.equal(evidence.length, 6, 'four OSC 133 case files, their journal header and the OSC 633 report');
    for (const [rel, digest] of evidence) assert.equal(hash(read(qa, rel)), digest, rel);
    // The report names the OSC 633 grading adapter, not one inherited through the context fallback.
    assert.equal(retained.report.adapter.id, 'ntilde-owned-osc633-grading-v1');
    assert.deepEqual(retained.report.adapter, osc633.gradingAdapter);
    // Contract equivalence: every admitted claim is exactly the carrier's retained claim for the same
    // effect contract, and the frozen historical negative stays untouched beside them.
    assert.deepEqual(osc133.admitted, ['a', 'b', 'c', 'd'].map(letter => ({ caseId: `extensions.osc133-${letter}`, outcome: 'supported' })));
    assert.deepEqual(osc633.admitted, ['a', 'b', 'c', 'd'].map(letter => ({ caseId: `extensions.osc633-${letter}`, outcome: 'supported' })));
    assert.equal(osc133.effectContractId, 'ntilde-osc133-shell-lifecycle-effect-v1');
    assert.equal(osc633.effectContractId, 'ntilde-osc633-shell-lifecycle-effect-v1');
    // The two dialect contracts share one lineage: the OSC 633 grading adapter derives from the OSC 133 one.
    assert.equal(osc633.gradingAdapter.derivedFromAdapterId, osc133.gradingAdapter.id);
    assert.equal(osc633.gradingAdapter.upstreamBundleSha256, osc133.gradingAdapter.upstreamBundleSha256);
    const historical = json(join(qa, '2026-11-16-osc633/'), 'report.json');
    for (const item of historical.cases) assert.equal(item.contractResult.outcome, 'unsupported', item.caseId);
    // The projection reuses the primary carrier's receipt and stock counts without minting new ones.
    const carrierSummary = json(join(qa, '2026-11-16-osc633-implementation/'), 'summary.json');
    assert.deepEqual(retained.summary.receipt, carrierSummary.receipt);
    assert.equal(retained.summary.ownedRunId, carrierSummary.ownedRunId);
    assert.equal(retained.summary.stockCollector.rawSha256, carrierSummary.stockCollector.rawSha256);
    assert.match(retained.summary.stockCollector.note, /collects nothing and alters no grade/);
    assert.equal(retained.summary.stockCollectorRunId ?? null, null, 'the projection claims no stock run of its own');
});

test('the pinned aggregate paths replay to exactly their registered inconclusive results under both declared values', async () => {
    for (const caseId of aggregateCaseIds) {
        for (const declared of [false, true]) {
            const { termless, terminal, registrationSha256 } = await replayAggregatePinned(caseId, declared);
            const notes = aggregatePinnedNotes[caseId];
            assert.deepEqual(termless, {
                pass: false, response: `{"declared":${declared}}`, note: notes.termless,
                observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: notes.termless },
            });
            assert.deepEqual(terminal, {
                pass: false, note: notes.terminal,
                observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'none', note: notes.terminal },
            });
            assert.match(registrationSha256, /^[0-9a-f]{64}$/);
        }
    }
    const bundle = read(dir, 'terminfo-dev-4.0.0.bundle.mjs.gz');
    assert.equal(hash(gunzipSync(bundle)), upstreamBundleSha256);
    // A tampered bundle content is refused before any replay.
    const doctored = gzipSync(gunzipSync(bundle).toString('utf8').replace('insufficient-evidence', 'insufficient-evidenc'), { mtime: 0 });
    assert.throws(() => replayAggregatePinned(aggregateCaseIds[0], false, doctored), /upstream bundle differs/);
    assert.throws(() => replayAggregatePinned(aggregateCaseIds[0], false, Buffer.from('not a bundle')), /.+/, 'a corrupt archive cannot replay');
});

test('the retained report keeps the pinned, effect, stock and upstream outcomes distinct per aggregate', async () => {
    const report = json(dir, 'report.json');
    assert.deepEqual(report.cases.map(item => item.caseId), aggregateCaseIds);
    for (const item of report.cases) {
        assert.equal(pinnedVerdictOf(item), 'inconclusive');
        assert.equal(item.pinned.fixtureBytes, false);
        assert.deepEqual(item.pinned.replay.declaredVariants, [{ declared: false, response: '{"declared":false}' }, { declared: true, response: '{"declared":true}' }]);
        assert.equal(item.evidenceContract.version, 1);
        assert.match(item.contractResult.attribution, /not the pinned probe, not a marker claim, not an upstream or common-harness verdict/);
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.grading, 'aggregate-pinned-replay-preserved-by-design');
    }
    const [, semanticPrompts] = report.cases;
    const [osc633] = report.cases;
    // The OSC 633 aggregate stays inconclusive with its pending records named; verdicts agree, so no divergence exists.
    assert.equal(effectVerdictOf(osc633), 'inconclusive');
    assert.equal(osc633.divergence, undefined);
    assert.deepEqual(osc633.evidenceContract.pending.map(entry => [entry.caseId, entry.owner]),
        [['extensions.osc633-e', '#983'], ['extensions.osc633-p', '#983']]);
    assert.deepEqual(osc633.evidenceContract.required.filter(entry => entry.admittedOutcome === null).map(entry => entry.caseId),
        ['extensions.osc633-e', 'extensions.osc633-p']);
    assert.match(osc633.contractResult.reason, /#983/);
    // The semantic-prompts aggregate closes supported over the four admitted OSC 133 lifecycle claims, with the divergence recorded.
    assert.equal(effectVerdictOf(semanticPrompts), 'supported');
    assert.deepEqual(semanticPrompts.divergence, { kind: 'pinned-inconclusive-by-design', pinnedOutcome: 'inconclusive', contractOutcome: 'supported' });
    assert.deepEqual(semanticPrompts.evidenceContract.required.map(entry => entry.admittedOutcome), ['supported', 'supported', 'supported', 'supported']);
    assert.deepEqual(semanticPrompts.evidenceContract.pending, []);
    assert.match(semanticPrompts.evidenceContract.excluded[0].reason, /\(#985\)/);
    // No upstream, stock, Core or common-harness claim anywhere in the scope.
    assert.match(report.scope, /never an upstream supported verdict/);
    assert.match(report.scope, /no stock, Core, Modern or common-harness claim/);
    assert.match(report.scope, /#983/);
});

test('the suite is registered with the exact retained layout and the evidence set matches the live contracts', async () => {
    const suite = benchmarkSuites.find(entry => entry.suite === 'shell-integration-aggregates');
    assert.ok(suite, 'registered');
    assert.equal(suite.dir, '2026-11-16-shell-integration-aggregates');
    assert.equal(suite.receiptSuiteId, 'ntilde-owned-osc633-graded-v1');
    const { report, summary } = loadPublication(qa, suite);
    assert.deepEqual(report.cases.map(item => [item.caseId, pinnedVerdictOf(item), effectVerdictOf(item)]),
        [['extensions.osc-633-vscode', 'inconclusive', 'inconclusive'], ['extensions.semantic-prompts', 'inconclusive', 'supported']]);
    assert.deepEqual(summary.projection.caseIds, [...aggregateCaseIds]);
    for (const [caseId, set] of Object.entries(aggregateEvidenceSets)) {
        const item = report.cases.find(entry => entry.caseId === caseId);
        assert.equal(item.evidenceContract.id, set.effectContractId, caseId);
        assert.deepEqual(item.evidenceContract.required.map(entry => entry.caseId), set.required.map(entry => entry.caseId), caseId);
    }
});

test('the projection refuses carrier mutations even when the retained outcomes are unchanged', () => {
    const withRoot = body => {
        const root = mkdtempSync(join(tmpdir(), 'nt-aggregate-mutated-'));
        try {
            cpSync(join(qa, '2026-11-11-osc133'), join(root, '2026-11-11-osc133'), { recursive: true });
            cpSync(join(qa, '2026-11-16-osc633-implementation'), join(root, '2026-11-16-osc633-implementation'), { recursive: true });
            return body(root);
        } finally { rmSync(root, { recursive: true, force: true }); }
    };
    // Control: a faithful copy of both carrier directories rederives the retained projection exactly.
    const faithful = withRoot(root => shellIntegrationAggregatesPublication(root, aggregateSourceDigests(qa)));
    assert.deepEqual(faithful.summary, json(dir, 'summary.json'));
    assert.deepEqual(faithful.report, json(dir, 'report.json'));
    const mutate = (change, pattern) => withRoot(root => {
        change(root);
        assert.throws(() => shellIntegrationAggregatesPublication(root, aggregateSourceDigests(qa)), pattern);
    });
    // An OSC 133 case file altered while its admitted outcome stays supported is refused by its pinned digest.
    mutate(root => {
        const rel = join('2026-11-11-osc133', 'case-extensions.osc133-a.json');
        const item = JSON.parse(readFileSync(join(root, rel), 'utf8'));
        assert.equal(item.contractResult.outcome, 'supported');
        item.marker = 'tampered';
        writeFileSync(join(root, rel), JSON.stringify(item, null, 2) + '\n');
    }, /carrier osc133 evidence differs from the pinned expectation/);
    // The carrier summary (which carries its receipt) cannot change either.
    mutate(root => {
        const rel = join('2026-11-11-osc133', 'summary.json');
        const item = JSON.parse(readFileSync(join(root, rel), 'utf8'));
        delete item.receipt;
        writeFileSync(join(root, rel), JSON.stringify(item, null, 2) + '\n');
    }, /carrier osc133 summary differs from the pinned expectation/);
    // Nor the OSC 633 report whose outcomes the projection admits.
    mutate(root => {
        const rel = join('2026-11-16-osc633-implementation', 'report.json');
        const item = JSON.parse(readFileSync(join(root, rel), 'utf8'));
        item.cases[0].contractResult = { outcome: 'unsupported' };
        writeFileSync(join(root, rel), JSON.stringify(item, null, 2) + '\n');
    }, /carrier osc633 evidence differs from the pinned expectation/);
    // The caller must pin every file the projection admits from: an unpinned expectation is refused.
    const unpinned = aggregateSourceDigests(qa);
    delete unpinned.osc133.admittedEvidence[join('2026-11-11-osc133', 'journal-common.json')];
    assert.throws(() => shellIntegrationAggregatesPublication(qa, unpinned), /is not pinned by the caller/);
});
