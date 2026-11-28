import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { caseIds, deriveRun, gradeObservations } from './kitty-aggregate-owned.mjs';
import { replayPinnedAggregates } from './kitty-aggregate-pinned.mjs';
import { buildPublication, bundleArchive, publication, renderReport, withAggregateArchive } from './kitty-aggregate-publication.mjs';

const report = await buildPublication();
const hex = text => Buffer.from(text).toString('hex');
const rowNamed = (rows, stage) => rows.find(row => row.stage === stage);

test('the retained receipt and both pinned paths rederive the exact published cases', () => {
    assert.deepEqual(report, JSON.parse(readFileSync(join(publication, 'report.json'))));
    assert.equal(renderReport(report), readFileSync(join(publication, 'report.md'), 'utf8').replaceAll('\r\n', '\n'));
    assert.deepEqual(report.cases.map(row => row.id), caseIds);
    for (const row of report.cases) {
        assert.equal(row.stock, 'supported');
        assert.equal(row.pinned.terminal.observation.outcome, 'supported');
        assert.equal(row.pinned.termless.observation.outcome, 'supported');
        assert.equal(row.owned.verdict, 'supported');
    }
    assert.deepEqual(report.effects, {
        keyboard: { before: 0, active: 1, after: 0, bitOneActivated: true },
        graphics: { accepted: true, imageAbsent: true, placementCount: 0, unchanged: true },
    });
});

test('an ignored push, wrong reply ID or late reply cannot get a supported pinned verdict', async () => {
    const rows = structuredClone(report.observations);
    const archive = readFileSync(bundleArchive);
    rowNamed(rows, 'active').replyHex = hex('\x1b[?0u');
    rowNamed(rows, 'query').replyHex = hex('\x1b_Gi=32;OK\x1b\\');
    const replay = await replayPinnedAggregates(archive, rows);
    for (const path of ['terminal', 'termless']) {
        assert.equal(replay[caseIds[0]][path].observation.outcome, 'unsupported');
        assert.equal(replay[caseIds[1]][path].observation.outcome, 'inconclusive');
    }
    // Replay the pinned transport with a complete reply after the sentinel. The owned
    // transcript parser refuses this order before publication would reach this adapter.
    const detection = rowNamed(rows, 'detect');
    const original = detection.replyHex;
    detection.replyHex = '';
    detection.sentinelReplyHex += original;
    const late = await replayPinnedAggregates(archive, rows);
    assert.equal(late[caseIds[0]].terminal.observation.outcome, 'inconclusive');
    assert.equal(late[caseIds[0]].termless.observation.outcome, 'inconclusive');
});

test('an ACK can coexist with a changed image store or screen, while failed controls refuse the experiment', () => {
    const rows = structuredClone(report.observations);
    rowNamed(rows, 'missing-after').replyHex = rowNamed(rows, 'place-control').replyHex;
    rowNamed(rows, 'query').placementCount = 1;
    rowNamed(rows, 'query').unchanged = false;
    assert.deepEqual(gradeObservations(rows).graphics, { accepted: true, imageAbsent: false, placementCount: 1, unchanged: false });
    rowNamed(rows, 'place-control').painted = false;
    assert.throws(() => gradeObservations(rows), /Invalid or incomplete/);
    const unbalanced = structuredClone(report.observations);
    rowNamed(unbalanced, 'restored').replyHex = rowNamed(unbalanced, 'active').replyHex;
    assert.throws(() => gradeObservations(unbalanced), /Invalid or incomplete/);
});

function tryRewrite(directory, file, mutate) {
    const path = join(directory, file), original = readFileSync(path, 'utf8');
    try {
        const lines = original.trimEnd().split('\n').map(line => JSON.parse(line));
        mutate(lines);
        writeFileSync(path, lines.map(line => JSON.stringify(line)).join('\n') + '\n');
        assert.throws(() => deriveRun(directory), /Invalid or incomplete/);
    } finally {
        writeFileSync(path, original);
    }
}

test('fixture changes, cross-stage delivery fences, absent cleanup and altered original pixels are refused', () => withAggregateArchive(directory => {
    tryRewrite(directory, 'aggregate-actions.jsonl', actions => {
        actions.find(action => action.stageId === 'query').commandHex = hex('\x1b_Gi=31,s=1,v=1,a=t,t=d,f=24;AAAA\x1b\\\x1b[c');
    });
    tryRewrite(directory, 'aggregate-frames.jsonl', frames => {
        const find = name => frames.find(frame => frame.stageId === name);
        const { barrier, nonces } = find('detect').capture;
        Object.assign(find('active').capture, { barrier, nonces });
    });
    tryRewrite(directory, 'aggregate-frames.jsonl', frames => {
        frames.find(frame => frame.stageId === 'query').pixels[0].sha256 = '0'.repeat(64);
    });
    const proof = join(directory, 'ownership-proof.json');
    const original = readFileSync(proof);
    writeFileSync(proof, JSON.stringify({ ...JSON.parse(original), completed: false }));
    assert.throws(() => deriveRun(directory), /Invalid or incomplete/);
}));
