import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { OwnedInputObserver } from './input-capture.mjs';
import { generateOwnedInput } from './input-action.mjs';
import { buildInputReport } from './input-state.mjs';
import { assertRetainedSourceClosure, capturedMok3SourceFiles, capturedMok3SourcePolicy } from './owned-sources.mjs';
import { baselineScope, planFor, scope } from './modify-other-keys-3-contracts.mjs';

export { baselineScope, scope };
export const prefix = 'input-', workerFile = 'modify-other-keys-3-worker.mjs', allowGeneratedInput = true;
const journalFile = 'mok3-journal.json', receiptFile = 'mok3-receipt.json';
const save = (output, name, value) => writeFileSync(join(output, name), JSON.stringify(value) + '\n', { flag: 'wx', mode: 0o600 });
const read = (output, name) => readFileSync(join(output, name));
const json = (output, name) => JSON.parse(read(output, name));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

export class Collection {
    constructor(run) {
        this.run = run;
        this.observer = new OwnedInputObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            nonce: 'NTILDE_IN_' + run.runId, plan: planFor(run.collectionScope.id),
            performInput: (event, budget) => generateOwnedInput({ ...run, paneId: this.observer.paneId,
                workerPid: run.identities.worker.pid, workerIdentity: run.identities.worker, tty: run.proofs[0].tty, uid: process.getuid() }, event, budget) });
    }
    async observe(request, budget) {
        const observer = this.observer;
        try {
            const result = await observer.handle(request, budget);
            if (observer.restored) save(this.run.output, journalFile, { frames: observer.frames, actions: observer.actions, seal: observer.seal });
            return result;
        } catch (error) {
            save(this.run.output, 'mok3-failure.json', { frames: observer.frames, actions: observer.actions, attempt: observer.lastAttempt, error: error.message });
            throw error;
        }
    }
    get restored() { return this.observer.restored; }
}

function verifySources(output, context) {
    const dir = join(output, 'sources');
    const manifest = json(output, 'source-snapshot.json');
    assert.equal(context.sourceSnapshotPolicy, capturedMok3SourcePolicy);
    assert.equal(manifest.policy, capturedMok3SourcePolicy);
    assert.equal(manifest.schemaVersion, 1);
    assert.deepEqual(json(dir, 'owned-source-inventory.json'), capturedMok3SourceFiles);
    const files = Object.entries(capturedMok3SourceFiles);
    assert.equal(readdirSync(dir).length, files.length);
    const hashes = Object.fromEntries(files.map(([field, name]) => [field, hash(read(dir, name))]));
    assert.deepEqual(manifest.hashes, hashes);
    for (const [field, digest] of Object.entries(hashes)) assert.equal(context[field], digest);
    assertRetainedSourceClosure(dir, capturedMok3SourceFiles);
}

export function deriveRun(output) {
    assert.ok(!['failure.json', 'cleanup-failure.json', 'mok3-failure.json', 'input-raw-failure.json'].some(file => existsSync(join(output, file))));
    const context = json(output, 'context.json'), proof = json(output, 'ownership-proof.json'), settings = json(output, 'initial-settings.json');
    const { frames, actions, seal } = json(output, journalFile);
    const id = context.collectionScope.id, plan = planFor(id);
    assert.deepEqual(context.collectionScope, id === baselineScope.id ? baselineScope : scope);
    assert.deepEqual(proof.collectionScope, context.collectionScope);
    assert.ok(proof.completed && !proof.cleanupError && !proof.persistentDaemon && proof.proofs.length === 1);
    const [owner] = proof.proofs, terminal = owner.privateClipboard;
    assert.ok(terminal.originalTerminalState && terminal.originalTerminalState === terminal.restoredTerminalState);
    assert.ok(Date.parse(terminal.restoredAt) <= Date.parse(proof.cleanedUpAt) && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt));
    assert.equal(proof.sourceSnapshot.policy, context.sourceSnapshotPolicy);
    assert.equal(proof.sourceSnapshot.root, context.sourceSnapshotRoot);
    assert.equal(context.ownerPid, owner.app.pid);
    assert.equal(owner.app.executable, context.executable);
    assert.ok(settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false
        && settings.AgentAccessObserveEnabled && settings.AgentAccessActEnabled && settings.Profiles.length === 1);
    const profile = 'terminfo disposable worker ' + context.runId;
    assert.equal(settings.Profiles[0].Name, profile);
    const report = buildInputReport(frames, actions, seal, context.runId, plan);
    const observations = report.observations.map((row, index) => {
        const item = report.inputs[index], action = actions[index].observed, app = action.app;
        assert.deepEqual(action.before.app, owner.app);
        assert.deepEqual(action.before.worker, owner.worker);
        assert.equal(action.before.pane.profileName, profile);
        const matches = row.result === 'passed' && app.handled === item.handled
            && (item.event.kind !== 'keyDown' || item.event.text === undefined || app.textHandled)
            && app.inputSubmitted === (item.expectedHex.length !== 0);
        return { id: item.id, role: item.role, event: item.event, level: item.modes.modifyOtherKeysLevel,
            expectedHex: row.expectedHex, actualHex: row.actualHex, handled: app.handled, textHandled: app.textHandled ?? null, matches };
    });
    const outcome = observations.some(row => row.role === 'control' && !row.matches) ? 'inconclusive'
        : observations.every(row => row.matches) ? 'supported' : 'unsupported';
    const files = Object.fromEntries([journalFile, 'context.json', 'ownership-proof.json', 'initial-settings.json', 'source-snapshot.json']
        .map(name => [name, hash(read(output, name))]));
    assert.equal(files['initial-settings.json'], context.initialSettingsSha256);
    assert.equal(files['source-snapshot.json'], context.sourceSnapshotManifestSha256);
    verifySources(output, context);
    const receipt = { schemaVersion: 1, suiteId: id, runId: context.runId, fixtureRemoved: true, files, verdict: outcome };
    return { context, frames, observations, receipt };
}

export function completeReceipt(output, scratch) {
    assert.ok(!existsSync(scratch));
    const { receipt } = deriveRun(output);
    save(output, receiptFile, receipt);
    return receipt;
}
