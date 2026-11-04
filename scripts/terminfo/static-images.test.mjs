import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { buildStaticImageReport, imagePlan, imageReply } from './static-images.mjs';
import { staticImageCases } from './static-images-cases.mjs';
import { syntheticImageJournal } from './fixtures/static-images-journal.mjs';
import { snapshotHash } from './agent-observer.mjs';
const fixture = action => { const output = mkdtempSync(join(tmpdir(), 'nt-image-model-')); try { action(output, syntheticImageJournal(output)); } finally { rmSync(output, { recursive: true, force: true }); } };
const derive = (output, f, prior = []) => buildStaticImageReport(f.frames, f.actions, f.seal, f.runId, output, prior);
test('synthetic31 paired frames and30 admissions rederive23 separate reply/pixel observations', () => fixture((output, f) => {
    const report = derive(output, f);
    assert.equal(imagePlan.length, 30); assert.equal(report.snapshots.length, 31); assert.equal(report.observations.length, 23);
    assert.equal(report.inputs.length, 7); assert.ok(report.observations.every(item => item.result === 'passed'));
    assert.equal(report.caseResults.length, 5); assert.ok(report.caseResults.every(item => item.result === 'passed'));
    assert.match(report.scope, /No animation, placeholders/);
}));
for (const [name, mutate] of [
    ['missing paired capture', f => f.frames[5].pixels.pop()],
    ['state mutation', f => { f.frames[5].screen.cursorCol++; f.frames[5].sha256 = snapshotHash(f.frames[5].screen); }],
    ['image request mutation', f => { f.actions[2].commandHex += '00'; }],
    ['changed worker', f => { f.actions[4].ownership.worker.pid++; }],
    ['changed geometry', f => { f.frames[4].ownership.terminalOutput.description = '11 rows; 60 columns; -opost'; }],
    ['missing restoration', f => f.frames.splice(5, 1)],
    ['unsealed raw reader', f => { f.seal.disposed = false; }],
    ['late bytes', f => { f.seal.lastAttempt.hex += 'ff'; }],
    ['reused raw fence', f => { f.frames[4].capture = structuredClone(f.frames[3].capture); }],
    ['wrong fence spelling', f => { f.frames[4].capture.barrier += 'x'; }],
    ['changed paired unpainted derived color', f => { f.frames[4].pixels[0].renderDetails.theme.cursorTextArgb = 0xff112233; }],
    ['runtime background pin absent', f => { for (const pixel of f.frames[4].pixels) pixel.renderDetails.theme.backgroundOverride = false; }],
    ['wrong independent background query', f => { f.frames[4].capture.hex = f.frames[4].capture.hex.replace(Buffer.from('rgb:1010/1010/1010').toString('hex'), Buffer.from('rgb:0000/0000/0000').toString('hex')); }],
    ['reverse-video mode set', f => { f.frames[4].capture.hex = f.frames[4].capture.hex.replace(Buffer.from('\x1b[?5;2$y').toString('hex'), Buffer.from('\x1b[?5;1$y').toString('hex')); }],
    ['unrestored original capture context', f => { for (const pixel of f.frames.at(-1).pixels) pixel.renderDetails.theme.cursorTextArgb = 0xff112233; }],
]) test(`report rejects ${name}`, () => fixture((output, f) => { mutate(f); assert.throws(() => derive(output, f)); }));
test('input and image fences cannot reuse a challenge pair across suite boundaries', () => fixture((output, f) => {
    assert.throws(() => derive(output, f, [f.frames[1].capture.nonces.join(',')]));
}));
test('retained PNG/source-image changes fail; no archived byte is trusted from a reported digest alone', () => fixture((output, f) => {
    const path = join(output, f.frames[3].pixels[0].file), bytes = readFileSync(path); bytes[bytes.length - 1] ^= 1; writeFileSync(path, bytes);
    assert.throws(() => derive(output, f));
}));
test('a successful ACK with absent pixels completes as semantic failure, not support', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-image-no-pixels-'));
    try {
        const f = syntheticImageJournal(output, { pixelOptions: entry => ({ blank: entry.kind === 'read' }) });
        const report = derive(output, f), place = report.observations.find(item => item.featureId.endsWith('.place'));
        assert.equal(place.reply.passed, true); assert.equal(place.result, 'failed');
        assert.equal(report.observations.length, 23);
    } finally { rmSync(output, { recursive: true, force: true }); }
});
for (const [caseId, absentStage, dependentStage] of [['sixel.native-raster', 'show', 'clear'], ['kitty.numbered-lifecycle', 'place', 'soft-delete']])
    test(`disappearance qualification requires the actual prior visible witness: ${caseId}`, () => {
        const output = mkdtempSync(join(tmpdir(), 'nt-image-causal-'));
        try {
            const f = syntheticImageJournal(output, { pixelOptions: entry => ({ blank: entry.caseId === caseId && entry.stageId === absentStage }) });
            const report = derive(output, f);
            assert.equal(report.observations.find(value => value.featureId === caseId + '.' + absentStage).result, 'failed');
            const dependent = report.observations.find(value => value.featureId === caseId + '.' + dependentStage);
            assert.equal(dependent.result, 'inconclusive'); assert.equal(dependent.skipReason, null);
            assert.match(dependent.inconclusiveReason, /requires passed prior witness/);
            assert.ok(dependent.causality.some(value => value.featureId === caseId + '.' + absentStage && value.result === 'failed'));
            assert.equal(report.caseResults.find(value => value.caseId === caseId).result, 'failed');
            assert.equal(f.actions[dependent.actionIndex].commandHex.includes(Buffer.from('a=d').toString('hex')), caseId.startsWith('kitty.'));
        } finally { rmSync(output, { recursive: true, force: true }); }
    });
