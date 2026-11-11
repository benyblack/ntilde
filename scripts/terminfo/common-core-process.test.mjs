import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { spawnCommonCoreApplication } from './common-core-process.mjs';
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
