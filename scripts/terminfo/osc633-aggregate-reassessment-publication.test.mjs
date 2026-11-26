import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { admitRequired, buildPublication, caseIds, publication } from './osc633-aggregate-reassessment.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));

test('reissues both carrier receipts and reproduces the post-983 aggregate', async () => {
    const { summary, report } = await buildPublication();
    assert.deepEqual(summary, readJson(publication, 'summary.json'));
    assert.deepEqual(report, readJson(publication, 'report.json'));
    const [row] = report.cases;
    assert.deepEqual(report.cases.map(item => item.caseId), caseIds);
    assert.equal(row.historical.outcome, 'inconclusive');
    assert.equal(row.contractResult.outcome, 'supported');
    assert.equal(row.stock.outcome, 'inconclusive');
    assert.deepEqual(row.pinned.replay.map(({ declared, termless, terminal }) => [declared, termless.observation.outcome, terminal.observation.outcome]),
        [[false, 'inconclusive', 'inconclusive'], [true, 'inconclusive', 'inconclusive']]);
    assert.equal(row.pinned.fixtureBytes, false);
    assert.deepEqual(row.evidenceContract.required.map(item => item.caseId),
        ['extensions.osc633-a', 'extensions.osc633-b', 'extensions.osc633-c', 'extensions.osc633-d', 'extensions.osc633-e', 'extensions.osc633-p']);
    assert.deepEqual(row.evidenceContract.required.map(item => item.carrierSuiteId), [
        ...Array(4).fill('ntilde-owned-osc633-graded-v1'), ...Array(2).fill('ntilde-owned-osc633-metadata-v1'),
    ]);
    assert.match(row.evidenceContract.required[5].effectContractId, /p-cwd-metadata-v1$/);
    assert.notEqual(report.carriers[0].runId, report.carriers[1].runId);
    assert.notEqual(report.carriers[0].assemblySha256, report.carriers[1].assemblySha256);
    assert.deepEqual(report.carriers.map(item => item.receiptSchema), [69, 1]);
});

test('the admission rule refuses missing metadata, failed controls, and the wrong property contract', () => {
    const lifecycle = readJson(qa, '2026-11-16-osc633-implementation/report.json');
    const metadata = readJson(qa, '2026-11-26-osc633-metadata/report.json');
    assert.equal(admitRequired(lifecycle, metadata).length, 6);
    for (const mutate of [
        copy => copy.cases.pop(),
        copy => { copy.cases[0].contractResult.controls = false; },
        copy => { copy.cases[0].contractResult.outcome = 'inconclusive'; },
        copy => { copy.cases[1].contractResult.contractId = 'other-property'; },
    ]) {
        const copy = structuredClone(metadata);
        mutate(copy);
        assert.throws(() => admitRequired(lifecycle, copy));
    }
    const failedLifecycle = structuredClone(lifecycle);
    failedLifecycle.cases[0].effectContract.validity[0].held = false;
    assert.throws(() => admitRequired(failedLifecycle, metadata));
});
