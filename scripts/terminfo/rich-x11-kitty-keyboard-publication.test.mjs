import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { buildPublication, withMeasurement } from './rich-x11-kitty-results.mjs';
import { plans, replayKeyboard } from './rich-x11-kitty-keyboard.mjs';

test('the owned Rich Kitty archive rederives the exact child bytes for all five flags', () => {
    const { rows, controls } = buildPublication();
    assert.deepEqual(rows.map(row => row.id), plans.map(plan => plan.id));
    assert.deepEqual(controls.map(control => control.childHex), ['61', '09']);
    assert.deepEqual(rows.map(row => row.effect), Array(5).fill('supported'));
    assert.equal(rows[0].childHex, '1b5b3130353b3575');
    assert.ok(rows[1].repeats > 0);
    assert.deepEqual(rows.slice(2).map(row => row.childHex), ['1b5b39373a36353b3675', '1b5b393775', '1b5b39373b313b393775']);
});

test('false keyboard custody, release or negotiation evidence refuses replay', () => {
    withMeasurement(directory => {
        const path = join(directory, 'observation.json');
        const fresh = () => JSON.parse(readFileSync(path));
        const focus = fresh();
        focus.cases[0].target.native.focus = -1;
        assert.throws(() => replayKeyboard(focus), /focus/);
        const release = fresh();
        release.cases[1].target.native.events.pop();
        assert.throws(() => replayKeyboard(release), /incomplete XTEST chord/);
        const mask = fresh();
        mask.cases[2].push.replyHex = mask.cases[0].push.replyHex;
        assert.throws(() => replayKeyboard(mask), /missing fresh mode observations/);
        const delimiter = fresh();
        delimiter.cases[3].target.capture.chunks.pop();
        assert.throws(() => replayKeyboard(delimiter), /fence/);
    });
});
