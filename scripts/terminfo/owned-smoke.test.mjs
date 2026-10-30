// Lifecycle checks. A real app is used only when explicitly selected by the caller.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { upstreamReceipt } from './collector.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const prepared = process.platform === 'darwin' && existsSync(resolve(repo, 'artifacts/terminfo/tool/package-lock.json'));

for (const [field, value] of [
    ['schemaVersion', 2],
    ['probeHash', 'a'.repeat(12)],
    ['collectorRevision', 'b'.repeat(40)],
    ['manifestSha256', 'c'.repeat(64)],
    ['bundleSha256', 'd'.repeat(64)],
]) {
    test(`owned runner rejects a modified receipt ${field} before creating evidence or launching an app`, { skip: !prepared }, () => {
        // Clone the small runner fixture so tests never mutate the real tool cache.
        const root = mkdtempSync(join(tmpdir(), 'nt-receipt-'));
        try {
            for (const file of ['terminfo-owned.mjs', 'terminfo/collector.mjs', 'terminfo/ownership.mjs', 'terminfo/runtime.mjs']) {
                const target = join(root, 'scripts', file);
                mkdirSync(dirname(target), { recursive: true });
                copyFileSync(join(repo, 'scripts', file), target);
            }
            const pkg = join(root, 'artifacts/terminfo/tool/node_modules/terminfo.dev');
            mkdirSync(join(pkg, 'dist'), { recursive: true });
            writeFileSync(join(pkg, 'package.json'), JSON.stringify({ version: '4.0.0' }));
            copyFileSync(join(repo, 'artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs'), join(pkg, 'dist/terminfo.bundle.mjs'));
            writeFileSync(join(pkg, 'dist/terminfo.bundle.receipt.json'), JSON.stringify({ ...upstreamReceipt, [field]: value }));
            const result = spawnSync(process.execPath, [join(root, 'scripts/terminfo-owned.mjs'),
                '--executable', '/usr/bin/true', '--app-version', '0.0.0'], { encoding: 'utf8', timeout: 5000 });
            assert.equal(result.status, 1, result.stdout + result.stderr);
            assert.match(result.stderr, /Collector receipt/);
            assert.ok(!result.stdout.includes('Collecting in disposable'));
            assert.deepEqual(readdirSync(join(root, 'artifacts/terminfo')).filter(name => name.startsWith('owned-')), []);
        } finally { rmSync(root, { recursive: true, force: true }); }
    });
}

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
