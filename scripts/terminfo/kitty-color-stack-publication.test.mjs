import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { da1 } from './kitty-color-stack-contracts.mjs';
import { deriveRun, framesFile } from './kitty-color-stack-owned.mjs';
import { readPinnedProbes, replay } from './kitty-color-stack-pinned.mjs';
import { buildPublication, publication, renderReport, withKittyColorStackArchive } from './kitty-color-stack-publication.mjs';
import { readJson } from './unicode-publication-inputs.mjs';

test('archived Kitty foreground fixtures and nested stack rederive both publications', async () => {
    const report = await buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.equal(report.pinned.length, 2);
    for (const { terminal, termlessReplay } of report.pinned) {
        assert.equal(terminal.observation.outcome, 'supported');
        assert.deepEqual(terminal, termlessReplay);
        assert.deepEqual(JSON.parse(terminal.response), {
            before: 'rgb:d3d3/d3d3/d3d3', changed: 'rgb:aaaa/bbbb/cccc', restored: 'rgb:d3d3/d3d3/d3d3',
        });
    }
    const at = stage => report.owned.nested.observations.find(row => row.stage === stage);
    for (const [restored, expected] of [['restored-b', 'b'], ['restored-a', 'a'], ['restored-original', 'original']]) {
        assert.equal(at(restored).queriedPaletteEntries, 256);
        for (const key of ['colors', 'overrides', 'indices', 'fullPaletteSha256']) {
            assert.deepEqual(at(restored)[key], at(expected)[key]);
        }
    }
    assert.equal(at('after-empty-pop').fullPaletteSha256, at('a').fullPaletteSha256);
    assert.equal(new Set(['a', 'b', 'c'].map(stage => at(stage).fullPaletteSha256)).size, 3);
});

test('pinned callbacks reject a valid but unrestored color and refuse changed fixture bytes', async () => {
    const report = await buildPublication();
    const probe = readPinnedProbes()[0];
    const caseRun = structuredClone(report.owned.cases[0]);
    const [, , , changed, , restored] = caseRun.exchanges;
    restored.reply = changed.reply;
    restored.raw = changed.reply + da1;
    const { terminal, termlessReplay } = await replay(probe, caseRun);
    assert.equal(terminal.observation.outcome, 'unsupported');
    assert.deepEqual(terminal, termlessReplay);
    caseRun.exchanges[2].request = '\x1b]10;rgb:00/00/00\x07';
    await assert.rejects(replay(probe, caseRun), /pinned request bytes changed/);
});

const readFrames = path => readFileSync(path, 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));

test('full palette evidence fits the owner request cap and raw capture budget', () => {
    withKittyColorStackArchive((directory, manifest) => {
        const frames = readFrames(join(directory, framesFile));
        const samples = frames.flatMap(frame => frame.samples ?? (frame.sample ? [{ sample: frame.sample }] : []));
        assert.ok(samples.length <= 128);
        assert.ok(samples.every(item => item.sample.byteLength <= 512));
        for (const frame of frames.filter(item => item.samples)) {
            const request = { samples: frame.samples, kind: 'kitty-colors-' + frame.stage, tty: frame.ownership.tty,
                token: 'a'.repeat(64), runId: manifest.measurement.runId, workerPid: frame.ownership.worker.pid };
            assert.ok(Buffer.byteLength(JSON.stringify(request) + '\n') <= 8192, frame.stage);
        }
    });
});

function replaceReply(sample, raw) {
    Object.assign(sample, { hex: Buffer.from(raw).toString('hex'), byteLength: Buffer.byteLength(raw) });
}

test('receipt refuses cross-case fences, stale modes, changed palette or overrides, and altered cells', () => {
    withKittyColorStackArchive(directory => {
        const path = join(directory, framesFile), original = readFrames(path);
        for (const [mutate, message] of [
            [at => {
                const push = at('extensions.osc30001-color-stack-push/initial');
                const pop = at('extensions.osc30101-color-stack-pop/initial');
                pop.samples[0].sample = push.samples[0].sample;
            }, /owned routed-input evidence/],
            [at => {
                const { sample } = at('after-empty-pop').samples[0];
                replaceReply(sample, Buffer.from(sample.hex, 'hex').toString().replace('[?2004;1$y', '[?2004;2$y'));
            }, /unrelated mode replies/],
            [at => {
                const frame = at('pop-b');
                const { colorBinding } = frame.screen;
                // This palette index is absent from the stored cells and both observers report the forged color.
                colorBinding.ansiPaletteArgb[255] = frame.theme.ansiPaletteArgb[255] = 0xff010203;
                frame.sha256 = frame.afterSha256 = snapshotHash(frame.screen);
            }, /expected colors, overrides and full palette/],
            [at => { at('pop-a').theme.selectionForegroundOverride = false; }, /expected colors, overrides and full palette/],
            [at => {
                const frame = at('pop-b');
                frame.screen.lines[1] = 'Ab Bb';
                frame.sha256 = frame.afterSha256 = snapshotHash(frame.screen);
            }, /operation changed stored cells/],
            [at => { at('reset').theme.backgroundOverride = true; }, /App color observations disagreed/],
        ]) {
            const frames = structuredClone(original);
            mutate(stage => frames.find(frame => frame.stage === stage));
            writeFileSync(path, frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
            assert.throws(() => deriveRun(directory), message);
        }
    });
});

test('receipt refuses incomplete kernel cleanup', () => {
    withKittyColorStackArchive(directory => {
        const proof = readJson(directory, 'ownership-proof.json');
        proof.proofs[0].privateClipboard.restoredTerminalState += '-changed';
        writeFileSync(join(directory, 'ownership-proof.json'), JSON.stringify(proof));
        assert.throws(() => deriveRun(directory), /exact PTY cleanup/);
    });
});
