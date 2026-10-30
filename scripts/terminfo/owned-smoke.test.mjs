// Lifecycle checks. A real app is used only when explicitly selected by the caller.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { test } from 'node:test';
import { upstreamReceipt } from './collector.mjs';
import { installLockedCollector } from './runtime.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const prepared = process.platform === 'darwin'
    && existsSync(resolve(repo, 'artifacts/terminfo/tool/package-lock.json'))
    && existsSync(resolve(repo, 'artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs'));

for (const alias of [false, true]) {
    test(`packaged macOS executables are refused before launch${alias ? ' through an outside alias' : ''}`, {
        skip: process.platform !== 'darwin' || Number(process.versions.node.split('.')[0]) < 24,
    }, () => {
        const root = mkdtempSync(join(tmpdir(), 'nt-bundle-'));
        try {
            const bundled = join(root, 'Installed.app/Contents/MacOS/Ntilde');
            mkdirSync(dirname(bundled), { recursive: true });
            writeFileSync(bundled, '#!/bin/sh\n: > "$NTILDE_TERMINFO_TEST_LAUNCH_MARKER"\n', { mode: 0o700 });
            const executable = alias ? join(root, 'alias') : bundled;
            if (alias) symlinkSync(bundled, executable);
            const marker = join(root, 'launched');
            const result = spawnSync(process.execPath, ['scripts/terminfo-owned.mjs',
                '--executable', executable, '--app-version', '0.0.0'], {
                cwd: repo, encoding: 'utf8', timeout: 5000,
                env: { ...process.env, NTILDE_TERMINFO_TEST_LAUNCH_MARKER: marker },
            });
            assert.equal(result.status, 1, result.stdout + result.stderr);
            assert.match(result.stderr, /uninstalled sidecar outside .app bundles/);
            assert.ok(!result.stdout.includes('Collecting in disposable'));
            assert.ok(!existsSync(marker), 'The packaged executable must never start.');
        } finally { rmSync(root, { recursive: true, force: true }); }
    });
}

function withCollectorFixture(receipt, lockfile, action) {
    // Clone the small runner fixture so tests never mutate the real tool cache.
    const root = mkdtempSync(join(tmpdir(), 'nt-receipt-'));
    try {
        for (const file of ['terminfo-owned.mjs', 'terminfo-baseline.mjs', 'terminfo/collector.mjs', 'terminfo/ownership.mjs', 'terminfo/runtime.mjs']) {
            const target = join(root, 'scripts', file);
            mkdirSync(dirname(target), { recursive: true });
            copyFileSync(join(repo, 'scripts', file), target);
        }
        const tool = join(root, 'artifacts/terminfo/tool');
        const pkg = join(tool, 'node_modules/terminfo.dev');
        mkdirSync(join(pkg, 'dist'), { recursive: true });
        writeFileSync(join(pkg, 'package.json'), JSON.stringify({ version: '4.0.0' }));
        copyFileSync(join(repo, 'artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs'), join(pkg, 'dist/terminfo.bundle.mjs'));
        writeFileSync(join(pkg, 'dist/terminfo.bundle.receipt.json'), JSON.stringify(receipt));
        if (lockfile) copyFileSync(join(repo, 'artifacts/terminfo/tool/package-lock.json'), join(tool, 'package-lock.json'));
        copyFileSync(join(repo, 'artifacts/terminfo/tool/package.json'), join(tool, 'package.json'));
        action(root);
    } finally { rmSync(root, { recursive: true, force: true }); }
}

function assertRejectedBeforeRun(root, errorPattern) {
    const result = spawnSync(process.execPath, [join(root, 'scripts/terminfo-owned.mjs'),
        '--executable', '/usr/bin/true', '--app-version', '0.0.0'], { encoding: 'utf8', timeout: 5000 });
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, errorPattern);
    assert.ok(!result.stdout.includes('Collecting in disposable'));
    assert.deepEqual(readdirSync(join(root, 'artifacts/terminfo')).filter(name => name.startsWith('owned-')), []);
}

for (const [field, value] of [
    ['schemaVersion', 2],
    ['probeHash', 'a'.repeat(12)],
    ['collectorRevision', 'b'.repeat(40)],
    ['manifestSha256', 'c'.repeat(64)],
    ['bundleSha256', 'd'.repeat(64)],
]) {
    test(`owned runner rejects a modified receipt ${field} before creating evidence or launching an app`, { skip: !prepared }, () => {
        withCollectorFixture({ ...upstreamReceipt, [field]: value }, true,
            root => assertRejectedBeforeRun(root, /Collector receipt/));
    });
}

