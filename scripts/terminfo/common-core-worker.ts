// Runs inside the disposable application's PTY. All probe decisions remain upstream-owned.
import { pollCommonCore } from './common-core-poll.mjs';
import { publishCommonCoreJson } from './common-core-handshake.mjs';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';

const configPath = process.argv[2];
if (!configPath) throw new Error('Expected a comparison configuration path.');
const config = JSON.parse(readFileSync(configPath, 'utf8'));
/** Atomically publish a complete worker record. */
const write = (name: string, value: unknown) => publishCommonCoreJson(config.output, name, value);
/** Import only from the parent-recorded upstream source tree. */
const upstream = (path: string) => import(pathToFileURL(join(config.upstream, path)).href);
try {
    const tty = String(
        execFileSync('/usr/bin/tty', { encoding: 'utf8', timeout: 5000, stdio: [0, 'pipe', 'pipe'] }),
    ).trim();
    const size = String(
        execFileSync('/bin/stty', ['size'], { encoding: 'utf8', timeout: 5000, stdio: [0, 'pipe', 'pipe'] }),
    )
        .trim()
        .split(/\s+/)
        .map(Number);
    write('worker-ready.json', {
        runId: config.runId,
        pid: process.pid,
        tty,
        geometry: { rows: size[0], cols: size[1] },
    });
    const ownerPath = join(config.output, 'worker-owner.json');
    await pollCommonCore(() => existsSync(ownerPath), {
        timeoutMs: 90000,
        message: 'The launched worker received no parent ownership proof.',
    });
    const owner = JSON.parse(readFileSync(ownerPath, 'utf8'));
    if (owner.runId !== config.runId || owner.pid !== process.pid || owner.tty !== tty)
        throw new Error('Parent ownership response belongs to a different worker or PTY.');
    const measuredSize = String(
        execFileSync('/bin/stty', ['size'], { encoding: 'utf8', timeout: 5000, stdio: [0, 'pipe', 'pipe'] }),
    )
        .trim()
        .split(/\s+/)
        .map(Number);
    if (measuredSize[0] !== 41 || measuredSize[1] !== 140)
        throw new Error(`Expected the common 41x140 PTY, observed ${measuredSize.join('x')}`);
    write('measurement-geometry.json', {
        runId: config.runId,
        pid: process.pid,
        tty,
        geometry: { rows: measuredSize[0], cols: measuredSize[1] },
        source: 'stty size on the actual worker stdin after parent ownership and launch configuration',
    });
    const { ensureSourceSuiteEnvironment } = await upstream('packages/admin/versions.ts');
    const suite = ensureSourceSuiteEnvironment();
    if (suite.TERMINFO_PROBE_HASH !== config.probeHash)
        throw new Error('Upstream source changed after preparing the comparison.');
    const { collectProbeRun } = await upstream('packages/terminfo.dev/src/serve.ts');
    const draft = await collectProbeRun({
        ids: config.scheduledIds,
        captureRunId: config.runId,
        expectedLaunchRunId: config.runId,
        intendedSubject: { kind: 'term', id: config.appId, version: config.version, os: 'macos' },
        ...owner.assertion,
    });
    const { finalizeCollectedDraft } = await upstream('packages/terminfo.dev/src/capture-writer.ts');
    const run = finalizeCollectedDraft(draft, { kind: 'term', id: config.appId, version: config.version });
    write('raw.json', run);
    write('worker-complete.json', { runId: config.runId, pid: process.pid, completedAt: new Date().toISOString() });
} catch (error) {
    write('worker-error.json', {
        name: error instanceof Error ? error.name : 'Error',
        message: String(error),
        stack: error instanceof Error ? error.stack : undefined,
    });
    process.exitCode = 1;
}
