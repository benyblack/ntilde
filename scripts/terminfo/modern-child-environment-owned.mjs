import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver } from './agent-observer.mjs';
import { names, parentValues, parseChildOutput, requireEnvironment, scope, snapshotEnvironment } from './modern-child-environment.mjs';
import { assertRetainedSourceClosure, capturedChildEnvironmentSourceFiles, capturedChildEnvironmentSourcePolicy } from './owned-sources.mjs';

export { scope };
export const workerFile = 'modern-child-environment-worker.mjs';
export const prefix = 'mce-';
const launchFile = 'launch-inputs.json';
const outputFile = 'child-environment.json';
const paneFile = 'child-pane.json';
const receiptFile = 'child-environment-receipt.json';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const isRunId = value => /^[a-f\d]{32}$/.test(value ?? '');
const isGuid = value => /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(value ?? '');
const files = ['Ntilde.dll', 'Ntilde.Pty.dll', 'librusty_pty.dylib'];

const read = (directory, file) => readFileSync(join(directory, file));
const json = (directory, file) => JSON.parse(read(directory, file));
const write = (directory, file, bytes) => writeFileSync(join(directory, file), bytes, { flag: 'wx', mode: 0o600 });
const writeJson = (directory, file, value) => write(directory, file, JSON.stringify(value, null, 2) + '\n');
const appBinaries = executable => Object.fromEntries(files.map(file => [file, hash(read(dirname(executable), file))]));

export function workerProfile({ node, workerScript, configPath, runId }) {
    return { Command: node, Arguments: `"${workerScript}" "${configPath}" --ntilde-terminfo-worker=${runId}` };
}

export function prepareLaunchEnvironment(run, env) {
    const control = process.env.NTILDE_TERMINFO_ENVIRONMENT_CONTROL;
    const values = parentValues(control);
    const launchEnv = { ...env };
    for (const name of [...names, 'NODE_OPTIONS', 'NODE_PATH', 'NTILDE_TERMINFO_ENVIRONMENT_CONTROL']) delete launchEnv[name];
    for (const name of names) {
        if (values[name].state === 'present') launchEnv[name] = values[name].value;
    }
    const { runId, output, executable, appData } = run;
    const profile = json(output, 'initial-settings.json').Profiles[0];
    writeJson(output, launchFile, { runId, control, appData, executable, inputs: snapshotEnvironment(launchEnv),
        nodePreloads: { optionsAbsent: !Object.hasOwn(launchEnv, 'NODE_OPTIONS'), pathAbsent: !Object.hasOwn(launchEnv, 'NODE_PATH') },
        appBinaries: appBinaries(executable), executableSha256: hash(readFileSync(executable)), nodeSha256: hash(readFileSync(profile.Command)) });
    return launchEnv;
}

export class Collection {
    constructor(run) {
        this.run = run;
        this.restored = false;
    }

    async observe(request, budget) {
        const { runId, app, appData, executable, output, profileName, proofs } = this.run;
        requireEnvironment(request.kind === prefix + 'environment' && !this.restored, 'unexpected or duplicate child output');
        parseChildOutput(request.raw);
        write(output, outputFile, request.raw);
        const observer = new OwnedCellObserver({ appData, ownerPid: app.pid, profileName });
        const session = await observer.session(budget);
        const { paneId, kind, rows, cols, isActive } = session;
        requireEnvironment(isActive && same(appBinaries(executable), json(output, launchFile).appBinaries), 'inactive pane or changed App binaries');
        writeJson(output, paneFile, { runId, appPid: app.pid, workerPid: request.workerPid, tty: request.tty,
            paneId, kind, rows, cols, isActive, profileName: session.profileName, at: new Date().toISOString() });
        requireEnvironment(proofs[0]?.worker.pid === request.workerPid, 'child output came from another worker');
        this.restored = true;
        return {};
    }
}

function verifySources(output, context) {
    const raw = read(output, 'source-snapshot.json'), manifest = JSON.parse(raw);
    const directory = join(output, 'sources'), files = Object.entries(capturedChildEnvironmentSourceFiles);
    requireEnvironment(manifest.schemaVersion === 1 && manifest.policy === capturedChildEnvironmentSourcePolicy
        && context.sourceSnapshotPolicy === manifest.policy && context.sourceSnapshotManifestSha256 === hash(raw)
        && Object.keys(manifest.hashes).length === files.length && readdirSync(directory).length === files.length
        && same(json(directory, 'owned-source-inventory.json'), capturedChildEnvironmentSourceFiles), 'incomplete captured source graph');
    for (const [field, name] of files) {
        requireEnvironment(context[field] === manifest.hashes[field] && context[field] === hash(read(directory, name)), 'changed source ' + name);
    }
    assertRetainedSourceClosure(directory, capturedChildEnvironmentSourceFiles);
}

