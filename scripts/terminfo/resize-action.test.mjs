import test from 'node:test';
import assert from 'node:assert/strict';
import { resizeOwnedPane } from './resize-action.mjs';
const paneId = '11111111-1111-1111-1111-111111111111', runId = 'a'.repeat(32);
function fixture() {
    const steps = [], config = { app: { pid: 42, exitCode: null, signalCode: null }, appData: '/private/fixture',
        executable: '/owned/app', workerPid: 43, uid: 1000, tty: '/dev/ttys100', paneId, runId };
    let worker = { pid: 43, ppid: 42, uid: 1000, tty: 'ttys100', executable: '/node' };
    const partial = { rows: 21, cols: 71, layoutSettled: false, exact: false };
    const dependencies = {
        record: pid => { steps.push('process'); return pid === 42 ? { pid: 42, uid: 1000, executable: '/owned/app' } : worker; },
        command: () => { steps.push('marker'); return `/owned/app --ntilde-terminfo-owner=${runId}`; },
        canonical: value => value,
        endpoint: (path, pid) => { assert.equal(path, config.appData); assert.equal(pid, 42); steps.push('endpoint'); return 'private'; },
        rpc: async (endpoint, pane, rows, cols) => {
            assert.equal(endpoint, 'private'); assert.equal(pane, paneId); assert.deepEqual([rows, cols], [20, 70]);
            steps.push('resize'); return partial;
        },
        outputMode: tty => { assert.equal(tty, config.tty); steps.push('kernel'); return { tool: '/bin/stty', outputProcessing: false, description: '-opost' }; },
    };
    return { config, dependencies, steps, partial, worker: value => { worker = { ...worker, ...value }; } };
}
test('only a proved target is resized, re-proved and independently observed; partial App facts remain partial', async () => {
    const f = fixture(), result = await resizeOwnedPane(f.config, 20, 70, () => 1000, f.dependencies);
    assert.deepEqual(f.steps, ['process', 'process', 'marker', 'endpoint', 'resize', 'process', 'process', 'marker', 'kernel']);
    assert.deepEqual(result.app, f.partial); assert.equal(result.paneId, paneId); assert.equal(result.runId, runId);
    assert.equal(new Date(result.observedAt).toISOString(), result.observedAt);
});
for (const [name, worker] of [['another parent', { ppid: 99 }], ['another user', { uid: 99 }], ['another TTY', { tty: 'ttys200' }]]) {
    test(name + ' cannot cause a resize', async () => {
        const f = fixture(); f.worker(worker);
        await assert.rejects(resizeOwnedPane(f.config, 20, 70, () => 1000, f.dependencies));
        assert.ok(!f.steps.includes('resize')); assert.ok(!f.steps.includes('kernel'));
    });
}
test('loss of ownership during the App action refuses a kernel observation', async () => {
    const f = fixture(); f.dependencies.rpc = async () => { f.worker({ ppid: 99 }); return f.partial; };
    await assert.rejects(resizeOwnedPane(f.config, 20, 70, () => 1000, f.dependencies)); assert.ok(!f.steps.includes('kernel'));
});
test('an exited App cannot cause any process query or action', async () => {
    const f = fixture(); f.config.app.signalCode = 'SIGTERM';
    await assert.rejects(resizeOwnedPane(f.config, 20, 70, () => 1000, f.dependencies)); assert.deepEqual(f.steps, []);
});
