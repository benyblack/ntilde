import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { gzipSync } from 'node:zlib';
import { buildPublication, probes, replayPinned, verifyRun, withMeasurement } from './rich-identity-results.mjs';

const osc176 = 'extensions.osc176-app-id';
const osc666 = 'extensions.osc666-termprop';

test('the published callbacks and property observations retain their distinct verdicts', async () => {
    const derived = await buildPublication();
    const expected = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-27-rich-identity/report.json', import.meta.url)));
    assert.deepEqual(derived, expected);
    for (const id of [osc176, osc666]) {
        assert.equal(derived.cases[id].stock, 'inconclusive');
        assert.equal(derived.cases[id].pinned.response, '1;1');
        assert.deepEqual(derived.cases[id].pinned.observation,
            { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' });
    }
    assert.equal(derived.cases[osc176].effect, 'inapplicable');
    assert.equal(derived.cases[osc666].fixtureEffect, 'inapplicable');
    assert.equal(derived.cases[osc666].registeredPropertyEffect, 'inconclusive');
    assert.deepEqual(Object.values(derived.measured.foot).map(sample => sample.appId),
        ['qa.foot.baseline', 'terminfo-test', 'qa.foot.baseline', 'terminfo-test', 'qa.foot.baseline']);
    assert.deepEqual(Object.values(derived.measured.vte.samples).map(sample => sample.containerName),
        [null, null, null, null, 'ntilde-qa', 'ntilde-qa', null]);
});

test('pinned replay checks its bundle and exact bytes, preserving an absent CPR verdict', async () => {
    assert.throws(() => probes(gzipSync('replacement')), /bundle digest/);
    const { pinned666 } = probes();
    const writes = ['\x1b[1;1H\x1b[2K', '\x1b]666;test-prop=value\x07', '\x1b[6n'];
    const result = await replayPinned(pinned666, writes, null);
    assert.equal(result.observation.outcome, 'inconclusive');
    assert.equal(result.observation.reason, 'no-response');
    writes[1] = '\x1b]666;test-prop=value\x1b\\';
    await assert.rejects(replayPinned(pinned666, writes, null), /exact pinned write order/);
});

const corruptions = [
    ['change a target write', run => { run.app.samples.request176.exchange.writes[0] = '00'; }, /changed PTY writes/],
    ['lose the DA fence', run => { run.app.samples.baseline.exchange.chunks.at(-1).hex = '1b'; }, /foreground state control|DA delivery fence|cursor report/],
    ['remove the independent color control', run => {
        run.app.samples.control.exchange.chunks[0].hex = run.app.samples.baseline.exchange.chunks[0].hex;
    }, /foreground state control/],
    ['admit the App before its layout is arranged', run => { run.app.arranged.height = 773; }, /missing compositor-sized App viewport/],
    ['substitute a different disposable window', run => { run.app.admission.id++; }, /disposable window changed/],
    ['ignore a foot set', run => {
        const pending = [run.foot.samples.request176.tree];
        while (pending.length) {
            const current = pending.pop();
            if (current.id === run.foot.admission.id) current.app_id = 'qa.foot.baseline';
            pending.push(...current.nodes, ...current.floating_nodes);
        }
    }, /foot compositor positive control/],
    ['break the registered property observer', run => { run.vte.samples.request666.containerName = null; }, /VTE observer bytes differ/],
    ['skip PTY restoration', run => { run.app.termios.after[0]++; }, /PTY settings were not restored/],
    ['leave a display socket', run => { run.socketsGone[Object.keys(run.socketsGone)[0]] = false; }, /display sockets remain/],
];

for (const [name, mutate, reason] of corruptions) {
    test('offline replay refuses to ' + name, () => withMeasurement(async directory => {
        const path = join(directory, 'observation.json');
        const run = JSON.parse(readFileSync(path));
        mutate(run);
        writeFileSync(path, JSON.stringify(run));
        await assert.rejects(verifyRun(directory), reason);
    }));
}

test('offline replay refuses a changed collector or incomplete desktop lease', () => withMeasurement(async directory => {
    const source = join(directory, 'sources', 'plan.json');
    const unchanged = readFileSync(source);
    writeFileSync(source, '{}');
    await assert.rejects(verifyRun(directory), /collector source binding/);
    writeFileSync(source, unchanged);
    const audit = join(directory, 'desktop-lease.json');
    const events = JSON.parse(readFileSync(audit));
    writeFileSync(audit, JSON.stringify(events.slice(0, -1)));
    await assert.rejects(verifyRun(directory), /incomplete desktop lease cleanup/);
}));

test('offline replay refuses a different App build revision', () => withMeasurement(async directory => {
    const path = join(directory, 'identity.json');
    const identity = JSON.parse(readFileSync(path));
    identity.appSourceRevision = 'a'.repeat(40);
    writeFileSync(path, JSON.stringify(identity));
    await assert.rejects(verifyRun(directory), /App embedded revision differs/);
}));
