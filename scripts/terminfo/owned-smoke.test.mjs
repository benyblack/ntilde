// Lifecycle checks. A real app is used only when explicitly selected by the caller.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { test } from 'node:test';
import { upstreamReceipt } from './collector.mjs';
import { installLockedCollector } from './runtime.mjs';
import { terminateRecordedWorker } from './ownership.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const prepared = process.platform === 'darwin'
    && existsSync(resolve(repo, 'artifacts/terminfo/tool/package.json'))
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
        for (const file of ['terminfo-owned.mjs', 'terminfo-baseline.mjs', 'terminfo/collector.mjs', 'terminfo/ownership.mjs', 'terminfo/runtime.mjs',
            'terminfo/agent-observer.mjs', 'terminfo/cells.mjs', 'terminfo/terminal-fence.mjs', 'terminfo/terminal-output.mjs',
            'terminfo/attributes.mjs', 'terminfo/sgr.mjs', 'terminfo/cell-details.mjs', 'terminfo/rgb.mjs', 'terminfo/rendition.mjs', 'terminfo/cursor.mjs', 'terminfo/receipt.mjs', 'terminfo/pixels.mjs', 'terminfo/pixels-capture.mjs', 'terminfo/pixels-cases.mjs', 'terminfo/pixels-png.mjs', 'terminfo/color-query.mjs', 'terminfo/color-cases.mjs', 'terminfo/color-capture.mjs', 'terminfo/colors.mjs', 'terminfo/editing-state.mjs', 'terminfo/editing-cases.mjs', 'terminfo/editing-expectations.mjs', 'terminfo/editing-capture.mjs', 'terminfo/editing.mjs', 'terminfo/history-cases.mjs', 'terminfo/history-capture.mjs', 'terminfo/history.mjs',
            'terminfo/saved-cases.mjs', 'terminfo/saved-capture.mjs', 'terminfo/saved.mjs', 'terminfo/saved-expectations.mjs', 'terminfo/saved-receipt.mjs',
            'terminfo/resize-cases.mjs', 'terminfo/resize-capture.mjs', 'terminfo/resize.mjs', 'terminfo/resize-action.mjs', 'terminfo/resize-receipt.mjs',
            'terminfo/collector-package.json', 'terminfo/collector-package-lock.json']) {
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
            env: { ...process.env, ...(observation.stubborn ? {
                NTILDE_TERMINFO_TEST_STUBBORN_WORKER: '1',
                NODE_OPTIONS: [process.env.NODE_OPTIONS, `--import=${pathToFileURL(join(repo, 'scripts/terminfo/fixtures/owned-lifecycle.mjs')).href}`].filter(Boolean).join(' '),
            } : {}), NTILDE_TERMINFO_TEST_LIFECYCLE_LOG: observation.log,
                NTILDE_TERMINFO_TEST_LIFECYCLE_PHASE: observation.phase,
                NTILDE_TERMINFO_TEST_PIXEL_BUDGET: observation.pixelBudget ? '1' : undefined,
                NTILDE_TERMINFO_TEST_RGB_BUDGET: observation.rgbBudget ? '1' : undefined },
        });
        let output = '';
        let errors = '';
        let sent = false;
        // Complete runs include 151 paired state/history frames, 75 PNGs and six App actions
        // with before/after process proofs. The resistant-worker run completes its receipt in
        // 20.06s (measured through process exit), including the bounded SIGTERM/SIGKILL path.
        // Allow 25s only for completed collections; interruption-only cases retain 15s.
        const collected = observation.stubborn || observation.phase === 'fixture-removal' && !interrupt;
        const timeoutMs = collected ? 25000 : 15000;
        let lifecycleError, terminationTimer;
        const timer = setTimeout(() => {
            lifecycleError = new Error('Adapter lifecycle did not finish.');
            child.kill('SIGTERM');
            // Wait for the adapter's cleanup before another desktop test can start.
            terminationTimer = setTimeout(() => { child.kill('SIGKILL'); }, 5000);
        }, timeoutMs);
        child.on('error', reject);
        child.stdout.on('data', chunk => {
            output += chunk;
            if (interrupt && !sent && output.includes('results:')) {
                sent = true;
                child.kill('SIGINT');
            }
        });
        child.stderr.on('data', chunk => { errors += chunk; });
        child.on('close', code => {
            clearTimeout(timer); clearTimeout(terminationTimer);
            if (lifecycleError) reject(lifecycleError); else resolve({ code, output, errors });
        });
    });
}

