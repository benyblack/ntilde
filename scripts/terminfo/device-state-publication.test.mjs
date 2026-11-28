import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { snapshotHash } from './agent-observer.mjs';
import { replayDeviceProbe } from './device-identity-publication.mjs';
import { da1, planFor, requests, verifySamples } from './device-state-contracts.mjs';
import { deriveRun } from './device-state-owned.mjs';
import { buildPublication, publication, readPinnedProbes, renderReport, withDeviceStateArchive } from './device-state-publication.mjs';
import { leaseReleasedAt, readJson } from './unicode-publication-inputs.mjs';

test('archived query and state evidence rederives the publication', async () => {
    const report = await buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.equal(report.pinned.length, 6);
    for (const row of report.pinned) {
        assert.equal(row.terminal.observation.outcome, 'supported');
        assert.equal(row.termlessReplay.observation.outcome, 'supported');
    }
    assert.deepEqual(report.owned.exchanges.map(row => row.reply), [
        '\x1bP1$r0m\x1b\\', '\x1bP1$r0;1;4:3;31;48;5;200;58;2;9;8;7m\x1b\\', '\x1bP1$r0m\x1b\\', '\x1bP0$r\x1b\\',
        '\x1b[?997;1n', '\x1b[?997;2n', '\x1b[?997;1n',
    ]);
    assert.ok(report.owned.exchanges.every(row => row.outcome === 'passed'));
    assert.ok(report.owned.restored);
});

const framesFile = 'device-state-frames.jsonl';
const readFrames = directory => readFileSync(join(directory, framesFile), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));

function replaceReply(sample, value) {
    const bytes = Buffer.from(value);
    Object.assign(sample, { hex: bytes.toString('hex'), byteLength: bytes.length });
}

test('unchanged pinned callbacks accept stale but well-formed state values that the exact oracle fails', async () => {
    const [sgr, scheme] = readPinnedProbes();
    const { frames, plan } = withDeviceStateArchive(directory => ({
        frames: readFrames(directory),
        plan: planFor(readJson(directory, 'context.json').runId),
    }));
    for (const [index, stage, probe, staleReply] of [
        [0, 'styled', sgr, '\x1bP1$r0m\x1b\\'],
        [1, 'light', scheme, '\x1b[?997;1n'],
    ]) {
        const { samples, screen } = frames.find(row => row.stage === stage);
        replaceReply(samples[0].sample, staleReply + da1);
        const step = plan.find(row => row.stage === stage);
        const exchange = verifySamples(step, samples, new Set(), screen);
        assert.equal(exchange.outcome, 'failed');
        const replay = await replayDeviceProbe(probe, exchange, requests[index]);
        assert.equal(replay.terminal.observation.outcome, 'supported');
        assert.equal(replay.termlessReplay.observation.outcome, 'supported');

        const incomplete = staleReply.slice(0, -1);
        const malformed = await replayDeviceProbe(probe, { ...exchange, raw: incomplete + da1, reply: incomplete }, requests[index]);
        assert.equal(malformed.terminal.observation.outcome, 'inconclusive');
        assert.equal(malformed.termlessReplay.observation.outcome, 'inconclusive');
    }
});

function verifyRefusals(directory, mutations) {
    const framesPath = join(directory, framesFile), original = readFrames(directory);
    for (const [change, message] of mutations) {
        const frames = structuredClone(original);
        change(stage => frames.find(row => row.stage === stage));
        writeFileSync(framesPath, frames.map(row => JSON.stringify(row)).join('\n') + '\n');
        assert.throws(() => deriveRun(directory), message);
    }
}

test('receipt refuses unsolicited traffic, enabled mode, stale fences and inconsistent color controls', () => {
    withDeviceStateArchive(directory => verifyRefusals(directory, [
        [at => replaceReply(at('light-set').sample, '\x1b[?997;2n'), /unexpected reply to a fixture write/],
        [at => replaceReply(at('dark-after').samples[0].sample, '\x1b[?2031;1$y'), /mode 2031 was enabled/],
        [at => replaceReply(at('light-before').samples[1].sample, '\x1b]11;rgb:1010/1010/1010\x1b\\'), /OSC 11 disagreed/],
        [at => { at('light').samples[0].sample = at('dark').samples[0].sample; }, /owned routed-input evidence/],
        [at => replaceReply(at('light').samples[0].sample, '\x1b[?997;2n'), /missing exact DA1 sentinel/],
    ]));
});

test('receipt refuses missing witness and query-side state mutation even with internally consistent screen hashes', () => {
    withDeviceStateArchive(directory => verifyRefusals(directory, [
        [at => {
            const frame = at('styled-after');
            frame.screen.cellDetails.find(run => run.underlineStyle === 3).underlineArgb = 0xff090809;
            frame.sha256 = frame.afterSha256 = snapshotHash(frame.screen);
        }, /stored witness attributes/],
        [at => {
            const frame = at('light');
            frame.screen.cursorVisible = !frame.screen.cursorVisible;
            frame.sha256 = frame.afterSha256 = snapshotHash(frame.screen);
        }, /query changed cells/],
        [at => {
            const frame = at('restored-set');
            frame.screen.colorBinding.backgroundOverride = true;
            frame.sha256 = frame.afterSha256 = snapshotHash(frame.screen);
        }, /did not change only the effective background/],
    ]));
});

test('publication refuses incomplete PTY restoration and missing lease release', () => {
    withDeviceStateArchive(directory => {
        const proof = readJson(directory, 'ownership-proof.json');
        proof.proofs[0].privateClipboard.restoredTerminalState += '-changed';
        writeFileSync(join(directory, 'ownership-proof.json'), JSON.stringify(proof));
        assert.throws(() => deriveRun(directory), /exact PTY cleanup/);
        const path = join(directory, 'desktop-lease.jsonl');
        const lines = readFileSync(path, 'utf8').trimEnd().split('\n');
        writeFileSync(path, lines.slice(0, -1).join('\n'));
        assert.throws(() => leaseReleasedAt(directory, JSON.parse(lines[0]).lease, assert.ok), /desktop lease release/);
    });
});
