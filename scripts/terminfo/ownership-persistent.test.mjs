import assert from 'node:assert/strict';
import { test } from 'node:test';
import { assertPersistentDaemon, assertProcessOwnership, discoverPersistentDaemon, environmentValue, readPersistentDaemon, terminateRecordedDaemon } from './ownership.mjs';

const runId = 'a'.repeat(32), appData = '/private/ntilde-qa/nt-abc123/appdata', executable = '/sidecar/Ntilde';
const daemonFacts = () => ({
    record: { pid: 300, ppid: 1, uid: 501, tty: '??', executable },
    text: `${executable} mux serve --idle-exit-minutes 10 NTILDE_APPDATA_ROOT=${appData} TERMINFO_RUN_ID=${runId} HOME=/Users/test`,
    birth: 'Tue Nov 17 16:07:30 2026', appBirth: 'Tue Nov 17 16:07:29 2026',
    descriptor: { pid: 300, processName: 'Ntilde', endpoint: appData + '/mux/sock' },
});
const persistentProof = () => ({
    app: { pid: 100, uid: 501, executable }, worker: { pid: 301, ppid: 300, uid: 501, tty: 'ttys012' },
    ownerPid: 100, uid: 501, executable, runId, appCommand: `${executable} --ntilde-terminfo-owner=${runId}`, tty: '/dev/ttys012',
    daemon: daemonFacts(), appData,
});

test('environmentValue reads one exact NAME=value token and nothing ambiguous', () => {
    assert.equal(environmentValue('cmd A=1 B=2', 'A'), '1');
    assert.equal(environmentValue('cmd A=1 A=2', 'A'), undefined);
    assert.equal(environmentValue('cmd XA=1', 'A'), undefined);
    assert.equal(environmentValue('cmd', 'A'), undefined);
});

test('accepts the worker of the owned multiplexer daemon', () => {
    assert.doesNotThrow(() => assertProcessOwnership(persistentProof()));
});

test('a persistent worker must not be mistaken for a direct child of the app', () => {
    const proof = persistentProof();
    delete proof.daemon;
    assert.throws(() => assertProcessOwnership(proof), /direct child of the owned app/);
});

for (const [name, change, message] of [
    ['a worker under another parent', p => { p.worker.ppid = 999; }, /owned multiplexer daemon/],
    ['a daemon of another user', p => { p.daemon.record.uid = 502; }, /another user/],
    ['another executable', p => { p.daemon.record.executable = '/other/Ntilde'; }, /launched executable/],
    ['a command that is not mux serve', p => { p.daemon.text = p.daemon.text.replace('mux serve', 'mux ls'); }, /launched executable/],
    ['no serve arguments', p => { p.daemon.text = `${executable} mux`; }, /launched executable/],
    ['another app-data root', p => { p.daemon.text = p.daemon.text.replace(appData, '/private/ntilde-qa/nt-other/appdata'); }, /environment differs/],
    ['another run identity', p => { p.daemon.text = p.daemon.text.replace(runId, 'b'.repeat(32)); }, /environment differs/],
    ['a repeated root', p => { p.daemon.text += ' NTILDE_APPDATA_ROOT=' + appData; }, /environment differs/],
    ['a descriptor naming another pid', p => { p.daemon.descriptor.pid = 301; }, /descriptor/],
    ['a descriptor endpoint outside the private root', p => { p.daemon.descriptor.endpoint = '/tmp/elsewhere/sock'; }, /descriptor/],
    ['a descriptor endpoint beside the root', p => { p.daemon.descriptor.endpoint = appData + '-sibling/sock'; }, /descriptor/],
    ['a daemon older than the app', p => { p.daemon.birth = 'Tue Nov 17 16:07:28 2026'; }, /predates/],
    ['an unreadable birth', p => { p.daemon.birth = 'garbage'; }, /predates/],
]) {
    test(`refuses ${name}`, () => {
        const proof = persistentProof();
        change(proof);
        assert.throws(() => assertProcessOwnership(proof), message);
    });
}

test('assertPersistentDaemon is the only place the daemon relation is decided', () => {
    const { app, worker, daemon } = persistentProof();
    assert.doesNotThrow(() => assertPersistentDaemon({ app, worker, daemon, executable, uid: 501, runId, appData }));
    assert.throws(() => assertPersistentDaemon({ app, worker, daemon, executable: '/other', uid: 501, runId, appData }), /launched executable/);
});

test('readPersistentDaemon refuses a descriptor that is not a regular file', () => {
    const facts = { record: () => ({ pid: 300, ppid: 1, uid: 501, tty: '??', executable }), birth: () => 'Tue Nov 17 16:07:30 2026',
        canonical: path => path, run: () => 'text', status: () => ({ isFile: () => false, isSymbolicLink: () => true }), read: () => '{}' };
    assert.throws(() => readPersistentDaemon(300, 100, appData, 1000, facts), /regular file/);
});

test('readPersistentDaemon returns the daemon facts and the descriptor it wrote', () => {
    const facts = { record: () => ({ pid: 300, ppid: 1, uid: 501, tty: '??', executable }), birth: pid => (pid === 300 ? 'Tue Nov 17 16:07:30 2026' : 'Tue Nov 17 16:07:29 2026'),
        canonical: path => path, run: () => `${executable} mux serve x NTILDE_APPDATA_ROOT=${appData}\n`, status: () => ({ isFile: () => true, isSymbolicLink: () => false }),
        read: () => JSON.stringify({ pid: 300, processName: 'Ntilde', endpoint: appData + '/mux/sock' }) };
    const facts2 = readPersistentDaemon(300, 100, appData, 1000, facts);
    assert.deepEqual(facts2.descriptor, { pid: 300, processName: 'Ntilde', endpoint: appData + '/mux/sock' });
    assert.equal(facts2.birth, 'Tue Nov 17 16:07:30 2026');
    assert.equal(facts2.appBirth, 'Tue Nov 17 16:07:29 2026');
});

test('cleanup treats an absent daemon as already stopped and never asks it to stop', async () => {
    let asked = false;
    await terminateRecordedDaemon(undefined, { executable, appData, runId }, { stop: () => { asked = true; } });
    await terminateRecordedDaemon({ pid: 2 ** 22 + 12345, uid: 501, executable }, { executable, appData, runId }, { stop: () => { asked = true; } });
    assert.equal(asked, false);
});

test('cleanup refuses to signal a live process that is not this run\'s daemon', async () => {
    let asked = false;
    await assert.rejects(terminateRecordedDaemon({ pid: process.pid, uid: process.getuid(), executable: process.execPath }, { executable: process.execPath, appData, runId },
        { stop: () => { asked = true; } }), /identity changed/);
    assert.equal(asked, false);
});

test('a daemon is discovered only from the private root\'s own descriptor, and an absent or malformed one is no candidate', () => {
    const record = pid => {
        if (!Number.isSafeInteger(pid)) throw new Error('Invalid process ID.');
        return { pid, ppid: 1, uid: 501, tty: '??', executable };
    };
    assert.deepEqual(discoverPersistentDaemon(appData, executable, { read: () => JSON.stringify({ pid: 300 }), record }), { pid: 300, uid: 501, executable });
    assert.equal(discoverPersistentDaemon(appData, executable, { read: () => { throw new Error('ENOENT'); }, record }), undefined);
    assert.equal(discoverPersistentDaemon(appData, executable, { read: () => 'not json', record }), undefined);
    assert.equal(discoverPersistentDaemon(appData, executable, { read: () => JSON.stringify({ pid: 'x' }), record }), undefined);
});