function assertRetainedCollector(output) {
    const context = JSON.parse(readFileSync(join(output, 'context.json')));
    const digest = bytes => createHash('sha256').update(bytes).digest('hex');
    assert.equal(digest(readFileSync(join(output, context.retainedAdapterBundle))), context.adapterBundleSha256);
    for (const [field, source] of [
        ['runnerSourceSha256', 'terminfo-owned.mjs'], ['adapterSourceSha256', 'terminfo/collector.mjs'],
        ['ownershipSourceSha256', 'terminfo/ownership.mjs'], ['workerSourceSha256', 'terminfo/worker.mjs'],
        ['runtimeSourceSha256', 'terminfo/runtime.mjs'],
        ['observerSourceSha256', 'terminfo/agent-observer.mjs'], ['cellSuiteSourceSha256', 'terminfo/cells.mjs'],
        ['cellFenceSourceSha256', 'terminfo/terminal-fence.mjs'], ['terminalOutputSourceSha256', 'terminfo/terminal-output.mjs'],
        ['attributeSourceSha256', 'terminfo/attributes.mjs'], ['sgrSuiteSourceSha256', 'terminfo/sgr.mjs'],
        ['cellDetailsSourceSha256', 'terminfo/cell-details.mjs'], ['rgbSuiteSourceSha256', 'terminfo/rgb.mjs'],
        ['renditionSuiteSourceSha256', 'terminfo/rendition.mjs'], ['cursorSuiteSourceSha256', 'terminfo/cursor.mjs'],
        ['pixelCaptureSourceSha256', 'terminfo/pixels-capture.mjs'], ['pixelSuiteSourceSha256', 'terminfo/pixels.mjs'],
        ['pixelCasesSourceSha256', 'terminfo/pixels-cases.mjs'], ['pixelPngSourceSha256', 'terminfo/pixels-png.mjs'],
        ['colorQuerySourceSha256', 'terminfo/color-query.mjs'], ['colorCaptureSourceSha256', 'terminfo/color-capture.mjs'],
        ['colorCasesSourceSha256', 'terminfo/color-cases.mjs'], ['colorSuiteSourceSha256', 'terminfo/colors.mjs'],
        ['editingStateSourceSha256', 'terminfo/editing-state.mjs'], ['editingCasesSourceSha256', 'terminfo/editing-cases.mjs'],
        ['editingExpectationsSourceSha256', 'terminfo/editing-expectations.mjs'], ['editingCaptureSourceSha256', 'terminfo/editing-capture.mjs'],
        ['editingSuiteSourceSha256', 'terminfo/editing.mjs'],
        ['historyCasesSourceSha256', 'terminfo/history-cases.mjs'], ['historyCaptureSourceSha256', 'terminfo/history-capture.mjs'],
        ['historySuiteSourceSha256', 'terminfo/history.mjs'],
        ['savedCasesSourceSha256', 'terminfo/saved-cases.mjs'], ['savedCaptureSourceSha256', 'terminfo/saved-capture.mjs'],
        ['savedSuiteSourceSha256', 'terminfo/saved.mjs'], ['savedExpectationsSourceSha256', 'terminfo/saved-expectations.mjs'],
        ['savedReceiptSourceSha256', 'terminfo/saved-receipt.mjs'],
        ['resizeCasesSourceSha256', 'terminfo/resize-cases.mjs'], ['resizeCaptureSourceSha256', 'terminfo/resize-capture.mjs'],
        ['resizeSuiteSourceSha256', 'terminfo/resize.mjs'], ['resizeActionSourceSha256', 'terminfo/resize-action.mjs'],
        ['resizeReceiptSourceSha256', 'terminfo/resize-receipt.mjs'],
        ['receiptSourceSha256', 'terminfo/receipt.mjs'],
        ['collectorManifestSha256', 'terminfo/collector-package.json'],
        ['collectorLockSha256', 'terminfo/collector-package-lock.json'],
    ]) {
        const retained = readFileSync(join(output, context.retainedSources, source.split('/').at(-1)));
        assert.equal(digest(retained), context[field], source);
        assert.deepEqual(retained, readFileSync(join(repo, 'scripts', source)), source);
    }
}

