import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { caseId, replayClicks } from './rich-x11-kitty-clicks.mjs';
import { buildPublication, verifyRichKittyClickRun, withMeasurement } from './rich-x11-kitty-clicks-results.mjs';

test('the Kitty click publication rederives both SGR coordinate modes and the silent controls', () => {
    const { id, pinned, effect, anchors, deliveryControls, samples } = buildPublication();
    assert.equal(id, caseId);
    assert.equal(pinned.verdict, 'unprobed');
    assert.equal(pinned.stock, null);
    assert.equal(effect, 'supported');
    assert.deepEqual(anchors, { primary: 13, secondary: 20, cursor: 26 });
    assert.deepEqual(Object.values(deliveryControls).map(control => [control.column, control.row]),
        [[52, 9], [52, 14], [52, 20], [52, 26], [52, 31]]);
    assert.deepEqual(samples.filter(sample => sample.expectedHex).map(sample => sample.childHex[2]), [
        '1b5b3c303b35323b31344d', '1b5b3c303b35323b32304d', '1b5b3c303b35323b32364d',
        '1b5b3c303b35323b324d', '1b5b3c303b35323b314d', '1b5b3c303b35323b374d',
        '1b5b3c303b35323b314d', '1b5b3c303b35323b374d',
    ]);
    const silent = samples.filter(sample => !sample.expectedHex);
    assert.deepEqual(silent.map(sample => sample.phase + '/' + sample.position), [
        'disabled/secondary', 'relative/above', 'relative/below', 'erase-lines/secondary', 'erase-lines/continuation',
        'new-prompt/secondary', 'after-submit/secondary', 'reset/secondary',
    ]);
    assert.ok(silent.every(sample => sample.childHex.join('') === ''));
});

test('the replay refuses missing click delivery, a changed fixture and a lost input fence', () => {
    withMeasurement(directory => {
        const path = join(directory, 'observation.json');
        const fresh = () => JSON.parse(readFileSync(path));
        const coords = fresh();
        coords.promptClicks.calibration[2].steps[1].native.after.point[0]++;
        assert.throws(() => replayClicks(coords), /unowned XTEST delivery/);

        const marker = fresh();
        marker.promptClicks.phases[2].setup.writeHex = marker.promptClicks.phases[1].setup.writeHex;
        assert.throws(() => replayClicks(marker), /missing fresh mode/);

        const anchor = fresh();
        anchor.promptClicks.phases[2].screen.lines[19] = 'unmarked';
        assert.throws(() => replayClicks(anchor), /unexpected marked prompt screen/);

        const fence = fresh();
        fence.promptClicks.phases[2].clicks[0].steps[2].capture.chunks = [];
        assert.throws(() => replayClicks(fence), /around the PTY fence/);
    });
});

test('replay refuses to publish a run with an incomplete desktop lease', () => {
    withMeasurement(directory => {
        const path = join(directory, 'desktop-lease.jsonl');
        const incomplete = readFileSync(path, 'utf8').trimEnd().split('\n').slice(0, -1);
        writeFileSync(path, incomplete.join('\n') + '\n');
        assert.throws(() => verifyRichKittyClickRun(directory), /unproved X11 desktop lease cleanup/);
    });
});
