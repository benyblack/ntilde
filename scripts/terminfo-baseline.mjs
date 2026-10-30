#!/usr/bin/env node
// Opt-in desktop observations. Never run this in a CI pseudo-terminal: that
// would measure the harness rather than Ntilde. Requires Node 24+ and npm.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkoutRevision, readCollectorLockfile, resolveNpmCli } from './terminfo/runtime.mjs';
import { validateCollectorReceipt } from './terminfo/collector.mjs';

const cliVersion = '4.0.0';
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');

function fail(message) {
    throw new Error(message);
}

function json(path) {
    return JSON.parse(readFileSync(path, 'utf8'));
}

function sha256(path) {
    return createHash('sha256').update(readFileSync(path)).digest('hex');
}

function writeJson(path, value) {
    writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
}

function run(args, env, capture = false) {
    const result = spawnSync(process.execPath, args, {
        env, stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit', encoding: 'utf8',
    });
    if (result.error) throw result.error;
    if (result.status !== 0) fail(`Collector exited ${result.status ?? result.signal}: ${result.stderr ?? ''}`);
    return result.stdout;
}

function validateInvocation(args) {
    if (args.length !== 2 || args[0] !== '--app-version' || !/^\d[\dA-Za-z.+_-]*$/.test(args[1])) {
        fail('Supply --app-version VERSION; use --help for usage.');
    }
    if (Number(process.versions.node.split('.')[0]) < 24) fail('Node 24+ is required.');
    if (!process.stdin.isTTY || !process.stdout.isTTY) fail('Run directly in a Ntilde tab with interactive stdin and stdout.');
    if (process.env.TERM_PROGRAM !== 'Ntilde') fail('TERM_PROGRAM must be Ntilde; open a local Ntilde tab.');
    if (process.env.TMUX || process.env.STY || process.env.SSH_CONNECTION || process.env.SSH_TTY) {
        fail('Use a direct local tab: this baseline does not identify remote or multiplexer paths.');
    }
}

function prepareCollector(tool, pkgManifest, env) {
    if (!existsSync(pkgManifest)) {
        if (process.platform === 'win32') fail('Install the collector first using the Windows preparation command in docs/qa/QA_TERMINFO.md.');
        // Execute the selected Node installation's npm CLI directly. Never search PATH.
        run([resolveNpmCli(), 'install', '--prefix', tool, '--ignore-scripts', '--no-audit', '--no-fund', '--package-lock=true',
            '--save-exact', `terminfo.dev@${cliVersion}`], env);
    }
    if (json(pkgManifest).version !== cliVersion) fail(`Expected terminfo.dev ${cliVersion}; remove artifacts/terminfo/tool and retry.`);
    return readCollectorLockfile(tool);
}

function main() {
    const args = process.argv.slice(2);
    if (args.length === 1 && ['--help', '-h'].includes(args[0])) {
        console.log(`Usage: node scripts/terminfo-baseline.mjs --app-version VERSION

Run inside a local Ntilde tab with Node 24+ and an npm installation.
VERSION must identify the running app (About/build log), not the checkout.
Installs terminfo.dev@${cliVersion} under artifacts/terminfo/tool if needed.
Saves raw observations, detection, suite receipt, dependency lock, context and
summary in a new private artifacts/terminfo/run-* directory. Nothing is uploaded.
State-changing probes may be refused by the collector; these are inconclusive.
See docs/qa/QA_TERMINFO.md.`);
        return;
    }
    validateInvocation(args);

    const root = join(repo, 'artifacts', 'terminfo');
    const tool = join(root, 'tool');
    const pkg = join(tool, 'node_modules', 'terminfo.dev');
    const pkgManifest = join(pkg, 'package.json');
    mkdirSync(root, { recursive: true });
    // Child tool invocations inherit the selected Node runtime first.
    const env = { ...process.env, PATH: dirname(process.execPath) + (process.platform === 'win32' ? ';' : ':') + process.env.PATH };
    const lockBytes = prepareCollector(tool, pkgManifest, env);

    const bundle = join(pkg, 'dist', 'terminfo.bundle.mjs');
    const receiptPath = join(pkg, 'dist', 'terminfo.bundle.receipt.json');
    const receiptBytes = readFileSync(receiptPath);
    const receipt = JSON.parse(receiptBytes);
    validateCollectorReceipt(readFileSync(bundle), receipt);
    // The upstream launcher prefers Bun if installed. Execute the checked bundle
    // with this explicit Node instead so the recorded runtime is the one used.
    const inheritedHints = Object.fromEntries(['TERM_PROGRAM', 'TERM_PROGRAM_VERSION', '__CFBundleIdentifier', 'TERM', 'COLORTERM']
        .map(key => [key, process.env[key] ?? null]));
    delete env.__CFBundleIdentifier; // Upstream checks this before TERM_PROGRAM.
    env.TERM_PROGRAM_VERSION = args[1]; // Never inherit the launching terminal's version.

    const directory = mkdtempSync(join(root, 'run-'));
    const context = {
        schemaVersion: 1, startedAt: new Date().toISOString(),
        appVersion: args[1], appVersionSource: 'operator-supplied; not independently verified',
        checkoutRevision: checkoutRevision(repo),
        nodeVersion: process.version, cliVersion, receipt, inheritedHints,
        geometry: { columns: process.stdout.columns, rows: process.stdout.rows },
        rawCommand: ['node', 'terminfo.bundle.mjs', 'test', '--output', 'raw.json'],
    };
    writeJson(join(directory, 'context.json'), context);
    writeFileSync(join(directory, 'suite-receipt.json'), receiptBytes, { flag: 'wx', mode: 0o600 });
    writeFileSync(join(directory, 'package-lock.json'), lockBytes, { flag: 'wx', mode: 0o600 });
    console.log(`Recording terminfo.dev ${cliVersion} observations in ${directory}`);
    const detected = JSON.parse(run([bundle, 'detect', '--json'], env, true));
    writeJson(join(directory, 'detect.json'), detected);
    if (detected.name !== 'ntilde' || detected.version !== args[1]) fail(`Unexpected collector identity: ${JSON.stringify(detected)}. Context retained in ${directory}`);

    const rawPath = join(directory, 'raw.json');
    run([bundle, 'test', '--output', rawPath], env);
    const raw = json(rawPath);
    if (raw.schemaVersion !== 2 || raw.target?.id !== 'ntilde' || raw.target?.version !== args[1]
        || raw.probeHash !== receipt.probeHash || raw.sourceRevision !== receipt.collectorRevision) {
        fail(`Unexpected raw identity or suite. Originals retained in ${directory}`);
    }
    const counts = {};
    const reasons = {};
    for (const observation of raw.observations) {
        counts[observation.outcome] = (counts[observation.outcome] ?? 0) + 1;
        if (observation.reason) reasons[observation.reason] = (reasons[observation.reason] ?? 0) + 1;
    }
    const summary = {
        runId: raw.runId, measuredAt: raw.measuredAt, target: raw.target,
        identity: raw.identity, suiteId: raw.suiteId, suiteComplete: raw.suiteComplete,
        rawSha256: sha256(rawPath), observations: raw.observations.length, counts, reasons,
    };
    writeJson(join(directory, 'summary.json'), summary);
    console.log(JSON.stringify(summary, null, 2));
    console.log(`Saved ${directory}. Inconclusive and unattempted checks are not failures; query results do not prove rendering or input behavior.`);
}

try {
    main();
} catch (error) {
    console.error(`[terminfo] ${error.message}`);
    process.exitCode = 1;
}
