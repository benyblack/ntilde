import assert from 'node:assert/strict';
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { checkoutRevision, createPrivateScratch, resolveNpmCli } from './runtime.mjs';

function fixture(callback) {
    const root = realpathSync(mkdtempSync(join(tmpdir(), 'nt-test-')));
    try { callback(root); } finally { rmSync(root, { recursive: true, force: true }); }
}

test('npm resolution uses the selected Node installation rather than PATH', () => fixture(root => {
    const cli = join(root, 'lib/node_modules/npm/bin/npm-cli.js');
    mkdirSync(dirname(cli), { recursive: true });
    writeFileSync(cli, '');
    assert.equal(resolveNpmCli(join(root, 'bin/node'), undefined), cli);
}));

test('npm permits an explicit absolute CLI but refuses relative or absent files', () => fixture(root => {
    const cli = join(root, 'npm-cli.js');
    writeFileSync(cli, '');
    assert.equal(resolveNpmCli(process.execPath, cli), cli);
    assert.throws(() => resolveNpmCli(process.execPath, 'npm-cli.js'), /absolute/);
    assert.throws(() => resolveNpmCli(process.execPath, join(root, 'missing.js')), /Prepare the collector cache/);
}));

test('checkout metadata refuses PATH lookup and tolerates an unavailable explicit git', () => fixture(root => {
    assert.throws(() => checkoutRevision(root, 'git'), /absolute/);
    assert.equal(checkoutRevision(root, join(root, 'missing-git')), null);
    assert.equal(checkoutRevision(root, null), null);
}));

test('checkout metadata runs only the explicitly selected executable', { skip: process.platform === 'win32' }, () => fixture(root => {
    const git = join(root, 'selected-git');
    writeFileSync(git, '#!/bin/sh\nprintf "%s\\n" "0123456789012345678901234567890123456789"\n', { mode: 0o700 });
    assert.equal(checkoutRevision(root, git), '0123456789012345678901234567890123456789');
    // Neither a non-revision stdout value nor a failed tool is checkout identity.
    writeFileSync(git, '#!/bin/sh\nprintf "%s\\n" invalid\n');
    assert.equal(checkoutRevision(root, git), null);
}));

test('scratch directories live beneath a private parent and stay private', { skip: process.platform === 'win32' }, () => fixture(root => {
    const parent = join(root, 'private');
    const scratch = createPrivateScratch(parent);
    assert.equal(dirname(scratch), parent);
    assert.equal(lstatSync(parent).mode & 0o077, 0);
    assert.equal(lstatSync(scratch).mode & 0o077, 0);
}));

test('scratch creation refuses shared parents and symlinks without changing their permissions', { skip: process.platform === 'win32' }, () => fixture(root => {
    const shared = join(root, 'shared');
    mkdirSync(shared);
    chmodSync(shared, 0o755);
    assert.throws(() => createPrivateScratch(shared), /private directory/);
    assert.equal(lstatSync(shared).mode & 0o777, 0o755);
    const link = join(root, 'link');
    symlinkSync(root, link);
    assert.throws(() => createPrivateScratch(link), /private directory/);
    assert.deepEqual(readdirSync(shared), []);
}));

test('long private fixture paths are rejected before creating scratch data', { skip: process.platform === 'win32' }, () => fixture(root => {
    const parent = join(root, 'x'.repeat(104));
    assert.throws(() => createPrivateScratch(parent), /too long/);
    assert.ok(existsSync(parent));
    assert.deepEqual(readdirSync(parent), []);
}));
