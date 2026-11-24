import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver } from './agent-observer.mjs';
import { gradeTitleCase, prefix, readStage, requireTitles, scope, titlePlans } from './modern-osc-titles.mjs';
import { assertRetainedSourceClosure, capturedOscTitlesSourceFiles, capturedOscTitlesSourcePolicy } from './owned-sources.mjs';
import { readOwnedOutputMode, validateOutputGeometry } from './terminal-output.mjs';

export { prefix, scope };
export const workerFile = 'modern-osc-titles-worker.mjs';
export const receiptFile = 'osc-titles-receipt.json';
export const reportFile = 'osc-titles-observations.json';
const journalFile = 'osc-titles-journal.jsonl';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const hasRestoredLabels = result => Object.values(result.restoration).every(status => status !== 'unproved');

export class Collection {
    constructor(run) {
        this.run = run;
        this.observer = new OwnedCellObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName });
        this.plans = titlePlans(run.runId);
        this.caseIndex = 0;
        this.stepIndex = 0;
        this.stages = {};
    }

    async observe(request, budget) {
        requireTitles(!this.busy && !this.restored, 'unexpected concurrent or completed label read');
        this.busy = true;
        const { caseId, phase, action, reads } = request;
        const frame = { caseId, phase, action, reads };
        try {
            const plan = this.plans[this.caseIndex], step = plan?.steps[this.stepIndex];
            requireTitles(caseId === plan?.id && phase === step?.phase, 'out-of-order label observation');
            if (request.kind === prefix + 'fault') {
                frame.partial = request.partial;
                throw new Error(request.error);
            }
            requireTitles(request.kind === prefix + 'stage', 'unexpected request kind');
            const { paneId, rows, cols, isActive } = await this.observer.session(budget);
            this.observer.paneId = paneId;
            frame.pane = { id: paneId, rows, cols };
            this.pane ??= frame.pane;
            frame.terminalOutput = readOwnedOutputMode(request.tty, budget());
            validateOutputGeometry(frame.terminalOutput.description, rows, cols);
            requireTitles(isActive, 'the direct-local pane is inactive');
            this.stages[phase] = readStage(frame, plan, step, this.pane);
            this.advance(plan);
            return {};
        } catch (error) {
            frame.error = error.message;
            throw error;
        } finally {
            this.busy = false;
            appendFileSync(join(this.run.output, journalFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
        }
    }

    advance(plan) {
        this.stepIndex++;
        if (this.stepIndex !== plan.steps.length) return;
        requireTitles(hasRestoredLabels(gradeTitleCase(plan, this.stages)), 'incoming label restoration was not proved');
        this.stages = {};
        this.stepIndex = 0;
        this.caseIndex++;
        this.restored = this.caseIndex === this.plans.length;
    }
}

function bytes(output, file) {
    const path = join(output, file), stat = lstatSync(path);
    requireTitles(stat.isFile() && stat.size > 0 && stat.size < 16 * 1024 * 1024, 'invalid retained file ' + file);
    return readFileSync(path);
}

const json = (output, file) => JSON.parse(bytes(output, file));

function verifySources(output, context) {
    const raw = bytes(output, 'source-snapshot.json'), manifest = JSON.parse(raw);
    const sources = join(output, 'sources'), files = Object.entries(capturedOscTitlesSourceFiles);
    requireTitles(manifest.schemaVersion === 1 && manifest.policy === capturedOscTitlesSourcePolicy
        && hash(raw) === context.sourceSnapshotManifestSha256 && Object.keys(manifest.hashes).length === files.length
        && readdirSync(sources).length === files.length && same(json(sources, 'owned-source-inventory.json'), capturedOscTitlesSourceFiles),
    'incomplete captured source graph');
    for (const [field, name] of files) {
        requireTitles(context[field] === manifest.hashes[field] && context[field] === hash(bytes(sources, name)), 'changed source ' + name);
    }
    assertRetainedSourceClosure(sources, capturedOscTitlesSourceFiles);
}

function verifyOwnership(context, ownership, settings) {
    const [proof] = ownership.proofs;
    const terminal = proof?.privateClipboard;
    requireTitles(context.sourceSnapshotPolicy === capturedOscTitlesSourcePolicy && same(context.collectionScope, scope)
        && /^[a-f\d]{32}$/.test(context.runId) && Number.isFinite(Date.parse(context.startedAt))
        && isDigest(context.executableSha256) && isDigest(context.assemblySha256), 'wrong run, App or source identity');
    requireTitles(settings.AllowTitleReport === true && settings.SessionPersistence === 'Off' && settings.Profiles?.length === 1
        && settings.Profiles[0].Name === 'terminfo disposable worker ' + context.runId && settings.AgentAccessObserveEnabled === true
        && settings.AgentAccessActEnabled === false && settings.AllowOsc52ClipboardWrite === false, 'changed disposable settings');
    requireTitles(ownership.proofs.length === 1 && same(ownership.collectionScope, scope) && ownership.completed === true
        && !ownership.cleanupError && !ownership.persistentDaemon && proof?.app.pid === context.ownerPid
        && proof.app.executable === context.executable && proof.worker.ppid === context.ownerPid
        && typeof terminal?.originalTerminalState === 'string' && terminal.originalTerminalState === terminal.restoredTerminalState
        && Date.parse(terminal.restoredAt) <= Date.parse(ownership.cleanedUpAt)
        && Date.parse(ownership.cleanedUpAt) <= Date.parse(ownership.sourceSnapshot?.removedAt)
        && ownership.sourceSnapshot.policy === capturedOscTitlesSourcePolicy && proof.terminalOutput?.outputProcessing === false,
    'missing direct-local ownership, terminal restoration or verified cleanup');
    return proof.terminalOutput;
}

function replay(journal, context, terminalOutput) {
    const frames = journal.toString().trimEnd().split('\n').map(line => JSON.parse(line));
    const pane = frames[0]?.pane;
    let index = 0;
    const cases = titlePlans(context.runId).map(plan => {
        const stages = Object.fromEntries(plan.steps.map(step => {
            const frame = frames[index++];
            const reading = readStage(frame, plan, step, pane);
            requireTitles(frame.terminalOutput?.outputProcessing === false, 'unverified stage PTY mode');
            validateOutputGeometry(frame.terminalOutput.description, pane.rows, pane.cols);
            return [step.phase, reading];
        }));
        const result = gradeTitleCase(plan, stages);
        requireTitles(hasRestoredLabels(result), result.reason);
        return result;
    });
    requireTitles(index === frames.length, 'extra label observations');
    validateOutputGeometry(terminalOutput.description, pane.rows, pane.cols);
    return { runId: context.runId, pane, cases };
}

function derive(output) {
    requireTitles(!existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json')), 'capture or cleanup failed');
    const contextBytes = bytes(output, 'context.json'), ownershipBytes = bytes(output, 'ownership-proof.json');
    const settingsBytes = bytes(output, 'initial-settings.json'), journal = bytes(output, journalFile);
    const context = JSON.parse(contextBytes), ownership = JSON.parse(ownershipBytes);
    requireTitles(hash(settingsBytes) === context.initialSettingsSha256, 'changed settings bytes');
    const terminalOutput = verifyOwnership(context, ownership, JSON.parse(settingsBytes));
    verifySources(output, context);
    const report = replay(journal, context, terminalOutput);
    const receipt = { schemaVersion: 1, runId: context.runId, contextSha256: hash(contextBytes), ownershipProofSha256: hash(ownershipBytes),
        journalSha256: hash(journal), fixtureRemoved: true, verdict: Object.fromEntries(report.cases.map(item => [item.id, item.verdict])) };
    return { report, receipt };
}

export function completeReceipt(output, scratch) {
    requireTitles(!existsSync(scratch), 'disposable fixture remains');
    const { report, receipt } = derive(output);
    for (const [name, value] of [[reportFile, report], [receiptFile, receipt]]) {
        writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    }
    return receipt;
}

export function verifyOscTitlesRun(output) {
    const derived = derive(output);
    requireTitles(same(json(output, receiptFile), derived.receipt) && same(json(output, reportFile), derived.report), 'run does not rederive');
    return derived;
}
