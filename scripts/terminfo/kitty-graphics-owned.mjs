import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedStaticImageObserver } from './static-images-capture.mjs';
import { parseStaticKittyReply, staticImageAnchor } from './static-images-cases.mjs';
import { staticImageEnvelope } from './static-images-oracle.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { checkImages, imageRestore, imageSetup, StaticImageReport, staticImageSuite } from './static-images.mjs';
import { pngHash } from './pixels-capture.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-kitty-graphics-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'kgraphics-';
export const workerFile = 'kitty-graphics-worker.mjs';
export const imageOwnership = true;
export const caseIds = Object.freeze(['extensions.kitty-graphics.transmit', 'extensions.kitty-graphics.display']);
const [transmit, display] = caseIds;
const rgb = Buffer.from([255, 0, 0]);
const apc = body => '\x1b_G' + body + '\x1b\\';
const commands = {
    upload: ({ number }) => apc('a=t,f=24,s=1,v=1,t=d,I=' + number + ';' + rgb.toString('base64')),
    place: ({ firstId }) => apc('a=p,i=' + firstId + ',c=2,r=1,C=1'),
    delete: ({ firstId }) => apc('a=d,d=I,i=' + firstId + ',q=2'),
};
const stage = (caseId, stageId) => ({ kind: 'read', caseId, stageId });
export const plan = Object.freeze([
    { kind: 'bind' }, stage(transmit, 'upload'), stage(transmit, 'delete'),
    stage(display, 'upload'), stage(display, 'place'), stage(display, 'delete'), { kind: 'finish' },
].map(Object.freeze));

function action(entry, state, runId, cursorVisible) {
    if (entry.kind === 'bind') return { command: imageSetup(runId), skipReason: null };
    if (entry.kind === 'finish') return { command: imageRestore(cursorVisible), skipReason: null };
    const skipped = entry.stageId !== 'upload' && state.firstId === null;
    return { command: skipped ? '' : commands[entry.stageId](state), skipReason: skipped ? 'no correlated allocation' : null };
}

function acceptReply(bytes, entry, states) {
    if (!bytes.length) return;
    const raw = bytes.toString('ascii'), state = states.get(entry.caseId);
    const { fields, status } = parseStaticKittyReply(raw);
    if (entry.stageId === 'upload') {
        checkImages(fields.I === state.number && raw === apc('i=' + fields.i + ',I=' + fields.I + ';' + status));
        if (status === 'OK') {
            checkImages(fields.i > 0);
            state.firstId = fields.i;
        }
    } else {
        checkImages(entry.stageId === 'place' && fields.i === state.firstId && raw === apc('i=' + fields.i + ';' + status));
    }
}

const cells = Object.freeze({ cols: 2, rows: 1 });
// Derive changed bounds from the PNGs. The source is a solid red square fitted
// inside the requested cells. A one-pixel rounding tolerance permits fractional scale.
export function redPixels(before, after, grid) {
    const envelope = staticImageEnvelope(before, grid, staticImageAnchor, cells);
    checkImages(after.width === before.width && after.height === before.height);
    let left = Infinity, top = Infinity, right = -1, bottom = -1;
    let changed = 0, outside = 0, otherColor = 0, background = true;
    for (let y = 0; y < before.height; y++) {
        for (let x = 0; x < before.width; x++) {
            const offset = (y * before.width + x) * 4;
            const within = x >= envelope.x && x < envelope.x + envelope.width && y >= envelope.y && y < envelope.y + envelope.height;
            const initial = before.pixels.readUInt32BE(offset), observed = after.pixels.readUInt32BE(offset);
            if (within && initial !== 0x101010ff) background = false;
            if (initial === observed) continue;
            if (!within) { outside++; continue; }
            changed++;
            if (observed !== 0xff0000ff) otherColor++;
            left = Math.min(left, x); top = Math.min(top, y);
            right = Math.max(right, x); bottom = Math.max(bottom, y);
        }
    }
    const bounds = changed ? { x: left, y: top, width: right - left + 1, height: bottom - top + 1 } : null;
    const side = Math.min(envelope.width, envelope.height);
    const square = bounds && Math.abs(bounds.width - side) <= 1 && Math.abs(bounds.height - side) <= 1
        && changed === bounds.width * bounds.height;
    return { envelope, bounds, changed, outside, otherColor, background, redSquare: Boolean(square && !outside && !otherColor) };
}

function placementMatches(details) {
    const [placement] = details.placements;
    return details.totalPlacements === 1 && placement.col === staticImageAnchor.col && placement.viewportRow === staticImageAnchor.row
        && placement.widthCells === cells.cols && placement.heightCells === cells.rows
        && placement.bitmapWidthPx === 1 && placement.bitmapHeightPx === 1 && placement.preserveAspectRatio
        && placement.clipTopRows === 0 && placement.clipBottomRows === 0 && placement.clipRightColumns === 0;
}