function verifyProfile(context, settings, launch, pane, proof) {
    const profile = settings.Profiles?.[0];
    requireEnvironment(settings.Profiles?.length === 1 && isGuid(profile.Id) && settings.DefaultProfileId === profile.Id
        && profile.Name === 'terminfo disposable worker ' + context.runId && pane.profileName === profile.Name
        && profile.Type === 0 && join(profile.StartingDirectory, 'appdata') === launch.appData && settings.SessionPersistence === 'Off'
        && settings.CommandAssistShellIntegrationEnabled === false && settings.CommandAssistPowerShellIntegrationEnabled === false
        && settings.AgentAccessObserveEnabled === true && settings.AgentAccessActEnabled === false && settings.AllowOsc52ClipboardWrite === false,
    'changed direct-local profile or settings');
    const expected = workerProfile({ node: proof.worker.executable, workerScript: join(context.sourceSnapshotRoot, 'scripts', 'terminfo', workerFile),
        configPath: join(profile.StartingDirectory, 'worker.json'), runId: context.runId });
    requireEnvironment(profile.Command === expected.Command && profile.Arguments === expected.Arguments,
        'profile did not execute the retained worker without a shell');
}

function verifyOwnership(context, ownership, pane) {
    const [proof] = ownership.proofs;
    const terminal = proof?.privateClipboard;
    const times = [context.startedAt, proof?.at, proof?.terminalOutput?.verifiedAt, pane.at, terminal?.restoredAt,
        ownership.cleanedUpAt, ownership.sourceSnapshot?.removedAt].map(time => Date.parse(time));
    requireEnvironment(times.every((time, index) => Number.isFinite(time) && (index === 0 || time >= times[index - 1])),
        'stale or out-of-order ownership observations');
    requireEnvironment(ownership.completed === true && same(ownership.collectionScope, scope)
        && !ownership.cleanupError && !ownership.persistentDaemon && ownership.proofs.length === 1
        && proof.app.pid === context.ownerPid && proof.app.executable === context.executable && proof.worker.ppid === proof.app.pid
        && proof.app.uid === proof.worker.uid && context.runId === pane.runId && pane.appPid === proof.app.pid
        && pane.workerPid === proof.worker.pid && pane.tty === proof.tty && proof.tty === '/dev/' + proof.worker.tty
        && typeof terminal?.originalTerminalState === 'string' && terminal.originalTerminalState === terminal.restoredTerminalState
        && proof.terminalOutput?.outputProcessing === false && ownership.sourceSnapshot.policy === context.sourceSnapshotPolicy
        && ownership.sourceSnapshot.root === context.sourceSnapshotRoot, 'missing direct-local ownership, terminal restoration or cleanup');
    requireEnvironment(pane.kind === 'local' && pane.isActive && isGuid(pane.paneId)
        && [pane.rows, pane.cols].every(value => Number.isSafeInteger(value) && value > 0), 'missing owned pane identity');
    return proof;
}

function derive(output) {
    requireEnvironment(!['failure.json', 'cleanup-failure.json'].some(file => existsSync(join(output, file))), 'capture or cleanup failed');
    const inputs = ['context.json', 'ownership-proof.json', 'initial-settings.json', launchFile, outputFile, paneFile]
        .map(file => [file, read(output, file)]);
    const [context, ownership, settings, launch, child, pane] = inputs.map(([, bytes], index) =>
        index === 4 ? parseChildOutput(bytes.toString()) : JSON.parse(bytes));
    requireEnvironment(isRunId(context.runId) && same(context.collectionScope, scope) && launch.runId === context.runId
        && same(launch.inputs, parentValues(launch.control))
        && same(launch.nodePreloads, { optionsAbsent: true, pathAbsent: true }), 'invalid controlled launch');
    requireEnvironment(context.executable === launch.executable && isDigest(context.executableSha256)
        && context.executableSha256 === launch.executableSha256 && isDigest(launch.nodeSha256)
        && same(Object.keys(launch.appBinaries), files) && files.every(file => isDigest(launch.appBinaries[file]))
        && context.assemblySha256 === launch.appBinaries['Ntilde.dll'], 'invalid App or Node identity');
    requireEnvironment(hash(inputs[2][1]) === context.initialSettingsSha256, 'changed settings bytes');
    const proof = verifyOwnership(context, ownership, pane);
    verifyProfile(context, settings, launch, pane, proof);
    verifySources(output, context);
    return { child, context, ownership, launch, pane,
        receipt: { schemaVersion: 1, runId: context.runId, fixtureRemoved: true, verdict: 'collected',
            sha256: Object.fromEntries(inputs.map(([name, bytes]) => [name, hash(bytes)])) } };
}

export function completeReceipt(output, scratch) {
    requireEnvironment(!existsSync(scratch), 'disposable fixture remains');
    const { receipt } = derive(output);
    writeJson(output, receiptFile, receipt);
    return receipt;
}

export function verifyChildEnvironmentRun(output) {
    const derived = derive(output);
    requireEnvironment(same(json(output, receiptFile), derived.receipt), 'environment receipt does not rederive');
    return derived;
}
