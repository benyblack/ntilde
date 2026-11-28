import { existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { redPixels } from './kitty-graphics-owned.mjs';
import { FocusedImageCollection } from './static-images-capture.mjs';
import { deriveFocusedImageRun } from './static-images-receipt.mjs';
import { checkImages, imageModeQueries, imageModeReplyHex, imageRestore, imageSetup, retainedImagePixel,
    StaticImageReport, staticImageSuite } from './static-images.mjs';

export const scope = Object.freeze({ id: 'ntilde-owned-kitty-aggregate-v1', persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'kitty-aggregate-';
export const workerFile = 'kitty-aggregate-worker.mjs';
export const imageOwnership = true;
export const caseIds = Object.freeze(['extensions.kitty-keyboard', 'extensions.kitty-graphics']);
const [keyboard, graphics] = caseIds;
const apc = body => '\x1b_G' + body + '\x1b\\';
const flags = '\x1b[?u', da = '\x1b[c';
const reply = status => apc('i=31;' + status);
const place = apc('a=p,i=31,c=2,r=1,C=1');
const missing = reply('ENOENT:Image not found');
const hex = text => Buffer.from(text).toString('hex');
const stage = (caseId, stageId, request, sentinel = '') => ({ kind: 'read', caseId, stageId, request, sentinel });

export const plan = Object.freeze([
    { kind: 'bind' },
    stage(keyboard, 'detect', flags, da),
    stage(keyboard, 'push', '\x1b[>1u'),
    stage(keyboard, 'active', flags, da),
    stage(keyboard, 'pop', '\x1b[<u'),
    stage(keyboard, 'restored', flags, da),
    stage(graphics, 'missing-before', place),
    stage(graphics, 'query', apc('i=31,s=1,v=1,a=q,t=d,f=24;AAAA'), da),
    stage(graphics, 'missing-after', place),
    stage(graphics, 'upload-control', apc('a=t,i=31,s=1,v=1,t=d,f=24;/wAA')),
    stage(graphics, 'place-control', place),
    stage(graphics, 'delete-control', apc('a=d,d=I,i=31,q=2')),
    stage(graphics, 'missing-after-delete', place),
    { kind: 'finish' },
].map(Object.freeze));

const hasExperimentalFlags = entry => entry.caseId === keyboard;
export function modeQueries(entry) {
    return hasExperimentalFlags(entry) ? imageModeQueries.slice(0, -flags.length) : imageModeQueries;
}

function modeReplyHex(entry) {
    return hasExperimentalFlags(entry) ? imageModeReplyHex.slice(0, -hex('\x1b[?0u').length) : imageModeReplyHex;
}

function action(entry, _state, runId, cursorVisible) {
    let command;
    if (entry.kind === 'bind') command = imageSetup(runId);
    else if (entry.kind === 'finish') command = imageRestore(cursorVisible);
    else command = entry.request + entry.sentinel;
    return { command, skipReason: null };
}

/** Separate the pinned primary DA from the response. The paired DECRPM fence is independently validated by the carrier. */
function splitReply(entry, bytes) {
    const raw = bytes.toString('ascii');
    if (!entry.sentinel) return { raw, sentinel: '' };
    const da1 = /\x1b\[\?\d+(?:;\d+)*c$/.exec(raw);
    checkImages(da1 && !/\x1b\[\?[0-9;]+c/.test(raw.slice(0, da1.index)));
    return { raw: raw.slice(0, da1.index), sentinel: da1[0] };
}

function readFlags(row) {
    const flagsReply = /^\x1b\[\?(0|[1-9]\d*)u$/.exec(Buffer.from(row.replyHex, 'hex').toString('ascii'));
    const value = Number(flagsReply?.[1]);
    return Number.isSafeInteger(value) && value >= 0 && value <= 31 ? value : null;
}

export function gradeObservations(rows) {
    const byStage = Object.fromEntries(rows.map(row => [row.stage, row]));
    const result = name => Buffer.from(byStage[name].replyHex, 'hex').toString('ascii');
    const before = readFlags(byStage.detect), active = readFlags(byStage.active), after = readFlags(byStage.restored);
    checkImages(before !== null && (before & 1) === 0 && after === before
        && result('push') === '' && result('pop') === ''
        && byStage.detect.sentinelReplyHex === byStage.active.sentinelReplyHex
        && byStage.detect.sentinelReplyHex === byStage.restored.sentinelReplyHex);
    checkImages(result('missing-before') === missing && byStage['missing-before'].unchanged
        && result('upload-control') === reply('OK') && byStage['upload-control'].unchanged
        && result('place-control') === reply('OK') && byStage['place-control'].painted
        && result('delete-control') === '' && byStage['delete-control'].unchanged
        && result('missing-after-delete') === missing && byStage['missing-after-delete'].unchanged);
    return {
        keyboard: { before, active, after, bitOneActivated: active !== null && (active & 1) === 1 },
        graphics: {
            accepted: result('query') === reply('OK'),
            imageAbsent: result('missing-after') === missing,
            placementCount: byStage.query.placementCount,
            unchanged: byStage.query.unchanged && byStage['missing-after'].unchanged,
        },
    };
}

class AggregateReport extends StaticImageReport {
    observe(entry, index, [pixel], replyBytes) {
        const { raw, sentinel } = splitReply(entry, replyBytes);
        const placementCount = pixel.imageDetails.totalPlacements;
        const unchanged = placementCount === 0 && pixel.bytes.equals(this.boundPixel.bytes);
        const painted = entry.stageId === 'place-control' && placementCount === 1
            && redPixels(this.boundPixel.image, pixel.image, pixel.details.grid).redSquare;
        this.observations.push({
            caseId: entry.caseId, stage: entry.stageId, snapshot: index,
            requestHex: hex(entry.request), replyHex: hex(raw),
            sentinelRequestHex: hex(entry.sentinel), sentinelReplyHex: hex(sentinel),
            placementCount, unchanged, painted,
        });
    }
}

const captureKind = kind => kind.replace(prefix, staticImageSuite.prefix);
const suite = Object.freeze({
    ...staticImageSuite, plan, prefix, action, captureKind, modeReplyHex,
    newState: () => ({}), registerState: () => ({}), acceptReply: () => {},
    inputs: () => [],
    retainPixel: (output, pixel, screen, kind) => retainedImagePixel(output, pixel, screen, captureKind(kind)),
    buildReport(frames, actions, seal, runId, output) {
        checkImages(/^[a-f\d]{32}$/.test(runId) && frames.length === plan.length + 1 && actions.length === plan.length);
        const report = new AggregateReport(frames, actions, runId, output, [], suite);
        frames.forEach((frame, index) => report.processFrame(frame, index));
        report.checkSeal(seal);
        return { suite: scope.id, runId, observations: report.observations, effects: gradeObservations(report.observations) };
    },
});

const files = {
    frames: 'aggregate-frames.jsonl', actions: 'aggregate-actions.jsonl', seal: 'aggregate-seal.json',
    report: 'aggregate-report.json', receipt: 'aggregate-receipt.json', context: 'context.json', proof: 'ownership-proof.json',
    source: 'source-snapshot.json', settings: 'initial-settings.json',
};
const write = (output, name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { mode: 0o600, flag: 'wx' });

export class Collection extends FocusedImageCollection {
    constructor(run) { super(run, suite, files); }
}

export function deriveRun(output) {
    return deriveFocusedImageRun(output, { files, rawFailure: 'aggregate-raw-failure.json', scope, suite });
}

export function completeReceipt(output, scratch) {
    checkImages(!existsSync(scratch));
    const { report, receipt } = deriveRun(output);
    write(output, files.report, report);
    write(output, files.receipt, receipt);
    return { ...receipt, effects: report.effects };
}