test('image bleed is a semantic failure after the independently blank fixture is established', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-image-bleed-'));
    try {
        const f = syntheticImageJournal(output, { pixelOptions: entry => ({ bleed: entry.stageId === 'show' }) });
        const report = derive(output, f); assert.equal(report.observations.find(item => item.featureId === 'sixel.native-raster.show').result, 'failed');
    } finally { rmSync(output, { recursive: true, force: true }); }
});
test('correlated allocation error is negative; old-id stages are explicitly inconclusive without guessed IDs', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-image-negative-'));
    try {
        const f = syntheticImageJournal(output, { mutateReply: (entry, raw) => {
            if (!entry.caseId?.startsWith('kitty.') || !raw) return raw;
            return raw.replace(/i=\d+,/, '').replace(';OK', ';ENOTSUP:Graphics disabled');
        }, pixelOptions: () => ({ blank: true }) });
        const report = derive(output, f);
        assert.equal(report.observations.find(item => item.featureId === 'kitty.number-reuse.store-first').result, 'failed');
        for (const stage of ['place-newest', 'delete-newest', 'place-old-id', 'delete-old-id']) {
            const item = report.observations.find(value => value.featureId === 'kitty.number-reuse.' + stage);
            assert.equal(item.result, 'inconclusive'); assert.match(item.skipReason, /both distinct successful allocations/);
            assert.equal(f.actions[item.actionIndex].firstId, null);
        }
    } finally { rmSync(output, { recursive: true, force: true }); }
});
test('a refused second allocation leaves all reuse-dependent stages inconclusive instead of qualifying blank deletion', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-image-second-id-'));
    try {
        const f = syntheticImageJournal(output, { mutateReply: (entry, raw) => entry.stageId === 'store-second'
            ? raw.replace(/i=\d+,/, '').replace(';OK', ';ENOTSUP:Second allocation refused') : raw });
        const report = derive(output, f);
        assert.equal(report.observations.find(value => value.featureId === 'kitty.number-reuse.store-second').result, 'failed');
        for (const stage of ['place-newest', 'delete-newest', 'place-old-id', 'delete-old-id']) {
            const item = report.observations.find(value => value.featureId === 'kitty.number-reuse.' + stage);
            assert.equal(item.result, 'inconclusive'); assert.match(item.skipReason, /both distinct successful allocations/);
        }
    } finally { rmSync(output, { recursive: true, force: true }); }
});
test('unframed, uncorrelated, duplicate or arbitrary replies cannot become semantic observations', () => {
    const item = staticImageCases[3], stage = item.stages[1], state = { number: 17, firstId: null, ids: [] };
    for (const raw of ['OK', '\x1b_GI=18;ENOTSUP:disabled\x1b\\', '\x1b_GI=17,I=17;ENOTSUP\x1b\\', '\x1b_GI=17;x\x1b\\']) assert.throws(() => imageReply(raw, item, stage, state));
    assert.deepEqual(imageReply('', item, stage, state), { passed: false, id: null, status: null });
});
test('actual unresolved numbered allocation/deletion errors are negative evidence, not malformed bytes', () => {
    const item = staticImageCases[3], state = { number: 137, firstId: null, ids: [] };
    // ExecuteKittyCommand + ReplyKittyCommand: identity.Id remains zero on refusal.
    const raw = '\x1b_Gi=0,I=137;ENOTSUP:Graphics disabled by host\x1b\\';
    for (const id of ['store', 'hard-delete']) {
        const result = imageReply(raw, item, item.stages.find(value => value.id === id), state);
        assert.equal(result.passed, false); assert.equal(result.id, null); assert.match(result.status, /^ENOTSUP:/);
    }
});
test('zero errors still require the exact number/placement and cannot bypass an explicit positive image ID', () => {
    const item = staticImageCases[4], state = { number: 137, firstId: 99, ids: [99, 100] };
    for (const raw of ['\x1b_Gi=0,p=1;ENOENT:Image not found\x1b\\', '\x1b_Gi=0,I=137,p=1;ENOENT:Image not found\x1b\\'])
        assert.throws(() => imageReply(raw, item, item.stages.find(value => value.id === 'place-old-id'), state));
    const numbered = staticImageCases[3], stage = numbered.stages.find(value => value.id === 'missing-place');
    for (const raw of ['\x1b_Gi=0,I=138,p=1;ENOENT:Image not found\x1b\\', '\x1b_Gi=0,I=137,p=2;ENOENT:Image not found\x1b\\'])
        assert.throws(() => imageReply(raw, numbered, stage, state));
    assert.equal(imageReply('\x1b_Gi=0,I=137,p=1;ENOENT:Image not found\x1b\\', numbered, stage, state).passed, true);
});
test('a later successful allocation cannot stand in for a refused first allocation in old-ID stages', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-image-first-id-'));
    try {
        const f = syntheticImageJournal(output, { mutateReply: (entry, raw) => entry.stageId === 'store-first'
            ? raw.replace(/i=\d+,/, '').replace(';OK', ';ENOTSUP:First allocation refused') : raw });
        const report = derive(output, f);
        assert.equal(report.observations.find(value => value.featureId === 'kitty.number-reuse.store-first').result, 'failed');
        assert.equal(report.observations.find(value => value.featureId === 'kitty.number-reuse.store-second').reply.passed, true);
        for (const stage of ['place-old-id', 'delete-old-id']) {
            const item = report.observations.find(value => value.featureId === 'kitty.number-reuse.' + stage);
            assert.equal(item.result, 'inconclusive'); assert.equal(f.actions[item.actionIndex].firstId, null);
        }
    } finally { rmSync(output, { recursive: true, force: true }); }
});
