import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { readJson } from './unicode-publication-inputs.mjs';
import { da1, exactReplies, planFor, verifySamples } from './device-identity-contracts.mjs';
import { deriveRun } from './device-identity-owned.mjs';
import { buildPublication, publication, readPinnedProbes, renderReport, replay, withIdentityArchive } from './device-identity-publication.mjs';

test('the pinned callbacks, exact replies, VT metadata and cleanup rederive from the owned archive', async () => {
    const report = await buildPublication();
    assert.deepEqual(report, readJson(publication, 'report.json'));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.deepEqual(report.pinned.map(row => [row.terminal.observation.outcome, row.termlessReplay.observation.outcome]),
        [['supported', 'supported'], ['supported', 'supported']]);
    assert.deepEqual(report.owned.exchanges.map(row => row.reply), [
        da1, '\x1b[>1;10;0c', '\x1bP!|00000000\x1b\\', '\x1bP!|00000000\x1b\\', '',
        '\x1bP>|Ntilde 0.14.0\x1b\\', '\x1bP>|Ntilde 0.14.0\x1b\\', '',
    ]);
    assert.ok(report.owned.exchanges.every(row => row.outcome === 'passed'));
    assert.ok(report.owned.restored);
});

const framesFile = 'identity-frames.jsonl';
const readFrames = directory => readFileSync(join(directory, framesFile), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
const replaceReply = (sample, raw) => {
    const bytes = Buffer.from(raw);
    Object.assign(sample, { hex: bytes.toString('hex'), byteLength: bytes.length });
};

test('pinned framing accepts different complete payloads and refuses incomplete frames', async () => {
    const probes = readPinnedProbes();
    const rows = withIdentityArchive(dir => {
        const frames = readFrames(dir), run = readJson(dir, 'identity-report.json'), plan = planFor(run.runId);
        return probes.map((probe, index) => {
            const step = plan.find(item => item.id === probe.id);
            const { samples } = frames.find(row => row.stage === step.stage);
            return { step, samples, reply: ['\x1bP!|12345678\x1b\\', '\x1bP>|Other 0.14.0\x1b\\'][index], version: run.metadata.version };
        });
    });
    for (const [index, { step, samples, reply, version }] of rows.entries()) {
        replaceReply(samples[0].sample, reply + da1);
        const exchange = verifySamples(step, samples, new Set(), exactReplies(version));
        assert.equal(exchange.outcome, 'failed');
        const complete = await replay(probes[index], exchange);
        assert.equal(complete.terminal.observation.outcome, 'supported');
        assert.equal(complete.termlessReplay.observation.outcome, 'supported');

        const incompleteReply = reply.slice(0, -2);
        const incomplete = await replay(probes[index], { ...exchange, reply: incompleteReply, raw: incompleteReply + da1 });
        assert.equal(incomplete.terminal.observation.reason, 'invalid-reply');
        assert.equal(incomplete.termlessReplay.observation.reason, 'invalid-reply');
    }
});

test('receipt rejects a stale fence, a changed mode and a cursor observer that missed the positive control', () => {
    withIdentityArchive(dir => {
        const original = readFrames(dir);
        const rewrite = frames => writeFileSync(join(dir, framesFile), frames.map(row => JSON.stringify(row)).join('\n') + '\n');
        const mutate = action => {
            const frames = structuredClone(original);
            action(stage => frames.find(row => row.stage === stage));
            rewrite(frames);
        };
        mutate(at => { at('da3-nonzero').samples[0].sample = at('version-nonzero').samples[0].sample; });
        assert.throws(() => deriveRun(dir), /owned routed-input evidence/);
        mutate(at => {
            const sample = at('version').samples[1].sample;
            const text = Buffer.from(sample.hex, 'hex').toString('ascii');
            replaceReply(sample, text.replace('\x1b[?2004;1$y', '\x1b[?2004;2$y'));
        });
        assert.throws(() => deriveRun(dir), /reported mode state changed/);
        mutate(at => {
            const { screen, sha256, afterSha256 } = at('modes');
            Object.assign(at('beam'), { screen, sha256, afterSha256 });
        });
        assert.throws(() => deriveRun(dir), /cursor-style control/);
    });
});

test('receipt rejects replaced VT binary or file range and failed cleanup', () => {
    withIdentityArchive(dir => {
        const metadataPath = join(dir, 'vt-metadata.json');
        const original = readFileSync(metadataPath);
        const fact = JSON.parse(original);
        writeFileSync(metadataPath, JSON.stringify({ ...fact, offset: fact.offset + 1 }));
        assert.throws(() => deriveRun(dir), /retained metadata bytes differ/);
        writeFileSync(metadataPath, original);

        const assemblyPath = join(dir, 'Ntilde.VT.dll'), image = readFileSync(assemblyPath);
        const changed = Buffer.from(image);
        changed[0] ^= 1;
        writeFileSync(assemblyPath, changed);
        assert.throws(() => deriveRun(dir), /VT binary digest/);
        writeFileSync(assemblyPath, image);

        const proofPath = join(dir, 'ownership-proof.json');
        const proof = readJson(dir, 'ownership-proof.json');
        proof.proofs[0].privateClipboard.restoredTerminalState += '-changed';
        writeFileSync(proofPath, JSON.stringify(proof));
        assert.throws(() => deriveRun(dir), /exact PTY cleanup/);
    });
});
