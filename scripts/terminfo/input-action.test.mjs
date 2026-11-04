import test from 'node:test';
import assert from 'node:assert/strict';
import { generateOwnedInput } from './input-action.mjs';
const paneId = '11111111-1111-1111-1111-111111111111', runId = 'a'.repeat(32);
function fixture() {
    const steps = [], config = { app: { pid: 42, exitCode: null, signalCode: null }, appData: '/private/fixture',
        executable: '/owned/app', image: { ino: 1, size: 20, mtimeMs: 10 }, workerPid: 43, uid: 1000,
        tty: '/dev/ttys100', paneId, profileName: 'fixture', runId };
    const worker = { pid: 43, ppid: 42, uid: 1000, tty: 'ttys100', executable: '/node' };
    config.workerIdentity = structuredClone(worker);
    let sessions = [{ paneId, kind: 'local', windowless: false, rows: 12, cols: 120, profileName: 'fixture' }];
    const partial = { eventDispatched: true, inputSubmitted: true, observationAvailable: false, handled: true };
    const dependencies = {
        image: () => config.image, canonical: value => value,
        record: pid => { steps.push('process'); return pid === 42 ? { pid: 42, uid: 1000, executable: '/owned/app' } : worker; },
        command: pid => { steps.push('marker'); return pid === 42 ? `/owned/app --ntilde-terminfo-owner=${runId}` : `/node --ntilde-terminfo-worker=${runId}`; },
        endpoint: () => 'private',
        read: async (_, method) => { steps.push(method); return { sessions }; },
        outputMode: () => { steps.push('kernel'); return { tool: '/bin/stty', outputProcessing: false, description: '12 rows; 120 columns; -opost' }; },
        rpc: async (_, request) => { steps.push('event'); assert.deepEqual(request, { paneId, kind: 'keyDown', key: '7', modifiers: 0 }); return partial; },
    };
    return { config, worker, partial, dependencies, steps, setSessions: value => { sessions = value; } };
}
const event = { kind: 'keyDown', key: '7', modifiers: 0 };
test('exact process marker,worker,UID,PTY,pane and output are proved around one action; partial facts are retained', async () => {
    const f = fixture(), result = await generateOwnedInput(f.config, event, () => 1000, f.dependencies);
    assert.deepEqual(f.steps, ['process', 'process', 'marker', 'marker', 'listSessions', 'kernel', 'event', 'process', 'process', 'marker', 'marker', 'listSessions', 'kernel']);
    assert.deepEqual(result.app, f.partial); assert.equal(result.postVerified, true); assert.equal(result.rpcStarted, true);
    assert.equal(result.request.paneId, paneId); assert.equal(result.app.observationAvailable, false);
});
for (const [name, mutate] of [
    ['another worker parent', f => f.worker.ppid++], ['another UID', f => f.worker.uid++],
    ['another PTY', f => f.worker.tty = 'ttys101'],
    ['another worker executable', f => f.worker.executable = '/another/node'],
    ['missing worker run marker', f => { const command = f.dependencies.command; f.dependencies.command = pid => pid === 42 ? command(pid) : '/node'; }],
    ['another image', f => f.dependencies.image = () => ({ ...f.config.image, size: 21 })],
    ['exited App', f => f.config.app.exitCode = 0],
    ['multiple panes', f => f.setSessions([{}, {}])], ['remote pane', f => f.setSessions([{ paneId, kind: 'remote' }])],
]) test(name + ' cannot dispatch an action', async () => {
    const f = fixture(); mutate(f);
    await assert.rejects(generateOwnedInput(f.config, event, () => 1000, f.dependencies), error => {
        assert.equal(error.inputAttempt.rpcStarted, false); assert.equal(error.inputAttempt.app, null); return true;
    }); assert.ok(!f.steps.includes('event'));
});
test('post-action ownership loss preserves actual submitted facts and never retries', async () => {
    const f = fixture(); let calls = 0;
    f.dependencies.rpc = async () => { calls++; f.worker.ppid++; return f.partial; };
    await assert.rejects(generateOwnedInput(f.config, event, () => 1000, f.dependencies), error => {
        assert.equal(error.inputAttempt.rpcStarted, true); assert.deepEqual(error.inputAttempt.app, f.partial);
        assert.equal(error.inputAttempt.postVerified, false); return true;
    }); assert.equal(calls, 1);
});
test('disconnect or shared-deadline exhaustion retains the started request without claiming no dispatch', async () => {
    for (const fail of [() => { throw new Error('disconnect'); }, () => Promise.reject()]) {
        const f = fixture(); f.dependencies.rpc = fail;
        await assert.rejects(generateOwnedInput(f.config, event, () => 1000, f.dependencies), error => {
            assert.equal(error.inputAttempt.rpcStarted, true); assert.deepEqual(error.inputAttempt.request, { ...event, paneId });
            assert.equal(error.inputAttempt.app, null); return true;
        });
    }
});
test('shared deadline exhausted before the RPC never marks or performs a started action', async () => {
    const f = fixture(); let calls = 0;
    await assert.rejects(generateOwnedInput(f.config, event, () => {
        if (++calls === 6) throw new Error('shared deadline exhausted');
        return 1000;
    }, f.dependencies), error => {
        assert.equal(error.inputAttempt.rpcStarted, false); assert.equal(error.inputAttempt.app, null); return true;
    });
    assert.ok(!f.steps.includes('event'));
});
