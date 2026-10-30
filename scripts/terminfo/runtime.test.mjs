import assert from 'node:assert/strict';
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { checkoutRevision, createPrivateScratch, createProcessBudget, installLockedCollector, readCollectorLockfile, resolveNpmCli, resolveOwnedExecutable } from './runtime.mjs';

function fixture(callback) {
    const root = realpathSync(mkdtempSync(join(tmpdir(), 'nt-test-')));
    try { callback(root); } finally { rmSync(root, { recursive: true, force: true }); }
}

test('owned launches accept an absolute standalone sidecar and reject bundle paths', () => fixture(root => {
    const standalone = join(root, 'Ntilde');
    writeFileSync(standalone, '');
    assert.equal(resolveOwnedExecutable(standalone), standalone);
    assert.throws(() => resolveOwnedExecutable('Ntilde'), /absolute/);
    const bundled = join(root, 'Installed.App', 'Contents', 'MacOS', 'Ntilde');
    mkdirSync(dirname(bundled), { recursive: true });
    writeFileSync(bundled, '');
    assert.throws(() => resolveOwnedExecutable(bundled), /uninstalled sidecar outside .app bundles/);
}));

test('an alias outside a bundle cannot bypass the owned launch restriction', { skip: process.platform === 'win32' }, () => fixture(root => {
    const bundled = join(root, 'Installed.app', 'Contents', 'MacOS', 'Ntilde');
    mkdirSync(dirname(bundled), { recursive: true });
    writeFileSync(bundled, '');
    const alias = join(root, 'alias');
    symlinkSync(bundled, alias);
    assert.throws(() => resolveOwnedExecutable(alias), /uninstalled sidecar outside .app bundles/);
}));

test('Unix npm resolution uses the selected Node installation rather than PATH', () => fixture(root => {
    const cli = join(root, 'lib/node_modules/npm/bin/npm-cli.js');
    mkdirSync(dirname(cli), { recursive: true });
    writeFileSync(cli, '');
    // null explicitly selects the Node layout without inheriting a user override.
    assert.equal(resolveNpmCli(join(root, 'bin/node'), null, 'darwin'), cli);
}));

test('Windows npm resolution uses node_modules beside node.exe and preserves explicit overrides', () => fixture(root => {
    const installation = join(root, 'Program Files', 'nodejs');
    const cli = join(installation, 'node_modules', 'npm', 'bin', 'npm-cli.js');
    mkdirSync(dirname(cli), { recursive: true });
    writeFileSync(cli, '');
    const node = join(installation, 'node.exe');
    assert.equal(resolveNpmCli(node, null, 'win32'), cli);
    const override = join(root, 'custom npm-cli.js');
    writeFileSync(override, '');
    assert.equal(resolveNpmCli(node, override, 'win32'), override);
    assert.throws(() => resolveNpmCli(node, 'npm-cli.js', 'win32'), /absolute/);
}));

test('collector preparation refuses a missing lockfile and retains exact bytes', () => fixture(root => {
    assert.throws(() => readCollectorLockfile(root), /missing package-lock.json/);
    const bytes = Buffer.from('{"lockfileVersion":3}\n');
    writeFileSync(join(root, 'package-lock.json'), bytes);
    assert.deepEqual(readCollectorLockfile(root), bytes);
}));

test('serial process queries share one budget below the owner response ceiling', () => {
    let elapsed = 0;
    const timeout = createProcessBudget(4000, () => elapsed);
    assert.equal(timeout(), 2000);
    elapsed = 1900;
    assert.equal(timeout(), 2000);
    elapsed = 3800;
    assert.equal(timeout(), 200);
    elapsed = 4000;
    assert.throws(timeout, /shared deadline/);
});

test('npm permits an explicit absolute CLI but refuses relative or absent files', () => fixture(root => {
    const cli = join(root, 'npm-cli.js');
    writeFileSync(cli, '');
    assert.equal(resolveNpmCli(process.execPath, cli), cli);
    assert.throws(() => resolveNpmCli(process.execPath, 'npm-cli.js'), /absolute/);
    assert.throws(() => resolveNpmCli(process.execPath, join(root, 'missing.js')), /Install npm beside the selected Node/);
}));

test('checkout metadata refuses PATH lookup and tolerates an unavailable explicit git', () => fixture(root => {
    assert.throws(() => checkoutRevision(root, 'git'), /absolute/);
    assert.equal(checkoutRevision(root, join(root, 'missing-git')), null);
    assert.equal(checkoutRevision(root, null), null);
}));

test('checkout metadata runs only the explicitly selected executable', { skip: process.platform === 'win32' }, () => fixture(root => {
    const git = join(root, 'selected-git');
    writeFileSync(git, '#!/bin/sh\n[ "$#" -eq 4 ] && [ "$1" = "-C" ] && [ "$2" = "${0%/*}" ] && [ "$3" = "rev-parse" ] && [ "$4" = "HEAD" ] || exit 1\nprintf "%s\\n" "0123456789012345678901234567890123456789"\n', { mode: 0o700 });
    assert.equal(checkoutRevision(root, git), '0123456789012345678901234567890123456789');
    assert.equal(checkoutRevision(join(root, 'wrong-repository'), git), null);
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

function lockedPackage(overrides = {}) {
    return Buffer.from(JSON.stringify({ lockfileVersion: 3, packages: {
        '': { dependencies: { 'terminfo.dev': '4.0.0' } },
        'node_modules/terminfo.dev': { version: '4.0.0', resolved: 'https://registry.npmjs.org/terminfo.dev/-/terminfo.dev-4.0.0.tgz',
            integrity: 'sha512-' + 'a'.repeat(86) + '==', ...overrides },
    } }));
}

for (const entry of [{ link: true }, { resolved: 'file:../mutable' }, { integrity: undefined }]) {
    test(`locked installations refuse mutable or unverified dependencies: ${JSON.stringify(entry)}`, () => fixture(root => {
        const directory = join(root, 'collector');
        assert.throws(() => installLockedCollector(directory, Buffer.from('{}'), lockedPackage(entry), '/unused/npm-cli.js'), /integrity digests/);
        assert.ok(!existsSync(directory));
    }));
}

test('failed locked installs stop collection and lockfile rewriting is refused', () => fixture(root => {
    const cli = join(root, 'npm-cli.mjs');
    writeFileSync(cli, 'process.exit(42);');
    assert.throws(() => installLockedCollector(join(root, 'failed'), Buffer.from('{}'), lockedPackage(), cli), /installation failed \(42\)/);
    writeFileSync(cli, `import { writeFileSync } from 'node:fs';
        import { join } from 'node:path';
        writeFileSync(join(process.argv[4], 'package-lock.json'), '{}');`);
    assert.throws(() => installLockedCollector(join(root, 'rewritten'), Buffer.from('{}'), lockedPackage(), cli), /lockfile changed/);
}));
