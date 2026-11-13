import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { ColorResetObserver, retainedColorResetCapture } from './color-reset-capture.mjs';
import { buildColorResetGradingReport } from './color-reset-grading.mjs';
import { colorResetReadPlan } from './color-reset-contracts.mjs';
import { syntheticColorResetEvidence, writeColorResetPngs, cloneColorResetEvidence } from './fixtures/color-reset-evidence.mjs';

const runId = 'a'.repeat(32), nonce = 'NTILDE_CR_' + runId;

/** An observer whose stored-state reads and renders are served from a synthetic run, frame by frame. */
function world(evidence = syntheticColorResetEvidence(runId), options = {}) {
    const output = mkdtempSync(join(tmpdir(), 'nt-color-reset-'));
    const state = { frame: evidence.frames[0], shown: null, renderOf: frame => frame };
    const observer = new ColorResetObserver({ output, appData: '/never/discover', ownerPid: 42, profileName: 'fixture', nonce, expectedPaneId: evidence.frames[0].paneId,
        rpc: async () => { throw new Error('unused'); }, endpoint: () => 'private-fixture',
        captureRpc: async () => {
            const frame = state.renderOf(state.frame), bytes = evidence.pngs.get(frame.pixel.file);
            return { width: frame.pixel.width, height: frame.pixel.height, rows: frame.pixel.rows, cols: frame.pixel.cols, byteCount: bytes.length, downscaled: false, inlineOmitted: false,
                mode: 'render', scale: 1, pngBase64: bytes.toString('base64'), presentationTimeMs: 0, renderDetails: frame.pixel.renderDetails, ...options.capture };
        } });
    for (const part of [observer.before, observer.after]) {
        part.session = async () => ({ paneId: evidence.frames[0].paneId, kind: 'local', windowless: false, profileName: 'fixture', rows: evidence.frames[0].screen.rows, cols: evidence.frames[0].screen.cols });
        part.capture = async () => {
            part.snapshots++;
            return structuredClone(part === observer.after && options.after ? options.after(state.frame.screen) : state.frame.screen);
        };
    }
    const call = async (frame, extra = {}) => {
        state.frame = frame;
        const operation = frame.kind.slice('crst-'.length);
        return observer.handle({ kind: frame.kind, barrier: frame.barrier, ...(operation === 'read' ? { caseId: frame.caseId, experimentId: frame.experimentId, stageId: frame.stageId, queries: frame.queries } : {}), ...extra }, () => 1000);
    };
    return { evidence, observer, output, state, call, done: () => rmSync(output, { recursive: true, force: true }) };
}

test('the observer reproduces a complete synthetic run frame for frame, and the report builder accepts what it retained', async () => {
    const w = world();
    try {
        for (const frame of w.evidence.frames) await w.call(frame);
        assert.equal(w.observer.restored, true);
        assert.equal(w.observer.frames.length, 47);
        for (const [index, frame] of w.observer.frames.entries()) assert.deepEqual(frame, w.evidence.frames[index], `frame ${index}`);
        for (const frame of w.observer.frames) assert.ok(existsSync(join(w.output, frame.pixel.file)));
        const run = cloneColorResetEvidence(w.evidence);
        const report = buildColorResetGradingReport(w.observer.frames, run.run, runId, run.adapter, { output: w.output });
        assert.deepEqual(report.cases.map(item => item.conclusion.outcome), ['supported', 'supported', 'supported', 'supported']);
        assert.equal(retainedColorResetCapture(w.output, w.observer.frames[7]).image.width, w.observer.frames[7].pixel.width);
        await assert.rejects(w.call(w.evidence.frames[3]), /busy or complete/);
    } finally { w.done(); }
});

async function until(w, count) { for (const frame of w.evidence.frames.slice(0, count)) await w.call(frame); }

test('the first frame must be a pristine private theme with a pristine screen', async () => {
    const dirty = syntheticColorResetEvidence(runId);
    dirty.frames[0].pixel.renderDetails.theme.cursorOverride = true;
    const w = world(dirty);
    try { await assert.rejects(w.call(w.evidence.frames[0]), /pristine private baseline/); } finally { w.done(); }
});

