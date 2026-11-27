import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { replay } from './modern-x11-osc5522-clipboard.mjs';
import { buildPublication, publish, verifyRun, withMeasurement } from './osc5522-clipboard-results.mjs';

test('the archived X11 receipt and the unchanged pinned callback rederive all three verdicts', async () => {
    const { report } = await buildPublication();
    const [row] = report.cases;
    assert.equal(row.pinned.observation.outcome, 'supported');
    assert.equal(row.pinned.termless.observation.outcome, 'supported');
    assert.deepEqual(row.history.map(sample => sample.outcome), ['unsupported', 'supported']);
    assert.equal(row.contractResult.outcome, 'supported');
    assert.equal(row.stock.outcome, 'unsupported');
    await publish(true);
});

test('mode controls, MIME snapshot, duplicate suppression and consent must come from the retained windows', () => {
    withMeasurement(scratch => {
        const { run } = verifyRun(scratch);

        const badMode = structuredClone(run);
        const mode = badMode.cases[0]['on-query'].checkpoint;
        mode.chunks[0].hex = mode.chunks[0].hex.replace('3b31', '3b32');
        assert.throws(() => replay(badMode), /control differs/);

        const replaced = structuredClone(run);
        replaced.cases[0].pasteRead.result.values = replaced.cases[0].later;
        assert.throws(() => replay(replaced), /independent clipboard readback differs/);

        const duplicate = structuredClone(run);
        const step = duplicate.cases[0].on;
        step.capture.chunks[0].hex = run.results[0].effect.offHex + step.capture.chunks[0].hex;
        assert.throws(() => replay(duplicate), /without ordinary paste bytes/);

        const trailingRecord = structuredClone(run);
        const trailing = trailingRecord.cases[0].on;
        trailing.capture.chunks.findLast(chunk => BigInt(chunk.at) < BigInt(trailing.native.fence.started)).hex += '0a';
        assert.throws(() => replay(trailingRecord), /without ordinary paste bytes/);

        const unrelated = structuredClone(run);
        unrelated.cases[0].reuse.dialog.owner++;
        assert.throws(() => replay(unrelated), /fresh explicit consent/);

        const late = structuredClone(run);
        const reuse = late.cases[0].reuse.capture;
        const delay = at => (BigInt(at) + 15_000_000_000n).toString();
        for (const chunk of reuse.chunks) chunk.at = delay(chunk.at);
        reuse.sealed = delay(reuse.sealed);
        assert.throws(() => replay(late), /not denied inside its validity window/);

        const lost = structuredClone(run);
        lost.cases[0].restoreRead.result.values = lost.cases[0].later;
        assert.throws(() => replay(lost), /independent clipboard readback differs/);
    });
});

test('a receipt with missing GTK process cleanup is refused even with intact PTY bytes', () => {
    withMeasurement(scratch => {
        const path = join(scratch, 'observation.json');
        const record = JSON.parse(readFileSync(path));
        record.cleanup.peers.pop();
        writeFileSync(path, JSON.stringify(record));
        assert.throws(() => verifyRun(scratch), /unproved GTK peer cleanup/);
    });
});
