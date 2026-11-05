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
const policy = 'readonly-owned-source-graph-v4';
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
// Comments are lexical trivia, including inside literal dynamic-import parentheses.
function importTriviaEnd(source, offset) {
    while (offset < source.length) {
        if (/\s/u.test(source[offset])) { offset++; continue; }
        if (source.startsWith('/*', offset)) {
            const end = source.indexOf('*/', offset + 2);
            if (end < 0) throw new Error('Unterminated comment in captured import.');
            offset = end + 2; continue;
        }
        if (source.startsWith('//', offset)) {
            const end = source.slice(offset + 2).search(/[\r\n\u2028\u2029]/u);
            if (end < 0) return source.length;
            offset += end + 2; continue;
        }
        return offset;
    }
    return offset;
}
function literalImports(source) {
    const keywords = /\b(?:(?<from>from)|(?<imported>import))\b/g, targets = [];
    let match = keywords.exec(source);
    while (match) {
        let offset = importTriviaEnd(source, keywords.lastIndex);
        if (match.groups.imported !== undefined && source[offset] === '(') offset = importTriviaEnd(source, offset + 1);
        keywords.lastIndex = offset;
        const quote = source[offset];
        if (quote === '"' || quote === "'") {
            const end = source.indexOf(quote, offset + 1), target = source.slice(offset + 1, end);
            if (end < 0 || target.includes('\\')) throw new Error('Unsupported captured import specifier.');
            targets.push(target); keywords.lastIndex = end + 1;
        }
        match = keywords.exec(source);
    }
    return targets;
}
function checkCapturedImports(sources) {
    // Check every literal runtime dependency before importing any measured module.
    for (const [file, bytes] of sources) {
        if (!file.endsWith('.mjs')) continue;
        for (const target of literalImports(bytes.toString())) {
            if (/^node:[a-z][a-z0-9_/-]*$/.test(target)) continue;
            if (!/^\.\/(?:terminfo\/)?[a-z][a-z0-9-]*\.mjs$/.test(target) || !sources.has(target.split('/').at(-1)))
                throw new Error('Source dependency outside captured scope before import: ' + target);
        }
    }
}
function capture(root) {
    const input = readFileSync(join(repo, 'scripts/terminfo/owned-source-inventory.json'));
    const inventory = JSON.parse(input), hashes = {}, sources = new Map();
    if (inventory.runnerSourceSha256 !== 'terminfo-owned-runner.mjs' || inventory.sourceInventorySha256 !== 'owned-source-inventory.json') throw new Error('Invalid source inventory.');
    for (const [key, file] of Object.entries(inventory)) {
        if (!/^[a-zA-Z][a-zA-Z0-9]*Sha256$/.test(key) || !/^[a-z][a-z0-9-]*\.(?:mjs|json)$/.test(file)) throw new Error('Invalid source inventory entry.');
        const source = pathFor(repo, file), info = lstatSync(source);
        if (!info.isFile() || info.isSymbolicLink()) throw new Error('Expected a regular source file.');
        const bytes = file === 'owned-source-inventory.json' ? input : readFileSync(source);
        const target = pathFor(root, file); mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
        writeFileSync(target, bytes, { flag: 'wx', mode: 0o400 }); hashes[key] = digest(bytes); sources.set(file, bytes);
    }
    checkCapturedImports(sources);
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
