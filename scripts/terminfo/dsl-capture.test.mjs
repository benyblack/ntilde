import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DslObserver, timingClock, validateTiming } from './dsl-capture.mjs';

test('a timing record is whole milliseconds of the parent monotonic clock and nothing else', () => {
    assert.deepEqual(validateTiming({ elapsedMs: 12, clock: timingClock }), { elapsedMs: 12, clock: 'parent-monotonic' });
    assert.deepEqual(validateTiming({ elapsedMs: 0, clock: timingClock }), { elapsedMs: 0, clock: timingClock });
});

test('a missing, fractional, negative, absurd or foreign timing is refused, never turned into a reading', () => {
    for (const bad of [undefined, null, 5, {}, { elapsedMs: 12 }, { elapsedMs: 12.5, clock: timingClock }, { elapsedMs: -1, clock: timingClock }, { elapsedMs: 600_001, clock: timingClock },
        { elapsedMs: 12, clock: 'worker-wall' }, { elapsedMs: 12, clock: timingClock, extra: 1 }, { elapsedMs: '12', clock: timingClock }, { elapsedMs: Number.NaN, clock: timingClock }]) {
        assert.throws(() => validateTiming(bad), /Invalid DECSCLM timing record/);
    }
});

test('the observer needs a clock function from the parent', () => {
    assert.throws(() => new DslObserver({ nonce: 'NTILDE_DSL_' + 'a'.repeat(32), now: 5 }), /needs a monotonic clock/);
});