test('reads must follow the plan: case, experiment and stage, and exactly the planned queries', async () => {
    const w = world();
    try {
        await until(w, 2);
        const first = w.evidence.frames[2];
        await assert.rejects(w.call(first, { stageId: 'override' }), /Unexpected color-reset read/);
        await assert.rejects(w.call(first, { experimentId: 'isolation' }), /Unexpected color-reset read/);
        await assert.rejects(w.call(first, { caseId: colorResetReadPlan[15].caseId }), /Unexpected color-reset read/);
        await assert.rejects(w.call(first, { queries: [] }), /Invalid owned color capture or query binding/);
        await assert.rejects(w.call(first, { queries: [{ ...first.queries[0], spec: { code: 10 } }] }), /Invalid owned color capture or query binding/);
        await assert.rejects(w.call(first, { queries: [{ ...first.queries[0], barrier: 'x' }] }), /Invalid owned color capture or query binding/);
        await assert.rejects(w.call(first, { queries: [{ ...first.queries[0], reply: { raw: '\x1b]4;0;rgb:0000/0000/0000\x1b\\', channels16: [1, 2, 3] } }] }), /Invalid owned color capture or query binding/);
        await w.call(first);
        assert.equal(w.observer.frames.length, 3);
        await assert.rejects(w.call(w.evidence.frames.at(-1)), /before the plan completed/);
    } finally { w.done(); }
});

test('only reads carry replies', async () => {
    const w = world();
    try {
        await assert.rejects(w.call(w.evidence.frames[0], { queries: w.evidence.frames[2].queries }), /Only reads carry color-reset replies/);
    } finally { w.done(); }
});

test('a render that does not describe the observed state, or a state that changes around it, is refused', async () => {
    const w = world();
    try {
        await until(w, 2);
        // The render shows the cursor at home while the observed state has it at row 9, column 9.
        w.state.renderOf = () => w.evidence.frames[0];
        await assert.rejects(w.call(w.evidence.frames[2]), /Invalid or changed controlled pixel capture/);
        w.state.renderOf = frame => frame;
        await w.call(w.evidence.frames[2]);
    } finally { w.done(); }
    const changing = world(undefined, { after: screen => ({ ...screen, lines: screen.lines.map((line, row) => (row === 0 ? 'x' : line)) }) });
    try {
        await assert.rejects(until(changing, 1), /changed the observed screen/);
    } finally { changing.done(); }
});

test('the finish frame must restore the original theme and render exactly', async () => {
    const evidence = syntheticColorResetEvidence(runId);
    const w = world(evidence);
    try {
        await until(w, evidence.frames.length - 1);
        const finish = structuredClone(evidence.frames.at(-1));
        finish.pixel.renderDetails.theme = structuredClone(evidence.frames[10].pixel.renderDetails.theme);
        evidence.pngs.set(finish.pixel.file, evidence.pngs.get(evidence.frames[10].pixel.file));
        await assert.rejects(w.call(finish), /was not restored|./);
        assert.equal(w.observer.restored, false);
    } finally { w.done(); }
});

test('retained renders are revalidated: changed bytes, a symlink and a foreign path are refused', async () => {
    const w = world();
    try {
        for (const frame of w.evidence.frames) await w.call(frame);
        const frame = w.observer.frames[5], path = join(w.output, frame.pixel.file), bytes = readFileSync(path);
        writeFileSync(path, Buffer.concat([bytes, Buffer.from([1])]));
        assert.throws(() => retainedColorResetCapture(w.output, frame), /bytes changed/);
        writeFileSync(path, bytes);
        assert.doesNotThrow(() => retainedColorResetCapture(w.output, frame));
        assert.throws(() => retainedColorResetCapture(w.output, { ...frame, pixel: { ...frame.pixel, file: '../frame-005.png' } }), /Invalid retained color-reset capture/);
        assert.throws(() => retainedColorResetCapture(w.output, { ...frame, pixel: { ...frame.pixel, sha256: 'x' } }), /Invalid retained color-reset capture/);
        assert.throws(() => retainedColorResetCapture(w.output, { ...frame, pixel: undefined }), /Invalid retained color-reset capture/);
    } finally { w.done(); }
});
