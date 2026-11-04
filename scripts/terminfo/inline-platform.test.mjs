import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const script = join(repo, 'scripts/terminfo-baseline.mjs');
const runs = join(repo, 'artifacts/terminfo');

// Run the inline runner in a child with a chosen platform. The working directory
// is a private empty folder, and no TTY or Ntilde environment is supplied, so the
// runner can only stop at its invocation checks: it never prepares a collector.
function runAs(platform, args) {
    const cwd = mkdtempSync(join(tmpdir(), 'nt-inline-platform-'));
    const program = `Object.defineProperty(process, 'platform', { value: ${JSON.stringify(platform)} });
        process.argv = ['node', ${JSON.stringify(script)}, ...${JSON.stringify(args)}];
        await import(${JSON.stringify(pathToFileURL(script).href)});`;
    try {
        const env = { ...process.env };
        for (const key of ['TERM_PROGRAM', 'TMUX', 'STY', 'SSH_CONNECTION', 'SSH_TTY']) delete env[key];
        return spawnSync(process.execPath, ['--input-type=module', '--eval', program],
            { cwd, encoding: 'utf8', timeout: 5000, env, stdio: ['ignore', 'pipe', 'pipe'] });
    } finally {
        rmSync(cwd, { recursive: true, force: true });
    }
}

const runDirectories = () => existsSync(runs) ? readdirSync(runs).filter(name => name.startsWith('run-')).sort() : [];

test('inline runner refuses native Windows before any run, cache or collector step', () => {
    const before = runDirectories();
    const result = runAs('win32', ['--app-version', '0.0.0']);
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /^\[terminfo\] terminfo\.dev 4\.0\.0 cannot record results on native Windows\./m);
    assert.match(result.stderr, /WSL inside a direct Ntilde tab/);
    assert.match(result.stderr, /Windows: collect through WSL" in docs\/qa\/QA_TERMINFO\.md/);
    assert.doesNotMatch(result.stdout, /Recording terminfo\.dev/);
    assert.deepEqual(runDirectories(), before, 'No run directory may be created.');
});

test('native Windows refusal takes precedence over invocation errors', () => {
    const result = runAs('win32', []);
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /cannot record results on native Windows/);
    assert.doesNotMatch(result.stderr, /Supply --app-version/);
});

test('help remains available on native Windows and names the WSL path', () => {
    const result = runAs('win32', ['--help']);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stdout, /^Usage: node scripts\/terminfo-baseline\.mjs --app-version VERSION/m);
    assert.match(result.stdout, /Native Windows is refused: run from WSL inside a direct Ntilde tab instead\./);
});

test('other platforms pass the Windows check and reach the existing invocation checks', () => {
    for (const platform of ['linux', 'darwin']) {
        const result = runAs(platform, ['--app-version', '0.0.0']);
        assert.equal(result.status, 1, result.stdout + result.stderr);
        assert.doesNotMatch(result.stderr, /native Windows/, platform);
        // Node 24+ is checked before the TTY; either existing check proves the
        // platform check let the invocation through.
        assert.match(result.stderr, /Node 24\+ is required\.|Run directly in a Ntilde tab with interactive stdin and stdout\./, platform);
    }
});
