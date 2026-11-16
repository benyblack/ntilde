import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawnSync } from 'node:child_process';
import { requireCollectorNode, realBundleRequired } from './real-bundle-test.mjs';

test('real collector replay refuses Node 22 before importing the locked using-render dependency', () => {
    assert.throws(() => requireCollectorNode('22.23.4'), /requires Node 24.*using render/);
    assert.doesNotThrow(() => requireCollectorNode('24.22.2'));
});

test('required replay is explicit and rejects misspelled switches', () => {
    assert.equal(realBundleRequired({}), false);
    assert.equal(realBundleRequired({ NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED: '0' }), false);
    assert.equal(realBundleRequired({ NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED: '1' }), true);
    assert.throws(() => realBundleRequired({ NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED: 'true' }), /must be 0 or 1/);
});

test('required replay fails when fresh preparation is unavailable instead of skipping', () => {
    const helper = new URL('./real-bundle-test.mjs', import.meta.url).href;
    const environment = { ...process.env, NTILDE_TERMINFO_REAL_BUNDLE_REQUIRED: '1', NTILDE_TERMINFO_NPM_CLI: '/missing-issue-977/npm-cli.js' };
    delete environment.NODE_TEST_CONTEXT; // The child's standalone reporter must not inherit the parent's binary IPC protocol.
    const child = spawnSync(process.execPath, ['--input-type=module', '-e',
        `import { testWithPinnedCollector } from ${JSON.stringify(helper)}; testWithPinnedCollector('required replay', () => { throw new Error('unverified collector ran'); });`], {
        encoding: 'utf8', timeout: 15000,
        env: environment,
    });
    assert.equal(child.error, undefined);
    assert.equal(child.status, 1);
    assert.match(child.stdout, /not ok|✖/);
    assert.doesNotMatch(child.stdout, /# SKIP|unverified collector ran/);
    assert.match(child.stdout, /Install npm beside the selected Node|requires Node 24/);
});
