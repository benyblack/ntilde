import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import {
    spawnCommonCoreApplication,
    commonCoreCollectionReady,
    assertCommonCoreLaunchKnown,
} from './common-core-process.mjs';
test('an asynchronous startup failure is observed without an unhandled error or false spawn witness', async () => {
    const state = spawnCommonCoreApplication(process.execPath + '.missing-file', [], { stdio: 'ignore' });
    await once(state.proc, 'error');
    assert.equal(state.spawned, false);
    assert.equal(state.error.code, 'ENOENT');
});
test('a real directly launched process has a spawn witness and exits normally', async () => {
    const state = spawnCommonCoreApplication(process.execPath, ['-e', 'process.exit(0)'], { stdio: 'ignore' });
    const [code] = await once(state.proc, 'exit');
    assert.equal(code, 0);
    assert.equal(state.spawned, true);
    assert.equal(state.error, null);
});

test('collection refuses a vanished worker promptly but accepts its already-published completion', () => {
    const pending = { completed: () => false, failure: () => null, running: () => false };
    assert.throws(() => commonCoreCollectionReady(pending), /worker exited/);
    assert.equal(commonCoreCollectionReady({ ...pending, completed: () => true }), true);
    assert.equal(commonCoreCollectionReady({ ...pending, running: () => true }), false);
    const failure = new Error('retained worker error');
    assert.throws(
        () => commonCoreCollectionReady({ ...pending, failure: () => failure }),
        (error) => error === failure,
    );
});
test('an unknown attempted launch cannot authorize releasing the shared lock', () => {
    assert.doesNotThrow(() => assertCommonCoreLaunchKnown(false));
    assert.throws(() => assertCommonCoreLaunchKnown(true), /launch outcome is unknown.*retain the collection lock/);
});