class GraphicsReport extends StaticImageReport {
    observe(entry, index, [pixel], replyBytes, { action, expected }) {
        acceptReply(replyBytes, entry, this.states);
        const placing = entry.stageId === 'place';
        const measurement = redPixels(this.boundPixel.image, pixel.image, pixel.details.grid);
        checkImages(measurement.background);
        const sameAsBlank = measurement.changed === 0 && measurement.outside === 0 && pixel.imageDetails.totalPlacements === 0;
        const passed = placing ? measurement.redSquare && placementMatches(pixel.imageDetails) : sameAsBlank;
        const verdict = expected.skipReason ? 'inconclusive' : passed ? 'passed' : 'failed';
        this.observations.push({
            caseId: entry.caseId, stage: entry.stageId, snapshot: index,
            replyEvidence: { requestHex: action.commandHex, replyHex: replyBytes.toString('hex') },
            pixelEvidence: { verdict, measurement, placement: pixel.imageDetails.placements[0] ?? null },
        });
    }
}

const input = { file: 'image-inputs/pinned-red.rgb', bytes: rgb, sha256: pngHash(rgb) };
export const suite = Object.freeze({
    ...staticImageSuite, plan, prefix, action, acceptReply,
    inputs: () => [input], captureKind: kind => kind.replace(prefix, staticImageSuite.prefix),
    buildReport(frames, actions, seal, runId, output, priorChallenges) {
        checkImages(/^[a-f\d]{32}$/.test(runId) && priorChallenges.length === 0
            && frames.length === plan.length + 1 && actions.length === plan.length);
        const report = new GraphicsReport(frames, actions, runId, output, priorChallenges, suite);
        frames.forEach((frame, index) => report.processFrame(frame, index));
        report.checkSeal(seal);
        return { suite: scope.id, runId, observations: report.observations };
    },
});

const files = { frames: 'graphics-frames.jsonl', actions: 'graphics-actions.jsonl', seal: 'graphics-seal.json',
    report: 'graphics-report.json', receipt: 'graphics-receipt.json', context: 'context.json', proof: 'ownership-proof.json',
    source: 'source-snapshot.json', settings: 'initial-settings.json' };
const write = (output, name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { mode: 0o600, flag: 'wx' });

export class Collection {
    constructor(run) {
        this.output = run.output;
        this.observer = new OwnedStaticImageObserver({ output: run.output, appData: run.appData, ownerPid: run.app.pid,
            profileName: run.profileName, nonce: 'NTILDE_IMG_' + run.runId, suite, deferReport: true });
    }

    async observe(request, budget, ownership) {
        const { observer, output } = this;
        const response = await observer.handle(request, budget, ownership);
        if (request.kind === prefix + 'seal') write(output, files.seal, observer.seal);
        else {
            const admitted = request.kind === prefix + 'admit';
            const fact = (admitted ? observer.actions : observer.frames).at(-1);
            appendFileSync(join(output, admitted ? files.actions : files.frames), JSON.stringify(fact) + '\n', { mode: 0o600 });
        }
        return response;
    }

    get restored() { return this.observer.captureRestored; }
}

export function deriveRun(output) {
    const context = json(output, files.context), proof = json(output, files.proof), settings = json(output, files.settings);
    const worker = proof.proofs?.[0], raw = worker?.privateClipboard;
    const frames = journal(output, files.frames), actions = journal(output, files.actions), seal = json(output, files.seal);
    const ownership = frames[0]?.ownership;
    checkImages(!['failure.json', 'cleanup-failure.json', 'graphics-raw-failure.json'].some(name => existsSync(join(output, name)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy
        && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt)
        && worker?.app.pid === context.ownerPid && same(worker.app, ownership?.app) && same(worker.worker, ownership?.worker)
        && worker.tty === ownership.tty && typeof raw?.originalTerminalState === 'string'
        && raw.originalTerminalState === raw.restoredTerminalState
        && settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false && settings.AgentAccessActEnabled === false);
    const report = suite.buildReport(frames, actions, seal, context.runId, output, []);
    const boundFiles = [files.context, files.proof, files.source, files.settings, files.frames, files.actions, files.seal, input.file,
        ...frames.flatMap(frame => frame.pixels.map(pixel => pixel.file))];
    const hashes = Object.fromEntries(boundFiles.map(name => [name, pngHash(read(output, name))]));
    checkImages(hashes[files.settings] === context.initialSettingsSha256 && hashes[files.source] === context.sourceSnapshotManifestSha256);
    return { context, frames, report, receipt: { schemaVersion: 1, runId: context.runId, suite: scope.id, fixtureRemoved: true, files: hashes } };
}

export function completeReceipt(output, scratch) {
    checkImages(!existsSync(scratch));
    const { report, receipt } = deriveRun(output);
    write(output, files.report, report);
    write(output, files.receipt, receipt);
    return { ...receipt, verdict: report.observations.map(row => [row.caseId, row.stage, row.pixelEvidence.verdict]) };
}