test('owned runner rejects a missing lockfile before creating run evidence', { skip: !prepared }, () => {
    withCollectorFixture(upstreamReceipt, false, root => assertRejectedBeforeRun(root, /missing package-lock.json/));
});

function runInlinePreparation(script, env = process.env) {
    const program = `Object.defineProperty(process.stdin, 'isTTY', { value: true });
        Object.defineProperty(process.stdout, 'isTTY', { value: true });
        process.argv = ['node', ${JSON.stringify(script)}, '--app-version', '0.0.0'];
        process.env.TERM_PROGRAM = 'Ntilde';
        for (const key of ['TMUX', 'STY', 'SSH_CONNECTION', 'SSH_TTY']) delete process.env[key];
        await import(${JSON.stringify(pathToFileURL(script).href)});`;
    return spawnSync(process.execPath, ['--input-type=module', '--eval', program], { encoding: 'utf8', timeout: 5000, env });
}

test('inline runner rejects a missing lockfile before creating run evidence', { skip: !prepared }, () => {
    withCollectorFixture(upstreamReceipt, false, root => {
        // Exercise preparation in isolation: no collector executes on this path.
        const script = join(root, 'scripts/terminfo-baseline.mjs');
        const result = runInlinePreparation(script);
        assert.equal(result.status, 1, result.stdout + result.stderr);
        assert.match(result.stderr, /missing package-lock.json/);
        assert.deepEqual(readdirSync(join(root, 'artifacts/terminfo')).filter(name => name.startsWith('run-')), []);
    });
});

function launch(executable, interrupt, observation = {}) {
    return new Promise((resolve, reject) => {
        const preload = observation.log ? ['--import', join(repo, 'scripts/terminfo/fixtures/owned-lifecycle.mjs')] : [];
        const child = spawn(process.execPath, [...preload, 'scripts/terminfo-owned.mjs', '--executable', executable, '--app-version', '0.0.0'], {
            cwd: repo, stdio: ['ignore', 'pipe', 'pipe'],
            env: { ...process.env, NTILDE_TERMINFO_TEST_LIFECYCLE_LOG: observation.log,
                NTILDE_TERMINFO_TEST_LIFECYCLE_PHASE: observation.phase },
        });
        let output = '';
        let errors = '';
        let sent = false;
        const timer = setTimeout(() => { child.kill('SIGTERM'); reject(new Error('Adapter lifecycle did not finish.')); }, 15000);
        child.on('error', reject);
        child.stdout.on('data', chunk => {
            output += chunk;
            if (interrupt && !sent && output.includes('results:')) {
                sent = true;
                child.kill('SIGINT');
            }
        });
        child.stderr.on('data', chunk => { errors += chunk; });
        child.on('exit', code => { clearTimeout(timer); resolve({ code, output, errors }); });
    });
}

function assertCleaned(result) {
    assert.equal(result.code, 1, result.output + result.errors);
    const match = /pid (\d+); results: ([^\n]+)/.exec(result.output);
    assert.ok(match, result.output + result.errors);
    const pid = Number(match[1]);
    const directory = match[2];
    const settings = JSON.parse(readFileSync(resolve(directory, 'initial-settings.json')));
    const scratch = dirname(settings.Profiles[0].Command);
    assert.ok(!existsSync(scratch), 'Private app-data and owner socket must be removed.');
    assert.throws(() => process.kill(pid, 0), /ESRCH/, 'Only the launched app should have been terminated.');
    const proof = JSON.parse(readFileSync(resolve(directory, 'ownership-proof.json')));
    assert.equal(proof.completed, false);
    assert.ok(existsSync(resolve(directory, 'failure.json')));
}

test('an early app exit fails promptly and removes the private fixture', { skip: !prepared }, async () => {
    const result = await launch('/usr/bin/true', false);
    assert.match(result.errors, /exited before collection/);
    assertCleaned(result);
});

test('interrupting a launched Ntilde instance tears it down', {
    skip: !prepared || !process.env.NTILDE_TERMINFO_TEST_EXECUTABLE,
}, async () => {
    const result = await launch(process.env.NTILDE_TERMINFO_TEST_EXECUTABLE, true);
    assert.match(result.errors, /Interrupted by SIGINT/);
    assertCleaned(result);
});

