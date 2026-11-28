import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { Collection as ReportCollection, verifyDirectReportCleanup, verifySeal } from './sixel-reports-owned.mjs';
import { gridOf, verifyControl } from './sixel-reports-contracts.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { cursorStyle, exactReplies, nonce, planFor, prefix, requireIdentity, scope, verifySamples } from './device-identity-contracts.mjs';
import { assemblyFile, deriveVersion, metadataFile, readAssembly, sha256 } from './device-identity-metadata.mjs';

export { prefix, scope };
export const workerFile = 'device-identity-worker.mjs';
export const allowGeneratedInput = true;
export const imageOwnership = true;
const framesFile = 'identity-frames.jsonl';
const write = (directory, file, bytes) => writeFileSync(join(directory, file), bytes, { flag: 'wx', mode: 0o600 });
const save = (directory, file, value) => write(directory, file, JSON.stringify(value, null, 2) + '\n');

export function prepareLaunchEnvironment(run, env) {
    const file = join(dirname(run.executable), assemblyFile);
    const { evidence, version } = readAssembly(file);
    const image = readFileSync(file);
    requireIdentity(sha256(image) === evidence.sha256, 'sidecar VT assembly changed before launch');
    write(run.output, assemblyFile, image);
    save(run.output, metadataFile, evidence);
    run.context.identityVtAssemblySha256 = version.sha256;
    return env;
}

function verifyPane(frame, initial) {
    const screen = screenSnapshot(frame.screen, false, true, true, true, true);
    const metadata = copyPaneMetadata(frame.metadata);
    requireIdentity(frame.paneId === initial.paneId && metadata.paneId === frame.paneId
        && frame.sha256 === snapshotHash(screen) && frame.afterSha256 === frame.sha256
        && metadata.presentation.paneActive && metadata.presentation.windowVisible
        && same(gridOf(screen), gridOf(initial.screen)) && same(gridOf(screen), gridOf(metadata.buffer))
        && metadata.buffer.alternateScreen === screen.bufferState.alternateScreen
        && metadata.buffer.cursorRow === screen.cursorRow && metadata.buffer.cursorCol === screen.cursorCol,
    'App pane, paired snapshot, or buffer metadata differed');
}

function verifyFixture(screen, runId) {
    const marker = nonce(runId), state = screen.bufferState;
    requireIdentity(screen.lines[0] === marker && screen.lines.slice(1).every(line => line === '')
        && screen.cellDetails[0].count === marker.length && screen.cellDetails[0].fg === 1
        && screen.cellDetails[0].underlineStyle === 1 && screen.cursorRow === 2 && screen.cursorCol === 3
        && same(screen.cursorMode, cursorStyle.block) && state.alternateScreen && state.originMode
        && !state.autoWrap && state.scrollTop === 1 && state.scrollBottom === 5,
    'styled cells, displaced cursor, or prepared modes were not observed');
}

function verifyStage(frame, step, initial, previous, runId, used, replies) {
    requireIdentity(frame?.stage === step.stage && !Object.hasOwn(frame, 'error'), 'missing, failed, or out-of-order stage');
    validateImageOwnership(frame.ownership, initial.ownership, (previous ?? initial).screen);
    requireIdentity(!previous || Date.parse(previous.ownership.at) <= Date.parse(frame.ownership.at), 'stage time moved backward');
    if (step.stage === 'seal') {
        verifySeal(frame, previous);
        return null;
    }
    verifyPane(frame, initial);
    if (step.write) {
        requireIdentity(frame.requestBytes === step.write, 'fixture bytes changed');
        verifyControl(frame.sample, used);
    }
    if (step.stage === 'start' || step.stage === 'finish') {
        requireIdentity(pristineImageBaseline(frame.screen) && frame.sha256 === initial.sha256, 'original screen was not pristine or restored');
    } else if (step.stage === 'bind') {
        verifyFixture(frame.screen, runId);
    } else if (step.stage === 'beam') {
        requireIdentity(same(previous.screen.cursorMode, cursorStyle.block)
            && same(frame.screen.cursorMode, cursorStyle.beam)
            && same(frame.screen, { ...previous.screen, cursorMode: cursorStyle.beam })
            && same(frame.metadata, previous.metadata), 'cursor-style control did not change just the stored cursor style');
    } else {
        requireIdentity(frame.sha256 === previous.sha256 && same(frame.metadata, previous.metadata),
            'query changed cells, cursor, or pane state');
    }
    return step.sample ? verifySamples(step, frame.samples, used, replies) : null;
}

export class Collection extends ReportCollection {
    constructor(run) {
        super(run, { plan: planFor(run.runId), stagePrefix: prefix });
        this.replies = exactReplies(deriveVersion(read(run.output, assemblyFile), json(run.output, metadataFile)).version);
    }

    async observeStage(request, budget, ownership, step) {
        const frame = { stage: step.stage, ownership };
        try {
            if (step.stage === 'seal') {
                frame.attempts = request.attempts;
                requireIdentity(sha256(readFileSync(join(dirname(this.run.executable), assemblyFile)))
                    === this.run.context.identityVtAssemblySha256, 'sidecar VT assembly changed during collection');
            } else {
                if (step.write) Object.assign(frame, { requestBytes: request.requestBytes, sample: request.sample });
                if (step.sample) frame.samples = request.samples;
                Object.assign(frame, await this.capture(budget));
            }
            verifyStage(frame, step, this.frames[0] ?? frame, this.frames.at(-1), this.run.runId, this.used, this.replies);
        } catch (error) {
            frame.error = error.message;
            throw error;
        } finally {
            appendFileSync(join(this.run.output, framesFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
        }
        this.frames.push(frame);
        return {};
    }
}

export function deriveRun(output) {
    const context = json(output, 'context.json'), frames = journal(output, framesFile);
    const worker = verifyDirectReportCleanup(output, context, frames, { scope, require: requireIdentity });
    const metadata = deriveVersion(read(output, assemblyFile), json(output, metadataFile));
    requireIdentity(metadata.sha256 === context.identityVtAssemblySha256, 'VT assembly digest differs from the launch context');
    const plan = planFor(context.runId), used = new Set(), replies = exactReplies(metadata.version);
    requireIdentity(/^[a-f\d]{32}$/.test(context.runId) && frames.length === plan.length, 'incomplete pane journal');
    const exchanges = plan.map((step, index) =>
        verifyStage(frames[index], step, frames[0], frames[index - 1], context.runId, used, replies)).filter(Boolean);
    const report = { runId: context.runId, suite: scope.id, metadata, exchanges, paneId: frames[0].paneId, tty: worker.tty,
        appPid: worker.app.pid, workerPid: worker.worker.pid, restored: true };
    const inputs = [framesFile, assemblyFile, metadataFile, 'context.json', 'ownership-proof.json', 'source-snapshot.json', 'initial-settings.json'];
    const files = Object.fromEntries(inputs.map(file => [file, sha256(read(output, file))]));
    requireIdentity(files['source-snapshot.json'] === context.sourceSnapshotManifestSha256
        && files['initial-settings.json'] === context.initialSettingsSha256, 'source or initial settings digest differs');
    return { context, frames, report, receipt: { schemaVersion: 1, suite: scope.id, runId: context.runId, fixtureRemoved: true, files } };
}

export function completeReceipt(output, scratch) {
    requireIdentity(!existsSync(scratch), 'fixture remains');
    const { report, receipt } = deriveRun(output);
    save(output, 'identity-report.json', report);
    save(output, 'identity-receipt.json', receipt);
    return { verdict: Object.fromEntries(report.exchanges.map(row => [row.stage, row.outcome])) };
}
