#!/usr/bin/env node
// Trusted BCL-only launcher. Capture measured source bytes before importing any of them.
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const pathFor = (root, file) => join(root, 'scripts', file === 'terminfo-owned-runner.mjs' ? file : 'terminfo/' + file);
const policy = 'readonly-owned-source-graph-v1';
function setDirectoryModes(root, mode) {
    for (const entry of readdirSync(root, { withFileTypes: true })) if (entry.isDirectory()) setDirectoryModes(join(root, entry.name), mode);
    chmodSync(root, mode);
}
function removeSnapshot(root) {
    if (!root || !existsSync(root)) return;
    if (!lstatSync(root).isDirectory() || lstatSync(root).isSymbolicLink()) { rmSync(root, { force: true }); return; }
    setDirectoryModes(root, 0o700);
    rmSync(root, { recursive: true, force: true });
}
function capture(root) {
    const input = readFileSync(join(repo, 'scripts/terminfo/owned-source-inventory.json'));
    const inventory = JSON.parse(input), hashes = {};
    if (inventory.runnerSourceSha256 !== 'terminfo-owned-runner.mjs' || inventory.sourceInventorySha256 !== 'owned-source-inventory.json') throw new Error('Invalid source inventory.');
    for (const [key, file] of Object.entries(inventory)) {
        if (!/^[a-zA-Z][a-zA-Z0-9]*Sha256$/.test(key) || !/^[a-z][a-z0-9-]*\.(?:mjs|json)$/.test(file)) throw new Error('Invalid source inventory entry.');
        const source = pathFor(repo, file), info = lstatSync(source);
        if (!info.isFile() || info.isSymbolicLink()) throw new Error('Expected a regular source file.');
        const bytes = file === 'owned-source-inventory.json' ? input : readFileSync(source);
        const target = pathFor(root, file); mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
        writeFileSync(target, bytes, { flag: 'wx', mode: 0o400 }); hashes[key] = digest(bytes);
    }
    const manifest = Buffer.from(JSON.stringify({ schemaVersion: 1, policy, hashes }, null, 2) + '\n');
    writeFileSync(join(root, 'source-snapshot.json'), manifest, { flag: 'wx', mode: 0o400 });
    setDirectoryModes(root, 0o500); return digest(manifest);
}
async function launch() {
    let root, interrupted;
    const note = signal => { interrupted ??= signal; process.env.NTILDE_TERMINFO_PENDING_SIGNAL = signal; };
    const onInt = () => note('SIGINT'), onTerm = () => note('SIGTERM');
    process.on('SIGINT', onInt); process.on('SIGTERM', onTerm);
    const names = ['NTILDE_TERMINFO_ORIGINAL_REPO', 'NTILDE_TERMINFO_SOURCE_SNAPSHOT', 'NTILDE_TERMINFO_SOURCE_SNAPSHOT_SHA256', 'NTILDE_TERMINFO_PENDING_SIGNAL'];
    const previous = names.map(name => [name, process.env[name]]);
    try {
        root = realpathSync(mkdtempSync(join(tmpdir(), 'ntilde-owned-sources-')));
        const sha256 = capture(root);
        if (interrupted) throw new Error('Interrupted by ' + interrupted + '.');
        process.env.NTILDE_TERMINFO_ORIGINAL_REPO = repo;
        process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT = root;
        process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT_SHA256 = sha256;
        delete process.env.NTILDE_TERMINFO_PENDING_SIGNAL;
        await import(pathToFileURL(pathFor(root, 'terminfo-owned-runner.mjs')).href);
        if (interrupted) process.exitCode = 1;
    } finally {
        try { removeSnapshot(root); }
        finally {
            process.removeListener('SIGINT', onInt); process.removeListener('SIGTERM', onTerm);
            for (const [name, value] of previous) { if (value === undefined) delete process.env[name]; else process.env[name] = value; }
        }
    }
}

try { await launch(); }
catch (error) { console.error('[terminfo-owned] ' + error.message); process.exitCode = 1; }
