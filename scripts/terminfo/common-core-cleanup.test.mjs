import test from 'node:test';
import assert from 'node:assert/strict';
import { terminateCommonCoreOwner } from './common-core-cleanup.mjs';
test('startup cleanup handles natural exit and a matched TERM exit', async () => {
    const signals = [];
    await terminateCommonCoreOwner({ exited: () => true, matches: () => false, signal: (s) => signals.push(s) });
    assert.deepEqual(signals, []);
    let gone = false;
    await terminateCommonCoreOwner({
        exited: () => gone,
        matches: () => true,
        signal: (s) => {
            signals.push(s);
            gone = true;
        },
    });
    assert.deepEqual(signals, ['SIGTERM']);
});
test('startup cleanup escalates only behind a fresh identity check and confirms exit', async () => {
    const signals = [];
    let checks = 0,
        gone = false;
    await terminateCommonCoreOwner({
        exited: () => gone,
        matches: () => {
            checks++;
            return true;
        },
        signal: (s) => {
            signals.push(s);
            if (s === 'SIGKILL') gone = true;
        },
        pause: async () => {},
        attempts: 2,
    });
    assert.equal(checks, 2);
    assert.deepEqual(signals, ['SIGTERM', 'SIGKILL']);
});
test('startup cleanup refuses foreign/reused identities and surviving owners', async () => {
    let signals = [];
    await assert.rejects(
        terminateCommonCoreOwner({ exited: () => false, matches: () => false, signal: (s) => signals.push(s) }),
        /changed launch identity/,
    );
    assert.deepEqual(signals, []);
    let checks = 0;
    await assert.rejects(
        terminateCommonCoreOwner({
            exited: () => false,
            matches: () => ++checks === 1,
            signal: (s) => signals.push(s),
            pause: async () => {},
            attempts: 1,
        }),
        /identity changed/,
    );
    assert.deepEqual(signals, ['SIGTERM']);
    signals = [];
    await assert.rejects(
        terminateCommonCoreOwner({
            exited: () => false,
            matches: () => true,
            signal: (s) => signals.push(s),
            pause: async () => {},
            attempts: 1,
        }),
        /survived/,
    );
    assert.deepEqual(signals, ['SIGTERM', 'SIGKILL']);
});