test('an oversized real viewport records RGB unavailable while retaining stock raw and completed cleanup', {
    skip: !prepared || !process.env.NTILDE_TERMINFO_TEST_EXECUTABLE,
}, async () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-rgb-budget-'));
    try {
        const result = await launch(process.env.NTILDE_TERMINFO_TEST_EXECUTABLE, false,
            { log: join(root, 'lifecycle.jsonl'), rgbBudget: true });
        assert.equal(result.code, 0, result.output + result.errors);
        const output = /results: ([^\n]+)/.exec(result.output)[1];
        const receipt = JSON.parse(readFileSync(join(output, 'cell-receipt.json')));
        const unavailable = JSON.parse(readFileSync(join(output, receipt.rgbEvidence.file)));
        assert.equal(receipt.rgbEvidence.status, 'unavailable');
        assert.equal(receipt.rgbEvidence.reason, 'screenDetailsUnavailable');
        assert.equal(receipt.resizeEvidence.status, 'unavailable');
        assert.equal(receipt.resizeEvidence.reason, 'screenDetailsUnavailable');
        assert.equal(readFileSync(join(output, 'resize-snapshots.jsonl'), 'utf8'), '');
        assert.equal(readFileSync(join(output, 'resize-actions.jsonl'), 'utf8'), '');
        assert.equal(receipt.renditionEvidence.status, 'unavailable');
        assert.equal(receipt.cursorEvidence.status, 'unavailable');
        assert.equal(receipt.pixelEvidence.status, 'unavailable');
        assert.deepEqual(receipt.pixelEvidence.counts, {});
        assert.equal(receipt.colorEvidence.status, 'unavailable');
        assert.equal(readFileSync(join(output, 'color-snapshots.jsonl'), 'utf8'), '');
        assert.equal(readFileSync(join(output, 'pixel-snapshots.jsonl'), 'utf8'), '');
        assert.deepEqual(receipt.rgbEvidence.counts, {});
        assert.equal(unavailable.fixtureStarted, false);
        assert.equal(unavailable.plannedExperiments, 20);
        assert.equal(unavailable.runId, receipt.runId);
        const hash = file => createHash('sha256').update(readFileSync(join(output, file))).digest('hex');
        assert.equal(receipt.rgbEvidence.sha256, hash(receipt.rgbEvidence.file));
        assert.equal(receipt.rawSha256, hash('raw.json'));
        assert.equal(receipt.fixtureRemoved, true);
        assert.equal(readFileSync(join(output, 'rgb-snapshots.jsonl'), 'utf8'), '');
        assert.ok(!existsSync(join(output, 'rgb-observations.json')));
        const ascii = JSON.parse(readFileSync(join(output, receipt.file)));
        assert.ok(ascii.snapshots[0].screen.rows * ascii.snapshots[0].screen.cols > 8192);
        assert.ok(JSON.parse(readFileSync(join(output, 'raw.json'))).observations.length > 0);
        const settings = JSON.parse(readFileSync(join(output, 'initial-settings.json')));
        assert.equal(settings.FontSize, 10);
        assert.ok(!existsSync(dirname(settings.Profiles[0].Command)));
        assertRetainedCollector(output);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

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
    assert.ok(!existsSync(resolve(directory, 'cell-receipt.json')), 'A failed run cannot finalize cell evidence.');
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
                assertRetainedCollector(output);
                assert.equal(JSON.parse(readFileSync(join(output, 'ownership-proof.json'))).completed, true);
                assert.match(JSON.parse(readFileSync(join(output, 'failure.json'))).error, /Interrupted by SIGINT/);
                assert.ok(!existsSync(join(output, 'cell-receipt.json')), 'Interruption after collection cannot finalize cell evidence.');
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
        // Recorded from a fresh npm ci install verified against the checked-in lock.
        assert.equal(JSON.parse(lock).packages['node_modules/commander'].version, '15.0.0');
        assert.equal(createHash('sha256').update(readFileSync(join(dependency, 'index.js'))).digest('hex'),
            '3b1110c896b70704627d415e6658953a5a6f099e0b141311c73dfb06e55bdbcc');
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

test('owned cleanup waits for a proved worker that ignores SIGTERM and PTY hangup', {
    skip: !prepared || !process.env.NTILDE_TERMINFO_TEST_EXECUTABLE,
}, async () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-resistant-'));
    const log = join(root, 'lifecycle.jsonl');
    try {
        const result = await launch(process.env.NTILDE_TERMINFO_TEST_EXECUTABLE, false, { log, stubborn: true });
        assert.equal(result.code, 0, result.output + result.errors);
        const output = /results: ([^\n]+)/.exec(result.output)[1];
        const proof = JSON.parse(readFileSync(join(output, 'ownership-proof.json')));
        const worker = proof.proofs[0].worker;
        const events = readFileSync(log, 'utf8').trim().split('\n').map(line => JSON.parse(line));
        assert.ok(events.some(event => event.stubbornWorker?.pid === worker.pid));
        assert.ok(events.some(event => event.workerSignal === 'SIGTERM' && event.pid === worker.pid));
        const removal = events.find(event => event.removingFixture);
        assert.ok(removal, 'The fixture removal boundary must be observed.');
        assert.equal(removal.workerAlive, false, 'The worker must exit before fixture removal begins.');
        assert.throws(() => process.kill(worker.pid, 0), /ESRCH/, 'Cleanup must wait for the worker to exit.');
        assert.equal(proof.completed, true);
        assert.equal(proof.proofs[0].terminalOutput.outputProcessing, false);
        assert.equal(proof.proofs[0].terminalOutput.tool, '/bin/stty');
        assert.match(proof.proofs[0].terminalOutput.description, /-opost/);
        assert.ok(proof.cleanedUpAt);
        assertRetainedCollector(output);
        const settings = JSON.parse(readFileSync(join(output, 'initial-settings.json')));
        assert.ok(!existsSync(dirname(settings.Profiles[0].Command)));
        assert.equal(settings.AgentAccessObserveEnabled, true);
        assert.equal(settings.AgentAccessActEnabled, true);
        const receipt = JSON.parse(readFileSync(join(output, 'cell-receipt.json')));
        const digest = file => createHash('sha256').update(readFileSync(join(output, file))).digest('hex');
        assert.equal(receipt.fixtureRemoved, true);
        assert.equal(receipt.sha256, digest(receipt.file));
        assert.equal(receipt.contextSha256, digest('context.json'));
        assert.equal(receipt.ownershipProofSha256, digest('ownership-proof.json'));
        assert.equal(receipt.rawSha256, digest('raw.json'));
        const report = JSON.parse(readFileSync(join(output, receipt.file)));
        assert.equal(report.runId, receipt.runId);
        assert.equal(report.observations.length, 8);
        assert.deepEqual(report.snapshots, readFileSync(join(output, 'cell-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
        assert.deepEqual(report.snapshots[0].screen, report.snapshots.at(-1).screen);
        assert.ok(report.observations.every(item => item.result === 'passed'), JSON.stringify(report.observations));
        assert.equal(receipt.sgrEvidence.sha256, digest('sgr-observations.json'));
        const attributes = JSON.parse(readFileSync(join(output, 'sgr-observations.json')));
        assert.equal(attributes.runId, receipt.runId);
        assert.equal(attributes.observations.length, 17);
        assert.deepEqual(attributes.snapshots, readFileSync(join(output, 'sgr-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
        assert.deepEqual(attributes.snapshots[0].screen, attributes.snapshots.at(-1).screen);
        assert.equal(attributes.snapshots[0].paneId, report.snapshots[0].paneId);
        assert.ok(attributes.observations.every(item => item.result === 'passed'), JSON.stringify(attributes.observations));
        assert.equal(receipt.rgbEvidence.sha256, digest('rgb-observations.json'));
        const rgb = JSON.parse(readFileSync(join(output, 'rgb-observations.json')));
        assert.equal(rgb.runId, receipt.runId);
        assert.equal(rgb.observations.length, 20);
        assert.deepEqual(rgb.snapshots, readFileSync(join(output, 'rgb-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
        assert.deepEqual(rgb.snapshots[0].screen, rgb.snapshots.at(-1).screen);
        assert.equal(rgb.snapshots[0].paneId, report.snapshots[0].paneId);
        assert.ok(rgb.observations.every(item => item.result === 'passed'), JSON.stringify(rgb.observations));
        assert.equal(receipt.schemaVersion, 7);
        assert.equal(receipt.savedEvidence.sha256, digest('saved-observations.json'));
        const saved = JSON.parse(readFileSync(join(output, 'saved-observations.json')));
        assert.equal(saved.snapshots.length, 57);
        assert.ok(saved.observations.every(item => item.control.passed));
        const ris = saved.observations.find(item => item.featureId === 'saved.reset.full-both-banks');
        assert.ok(saved.observations.filter(item => item !== ris).every(item => item.result === 'passed'));
        assert.ok(ris.assertions.filter(item => item.stageId !== 'full-reset').every(item => item.passed));
        assert.notEqual(ris.result, 'inconclusive'); // The known RIS cell-clearing negative remains visible for #773.
        const savedCounts = saved.observations.reduce((counts, item) => ({ ...counts, [item.result]: (counts[item.result] ?? 0) + 1 }), {});
        assert.deepEqual(receipt.savedEvidence.counts, savedCounts);
        assert.equal(saved.snapshots[0].paneId, report.snapshots[0].paneId);
        assert.deepEqual(saved.snapshots[0].screen, saved.snapshots.at(-1).screen);
        assert.deepEqual(saved.snapshots, readFileSync(join(output, 'saved-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
        assert.equal(receipt.resizeEvidence.sha256, digest('resize-observations.json'));
        const resized = JSON.parse(readFileSync(join(output, 'resize-observations.json')));
        assert.equal(resized.snapshots.length, 12); assert.equal(resized.actions.length, 6);
        assert.deepEqual(receipt.resizeEvidence.counts, { passed: 3 });
        assert.equal(resized.snapshots[0].paneId, report.snapshots[0].paneId);
        assert.deepEqual(resized.snapshots[0].screen, resized.snapshots.at(-1).screen);
        assert.deepEqual(resized.actions, readFileSync(join(output, 'resize-actions.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
        assert.equal(receipt.pixelEvidence.sha256, digest('pixel-observations.json'));
        const pixels = JSON.parse(readFileSync(join(output, 'pixel-observations.json')));
        assert.equal(pixels.snapshots.length, 40);
        assert.equal(pixels.observations.length, 25);
        assert.deepEqual(pixels.snapshots, readFileSync(join(output, 'pixel-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
        assert.equal(pixels.snapshots[0].pixel.sha256, pixels.snapshots.at(-1).pixel.sha256);
        for (const frame of pixels.snapshots) assert.equal(frame.pixel.sha256, digest(frame.pixel.file));
        assert.ok(pixels.observations.filter(item => item.featureId !== 'rendition.bold').every(item => item.result === 'passed'), JSON.stringify(pixels.observations));
        assert.equal(receipt.colorEvidence.sha256, digest('color-observations.json'));
        const colors = JSON.parse(readFileSync(join(output, 'color-observations.json')));
        assert.equal(colors.snapshots.length, 35);
        assert.equal(colors.observations.length, 10);
        assert.deepEqual(colors.snapshots, readFileSync(join(output, 'color-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
        assert.deepEqual(receipt.colorEvidence.counts, { passed: 10 });
        assert.equal(colors.snapshots[0].pixel.sha256, colors.snapshots.at(-1).pixel.sha256);
        for (const frame of colors.snapshots) assert.equal(frame.pixel.sha256, digest(frame.pixel.file));
        assert.ok(colors.observations.every(item => item.result === 'passed'), JSON.stringify(colors.observations));
        for (const [prefix, count, frames] of [['erase', 13, 29], ['editing', 22, 47], ['history', 30, 63]]) {
            const binding = receipt[prefix + 'Evidence'];
            assert.equal(binding.sha256, digest(prefix + '-observations.json'));
            const extra = JSON.parse(readFileSync(join(output, binding.file)));
            assert.equal(extra.runId, receipt.runId); assert.equal(extra.observations.length, count);
            assert.equal(extra.snapshots.length, frames);
            assert.deepEqual(extra.snapshots, readFileSync(join(output, prefix + '-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
            assert.deepEqual(extra.snapshots[0].screen, extra.snapshots.at(-1).screen);
            assert.equal(extra.snapshots[0].paneId, report.snapshots[0].paneId);
            assert.ok(extra.snapshots.every(frame => frame.screen.bufferState && frame.screen.history));
            assert.equal(extra.observations.filter(item => item.result === 'inconclusive').length, 0);
        }
        for (const [prefix, count] of [['rendition', 19], ['cursor', 9]]) {
            assert.equal(receipt[prefix + 'Evidence'].sha256, digest(prefix + '-observations.json'));
            const extra = JSON.parse(readFileSync(join(output, prefix + '-observations.json')));
            assert.equal(extra.runId, receipt.runId);
            assert.equal(extra.observations.length, count);
            assert.deepEqual(extra.snapshots, readFileSync(join(output, prefix + '-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)));
            assert.deepEqual(extra.snapshots[0].screen, extra.snapshots.at(-1).screen);
            assert.equal(extra.snapshots[0].paneId, report.snapshots[0].paneId);
            assert.ok(extra.observations.every(item => item.result === 'passed'), JSON.stringify(extra.observations));
        }
    } finally {
        if (existsSync(log)) {
            const events = readFileSync(log, 'utf8').trim().split('\n').map(line => JSON.parse(line));
            const worker = events.find(event => event.stubbornWorker);
            if (worker) await terminateRecordedWorker(worker.stubbornWorker, worker.runId);
        }
        rmSync(root, { recursive: true, force: true });
    }
});

for (const runner of ['owned', 'inline']) {
    test(`${runner} runner rejects a changed HTTPS dependency archive before evidence or npm execution`, { skip: !prepared }, () => {
        withCollectorFixture(upstreamReceipt, true, root => {
            const tool = join(root, 'artifacts/terminfo/tool');
            const lock = JSON.parse(readFileSync(join(tool, 'package-lock.json')));
            lock.packages['node_modules/commander'].resolved = 'https://example.invalid/changed.tgz';
            writeFileSync(join(tool, 'package-lock.json'), JSON.stringify(lock));
            if (runner === 'owned') return assertRejectedBeforeRun(root, /checked-in pin/);
            const result = runInlinePreparation(join(root, 'scripts/terminfo-baseline.mjs'));
            assert.equal(result.status, 1, result.stdout + result.stderr);
            assert.match(result.stderr, /checked-in pin/);
            assert.deepEqual(readdirSync(join(root, 'artifacts/terminfo')).filter(name => name.startsWith('run-')), []);
        });
    });
}


test('an initial pixel detail refusal after restored cursor evidence retains typed unavailability and cleanup', {
    skip: !prepared || !process.env.NTILDE_TERMINFO_TEST_EXECUTABLE,
}, async () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-pixel-budget-'));
    try {
        const log = join(root, 'lifecycle.jsonl');
        const result = await launch(process.env.NTILDE_TERMINFO_TEST_EXECUTABLE, false, { log, pixelBudget: true });
        assert.equal(result.code, 0, result.output + result.errors);
        const output = /results: ([^\n]+)/.exec(result.output)[1];
        const receipt = JSON.parse(readFileSync(join(output, 'cell-receipt.json')));
        assert.equal(receipt.fixtureRemoved, true);
        assert.equal(receipt.pixelEvidence.status, 'unavailable');
        assert.equal(receipt.pixelEvidence.reason, 'screenDetailsUnavailable');
        assert.deepEqual(receipt.pixelEvidence.counts, {});
        assert.equal(receipt.colorEvidence.status, 'unavailable');
        assert.equal(readFileSync(join(output, 'color-snapshots.jsonl'), 'utf8'), '');
        for (const prefix of ['erase', 'editing', 'history']) {
            assert.equal(receipt[prefix + 'Evidence'].status, 'unavailable');
            assert.deepEqual(receipt[prefix + 'Evidence'].counts, {});
            assert.equal(readFileSync(join(output, prefix + '-snapshots.jsonl'), 'utf8'), '');
            assert.equal(JSON.parse(readFileSync(join(output, prefix + '-unavailable.json'))).fixtureStarted, false);
        }
        assert.equal(receipt.cursorEvidence.counts.passed, 9);
        assert.equal(JSON.parse(readFileSync(join(output, 'pixel-unavailable.json'))).fixtureStarted, false);
        assert.equal(readFileSync(join(output, 'pixel-snapshots.jsonl'), 'utf8'), '');
        assert.ok(!existsSync(join(output, 'pixels')));
        assert.ok(!existsSync(join(output, 'pixel-observations.json')));
        assert.ok(readFileSync(log, 'utf8').includes('pixelBudgetRefusal'));
    } finally { rmSync(root, { recursive: true, force: true }); }
});
