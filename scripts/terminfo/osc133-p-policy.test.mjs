// Audit the exact upstream probe and retained observations; no new effect grade.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { adaptCollector } from './collector.mjs';
import { buildMetadataReport } from './metadata.mjs';

const fixture = '\x1b]133;P;Cwd=/tmp\x07X';
const result = consumed => {
    const note = consumed === null ? 'No cursor reply after semantic prompt marker'
        : consumed ? 'Marker was consumed; shell prompt integration was not observed'
            : 'Marker consumption was not confirmed; shell prompt integration was not observed';
    return { pass: false, note, observation: { outcome: 'inconclusive',
        evidence: consumed === null ? 'query' : 'consumed',
        reason: consumed === null ? 'no-response' : 'insufficient-evidence', note } };
};
const bounded = methods => new Proxy(methods, { get(target, key) {
    assert.ok(Object.hasOwn(target, key), `unexpected context read: ${String(key)}`);
    return target[key];
} });

testWithPinnedCollector('OSC 133 P pinned paths test consumption only, including mismatch and missing-reply controls', async bundleUrl => {
    const source = readFileSync(bundleUrl, 'utf8');
    const derived = adaptCollector(source, fileURLToPath(new URL('./ownership.mjs', import.meta.url)))
        + '\ninit_src();\nexport const auditedProbe = ALL_PROBES.find(item => item.id === "extensions.osc133-p");\n';
    const file = new URL(`./ntilde-osc133p-policy-${process.pid}.mjs`, bundleUrl);
    try {
        writeFileSync(file, derived);
        const { auditedProbe } = await import(file.href);
        assert.equal(auditedProbe.id, 'extensions.osc133-p');
        assert.equal(auditedProbe.termlessObservationEvidence, 'consumed');
        for (const char of ['X', 'Y']) {
            const calls = [];
            const ctx = bounded({ feed(bytes) { calls.push(['feed', bytes]); },
                getCell(row, col) { calls.push(['cell', row, col]); return { char }; } });
            assert.deepEqual(auditedProbe.termless(ctx), result(char === 'X'));
            assert.deepEqual(calls, [['feed', fixture], ['cell', 0, 0]]);
        }
        for (const position of [{ row: 1, col: 2 }, { row: 1, col: 3 }, null]) {
            const calls = [];
            const ctx = bounded({ write(bytes) { calls.push(['write', bytes]); },
                async queryCursorPosition() { calls.push(['query']); return position; } });
            assert.deepEqual(await auditedProbe.term(ctx), result(position === null ? null : position.col === 2));
            assert.deepEqual(calls, [['write', '\x1b[1;1H\x1b[2K'], ['write', fixture], ['query']]);
        }
    } finally { rmSync(file, { force: true }); }
});

test('retained P observations preserve distinguishable cwd/lifecycle controls, inconclusives and prepared restoration', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-metadata-snapshot.json', import.meta.url)));
    const report = buildMetadataReport(data.metadataReport.snapshots, data.context.runId);
    assert.deepEqual(report, data.metadataReport);
    assert.equal(data.appBuildSource, '1f4403f222ee0095d9b2d8a486009fd38254ed71');
    assert.equal(data.context.runId, '4fd48c78bc53c5e4b3835cfa96eda5ba');
    const frames = report.snapshots;
    const observation = id => report.observations.find(item => item.featureId === id);
    const cwd = observation('metadata.cwd.osc7');
    assert.equal(cwd.result, 'passed');
    const cwdRead = frames[cwd.assertions.find(item => item.stageId === 'observed').snapshot].before.metadata;
    assert.equal(cwdRead.workingDirectory, '/ntilde-qa/metadata owned/' + data.context.runId);
    assert.equal(observation('metadata.shell.lifecycle').result, 'passed');
    for (const [id, sequence] of [['metadata.shell.P.stock', fixture.slice(0, -1)],
        ['metadata.shell.P.prompt-part', '\x1b]133;P;k=r\x07']]) {
        const item = observation(id), input = report.inputs.find(item => item.id === id);
        assert.equal(input.stages.find(stage => stage.id === 'observed').sequence, sequence);
        assert.equal(item.result, 'inconclusive');
        assert.equal(item.control.passed, true);
        assert.ok(item.assertions.every(stage => stage.passed));
        const control = frames[item.control.snapshot].before;
        assert.equal(control.metadata.workingDirectory, '/ntilde-qa/metadata-base');
        assert.notEqual(control.metadata.workingDirectory, '/tmp');
        for (const stage of item.assertions) {
            const read = frames[stage.snapshot].before;
            assert.deepEqual(read.metadata, control.metadata);
            assert.deepEqual(read.status, control.status);
            assert.deepEqual(read.screen, control.screen);
        }
    }
    assert.equal(data.receipt.fixtureRemoved, true);
    assert.equal(data.receipt.schemaVersion, 8);
    assert.deepEqual(frames[report.restoredSnapshot].before.screen, frames[report.initialSnapshot].before.screen);
    assert.equal(frames[report.restoredSnapshot].before.metadata.workingDirectory, '/ntilde-qa/metadata-base');
    assert.deepEqual(data.receipt.metadataEvidence.counts, { passed: 10, inconclusive: 2, failed: 1 });
});