for (const [phase, initialInterrupt, collected] of [
    ['preparation', false, false], ['post-spawn', false, false],
    ['teardown', true, false], ['fixture-removal', true, false],
    ['fixture-removal', false, true],
]) {
    test(`repeated signals remain handled during ${phase}${collected ? ' after collection' : ''} and resources are cleaned`, {
        skip: !prepared || !process.env.NTILDE_TERMINFO_TEST_EXECUTABLE,
    }, async () => {
        const root = mkdtempSync(join(tmpdir(), 'nt-observe-'));
        const log = join(root, 'lifecycle.jsonl');
        try {
            const result = await launch(process.env.NTILDE_TERMINFO_TEST_EXECUTABLE,
                initialInterrupt, { log, phase });
            assert.equal(result.code, 1, result.output + result.errors);
            assert.match(result.errors, /Interrupted by SIGINT/);
            const observations = readFileSync(log, 'utf8').trim().split('\n').map(line => JSON.parse(line));
            const boundaries = observations.filter(item => item.phase === phase);
            assert.equal(boundaries.length, 2, JSON.stringify(observations));
            for (const boundary of boundaries) {
                assert.ok(boundary.sigint > 0, `Missing SIGINT handler: ${JSON.stringify(boundary)}`);
                assert.ok(boundary.sigterm > 0, `Missing SIGTERM handler: ${JSON.stringify(boundary)}`);
            }
            for (const item of observations) {
                if (item.scratch) assert.ok(!existsSync(item.scratch), 'Private fixture must be removed.');
                if (item.appPid) assert.throws(() => process.kill(item.appPid, 0), /ESRCH/, 'Disposable app must have exited.');
            }
            const finished = observations.find(item => item.phase === 'finished');
            assert.deepEqual(finished, { phase: 'finished', sigint: 0, sigterm: 0 });
            if (collected) {
                const output = /results: ([^\n]+)/.exec(result.output)[1];
                assert.ok(existsSync(join(output, 'summary.json')));
                assert.equal(JSON.parse(readFileSync(join(output, 'ownership-proof.json'))).completed, true);
                assert.match(JSON.parse(readFileSync(join(output, 'failure.json'))).error, /Interrupted by SIGINT/);
            }
        } finally { rmSync(root, { recursive: true, force: true }); }
    });
}

test('run dependencies are recreated from locked archives instead of the changed cache', {
    skip: !prepared || !process.env.NTILDE_TERMINFO_NPM_CLI,
}, () => {
    withCollectorFixture(upstreamReceipt, true, root => {
        const tool = join(root, 'artifacts/terminfo/tool');
        const changed = join(tool, 'node_modules/commander');
        mkdirSync(changed, { recursive: true });
        writeFileSync(join(changed, 'package.json'), '{"name":"commander","version":"0.0.0"}');
        writeFileSync(join(changed, 'index.js'), 'throw new Error("changed cache executed");');
        const lock = readFileSync(join(tool, 'package-lock.json'));
        const installed = installLockedCollector(join(root, 'run-collector'), readFileSync(join(tool, 'package.json')), lock);
        const dependency = join(dirname(installed), 'commander');
        assert.equal(JSON.parse(readFileSync(join(dependency, 'package.json'))).version,
            JSON.parse(lock).packages['node_modules/commander'].version);
        assert.deepEqual(readFileSync(join(dependency, 'index.js')),
            readFileSync(join(repo, 'artifacts/terminfo/tool/node_modules/commander/index.js')));
        assert.match(readFileSync(join(changed, 'index.js'), 'utf8'), /changed cache executed/);
        assert.ok(!existsSync(join(tool, 'node_modules/@silvery')), 'The prepared cache must not be rewritten.');
    });
});

for (const runner of ['owned', 'inline']) {
    test(`${runner} runner refuses to execute the reused cache if locked installation fails`, { skip: !prepared }, () => {
        withCollectorFixture(upstreamReceipt, true, root => {
            const cli = join(root, 'npm-cli.mjs');
            writeFileSync(cli, 'process.exit(42);');
            const env = { ...process.env, NTILDE_TERMINFO_NPM_CLI: cli };
            const result = runner === 'inline'
                ? runInlinePreparation(join(root, 'scripts/terminfo-baseline.mjs'), env)
                : spawnSync(process.execPath, [join(root, 'scripts/terminfo-owned.mjs'),
                    '--executable', '/usr/bin/true', '--app-version', '0.0.0'], { env, encoding: 'utf8', timeout: 5000 });
            assert.equal(result.status, 1, result.stdout + result.stderr);
            assert.match(result.stderr, /Locked collector installation failed \(42\)/);
            assert.ok(!/Collecting in disposable|Recording terminfo.dev/.test(result.stdout));
            if (runner === 'owned') {
                const output = readdirSync(join(root, 'artifacts/terminfo')).find(name => name.startsWith('owned-'));
                assert.match(JSON.parse(readFileSync(join(root, 'artifacts/terminfo', output, 'failure.json'))).error,
                    /Locked collector installation failed \(42\)/);
            }
        });
    });
}
