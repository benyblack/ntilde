import test from 'node:test';
import assert from 'node:assert/strict';
import { animationFrameColor, animationFrameInput, animationInputPins, animationFrameIds } from './kitty-animation-inputs.mjs';
import { animationCases, animationCommand, animationExpectedReplies, animationHoldMs } from './kitty-animation-cases.mjs';
import { classifyAnimationFrame, evaluateAnimationTiming, framesPossibleAt, inspectAnimationPixels } from './kitty-animation-oracle.mjs';

const grid = { originXPx: 2, originYPx: 4, cellWidthPx: 10, cellHeightPx: 18, renderWidthPx: 200, renderHeightPx: 160 };
const anchor = { col: 3, row: 4 }, cells = { cols: 4, rows: 2 };
const blank = () => {
    const value = { width: 200, height: 160, pixels: Buffer.alloc(200 * 160 * 4) };
    for (let i = 0; i < value.pixels.length; i += 4) value.pixels.set([16, 16, 16, 255], i);
    return value;
};
// Aspect fit in a 40x36 envelope at (32,76): a 36px square, offset 2px.
const square = { x: 34, y: 76, width: 36, height: 36 };
function draw(frameId, rect = square, edge = null) {
    const value = blank();
    for (let y = 0; y < rect.height; y++) for (let x = 0; x < rect.width; x++) {
        const border = edge && (x === 0 || y === 0 || x === rect.width - 1 || y === rect.height - 1);
        value.pixels.set(border ? edge : animationFrameColor(frameId), ((rect.y + y) * value.width + rect.x + x) * 4);
    }
    return value;
}
const inspect = (observed, appearance) => inspectAnimationPixels({ baseline: blank(), observed, grid, anchor, cells, appearance });
const all = items => items.every(item => item.passed);

test('frame inputs are fixed solid 16x16 RGBA bytes whose pins are enforced', () => {
    assert.deepEqual(animationFrameIds, ['root', 'second', 'third']);
    for (const id of animationFrameIds) {
        const input = animationFrameInput(id);
        assert.equal(input.bytes.length, 16 * 16 * 4); assert.equal(input.sha256, animationInputPins[id]);
        for (let i = 0; i < input.bytes.length; i += 4) assert.deepEqual([...input.bytes.subarray(i, i + 4)], input.color);
        input.bytes.fill(0); assert.deepEqual([...animationFrameInput(id).bytes.subarray(0, 4)], input.color);
    }
    assert.equal(new Set(animationFrameIds.map(id => animationInputPins[id])).size, 3);
    assert.throws(() => animationFrameInput('fourth')); assert.throws(() => animationFrameInput('__proto__'));
});

test('commands are exact bounded bytes from an explicit image id', () => {
    const build = animationCommand('kitty.animation.playback', 'build', { imageId: 7 });
    const apcs = build.split('\x1b\\').filter(Boolean);
    assert.equal(apcs.length, 4);
    assert.match(apcs[0], /^\x1b_Ga=T,f=32,s=16,v=16,t=d,i=7,p=1,c=4,r=2,C=1;/);
    assert.match(apcs[1], /^\x1b_Ga=f,f=32,s=16,v=16,t=d,i=7,z=5000;/); assert.match(apcs[2], /^\x1b_Ga=f,.*i=7,z=5000;/);
    assert.equal(apcs[3], `\x1b_Ga=a,i=7,r=1,z=${animationHoldMs}`);
    assert.equal(animationCommand('kitty.animation.playback', 'start', { imageId: 7 }), '\x1b_Ga=a,i=7,s=3\x1b\\');
    assert.equal(animationCommand('kitty.animation.stop-freeze', 'stop', { imageId: 7 }), '\x1b_Ga=a,i=7,s=1\x1b\\');
    assert.equal(animationCommand('kitty.animation.playback', 'clear', { imageId: 7 }), '\x1b_Ga=d,d=I,i=7\x1b\\\x1b[2J\x1b[H');
    for (const stageId of ['blank', 'second', 'third', 'loop']) assert.equal(animationCommand('kitty.animation.playback', stageId, { imageId: 7 }), '');
    assert.equal(animationCommand('kitty.animation.stopped-hold', 'hold', { imageId: 7 }), '');
    assert.throws(() => animationCommand('kitty.animation.playback', 'build', {})); assert.throws(() => animationCommand('kitty.animation.playback', 'build', { imageId: 0 }));
    assert.throws(() => animationCommand('kitty.animation.playback', 'nope', { imageId: 1 })); assert.throws(() => animationCommand('nope', 'build', { imageId: 1 }));
    assert.deepEqual(animationExpectedReplies('build', 7), Array(4).fill('\x1b_Gi=7;OK\x1b\\'));
    assert.deepEqual(animationExpectedReplies('start', 7), ['\x1b_Gi=7;OK\x1b\\']); assert.deepEqual(animationExpectedReplies('clear', 7), []);
});

test('every case begins blank, builds, and ends cleared, with unique stage ids and timed targets inside hold windows', () => {
    assert.equal(new Set(animationCases.map(item => item.id)).size, animationCases.length);
    for (const item of animationCases) {
        assert.equal(item.stages[0].id, 'blank'); assert.equal(item.stages.at(-1).id, 'clear');
        assert.equal(new Set(item.stages.map(stage => stage.id)).size, item.stages.length);
        for (const stage of item.stages.filter(value => value.targetMs !== undefined)) {
            const slot = Math.floor(stage.targetMs / animationHoldMs);
            assert.equal(['root', 'second', 'third'][slot % 3], stage.appearance, `${item.id}.${stage.id} aims inside its own window`);
            const offset = stage.targetMs % animationHoldMs; assert.ok(offset >= 1000 && offset <= animationHoldMs - 1000, 'centered with latency margin');
        }
    }
    assert.ok(animationCases.flatMap(item => item.stages).length * 2 + 8 <= 64, 'paired captures fit the per-suite budget');
});

for (const id of animationFrameIds) test(`a clean ${id} frame classifies exactly and passes only its own appearance`, () => {
    const observed = draw(id), value = inspect(observed, id);
    assert.ok(all(value.controls)); assert.ok(all(value.assertions));
    assert.deepEqual(value.classification, { kind: 'frame', frameId: id, geometryOk: true });
    for (const other of animationFrameIds.filter(item => item !== id)) assert.equal(inspect(observed, other).assertions[0].passed, false);
    assert.equal(inspect(observed, 'absent').assertions[0].passed, false);
    assert.match(value.scope, /no ownership, freshness, timing or stock grade/);
});

test('antialiased edge pixels do not change the named frame but a mixed interior does', () => {
    assert.equal(classifyAnimationFrame({ image: draw('second', square, [90, 140, 30, 255]), grid, anchor, cells }).frameId, 'second');
    const mixed = draw('second'); mixed.pixels.set([255, 0, 0, 255], ((square.y + 18) * mixed.width + square.x + 18) * 4);
    assert.deepEqual(classifyAnimationFrame({ image: mixed, grid, anchor, cells }).kind, 'mixed');
    assert.equal(inspect(mixed, 'second').assertions[0].passed, false);
});

test('blank capture is background, passes the absent appearance and fails any frame', () => {
    const value = inspect(blank(), 'absent');
    assert.ok(all(value.controls)); assert.ok(all(value.assertions)); assert.equal(value.classification.kind, 'background');
    assert.equal(inspect(blank(), 'root').assertions[0].passed, false);
});

test('stretched or shifted geometry and an unknown color fail; changed pixels outside the target fail the control', () => {
    assert.equal(inspect(draw('root', { x: 32, y: 76, width: 40, height: 36 }), 'root').assertions[0].passed, false);
    const unknown = draw('root'); for (let i = 0; i < unknown.pixels.length; i += 4) if (unknown.pixels[i] === 255 && unknown.pixels[i + 1] === 0) unknown.pixels.set([200, 0, 0, 255], i);
    assert.equal(classifyAnimationFrame({ image: unknown, grid, anchor, cells }).frameId, null);
    const outside = draw('root'); outside.pixels.set([255, 255, 255, 255], 0);
    assert.equal(all(inspect(outside, 'root').controls), false);
    const dirtyBaseline = inspectAnimationPixels({ baseline: draw('third'), observed: draw('root'), grid, anchor, cells, appearance: 'root' });
    assert.equal(dirtyBaseline.controls[0].passed, false);
});

test('malformed images, grids and appearances are refused', () => {
    assert.throws(() => inspect({ width: 2, height: 2, pixels: Buffer.alloc(3) }, 'root'));
    assert.throws(() => inspect(blank(), 'fourth'));
    assert.throws(() => classifyAnimationFrame({ image: blank(), grid: { ...grid, renderWidthPx: 1 }, anchor, cells }));
    assert.throws(() => inspectAnimationPixels({ baseline: blank(), observed: { ...blank(), width: 100, pixels: Buffer.alloc(100 * 160 * 4) }, grid, anchor, cells, appearance: 'root' }));
});

test('the independent timeline picks the frame from elapsed time and refuses ambiguity', () => {
    const start = { earliest: 1000, latest: 1300 };
    assert.deepEqual(framesPossibleAt(start, { start: 500, end: 900 }), ['root'], 'before the start the root is shown');
    assert.deepEqual(framesPossibleAt(start, { start: 1500, end: 2500 }), ['root']);
    assert.deepEqual(framesPossibleAt(start, { start: 7000, end: 8000 }), ['second']);
    assert.deepEqual(framesPossibleAt(start, { start: 12000, end: 13000 }), ['third']);
    assert.deepEqual(framesPossibleAt(start, { start: 17000, end: 18000 }), ['root'], 'it loops');
    assert.deepEqual(framesPossibleAt(start, { start: 5900, end: 6200 }), ['root', 'second'], 'start uncertainty straddles the boundary');
    assert.deepEqual(framesPossibleAt(start, { start: 6000, end: 40000 }), ['root', 'second', 'third']);
    assert.throws(() => framesPossibleAt({ earliest: 5, latest: 1 }, { start: 0, end: 1 })); assert.throws(() => framesPossibleAt(start, { start: NaN, end: 1 }));
});

const stageOf = (caseId, id) => animationCases.find(item => item.id === caseId).stages.find(stage => stage.id === id);
const start = { earliest: 100_000, latest: 100_300 };
const at = (offset, length = 1500) => ({ start: start.latest + offset, end: start.latest + offset + length });

test('running reads are valid only when the whole capture span sits in the planned window', () => {
    const second = stageOf('kitty.animation.playback', 'second');
    assert.deepEqual(evaluateAnimationTiming(second, { start, capture: at(6000) }), { valid: true, reason: null });
    assert.match(evaluateAnimationTiming(second, { start, capture: at(4300) }).reason, /straddles/);
    assert.match(evaluateAnimationTiming(second, { start, capture: at(11000) }).reason, /third window, not the planned second/);
    assert.match(evaluateAnimationTiming(second, { capture: at(6000) }).reason, /start time unavailable/);
    assert.match(evaluateAnimationTiming(second, { start, capture: { start: 5, end: 1 } }).reason, /unavailable/);
    assert.equal(evaluateAnimationTiming(stageOf('kitty.animation.playback', 'loop'), { start, capture: at(16500) }).valid, true);
});

test('a stopped animation is proved stationary only past a hold the model says would have advanced it', () => {
    const hold = stageOf('kitty.animation.stop-freeze', 'hold'), stop = { earliest: 108_000, latest: 108_200 };
    assert.equal(evaluateAnimationTiming(stageOf('kitty.animation.stop-freeze', 'stop'), { start, stop, capture: { start: 108_300, end: 109_500 } }).valid, true);
    assert.equal(evaluateAnimationTiming(hold, { start, stop, capture: { start: 114_500, end: 116_000 } }).valid, true);
    assert.match(evaluateAnimationTiming(hold, { start, stop, capture: { start: 108_500, end: 109_000 } }).reason, /could still show second/);
    assert.match(evaluateAnimationTiming(hold, { start, stop: { earliest: 112_000, latest: 112_100 }, capture: { start: 118_000, end: 119_000 } }).reason, /stop did not land inside the second window/);
    assert.match(evaluateAnimationTiming(hold, { start, capture: { start: 114_000, end: 115_000 } }).reason, /stop time unavailable/);
    assert.match(evaluateAnimationTiming(stageOf('kitty.animation.stop-freeze', 'stop'), { start, stop, capture: { start: 107_000, end: 107_500 } }).reason, /precedes the stop/);
});

test('without a start, a hold must outlast the root gap since the frame was built', () => {
    const hold = stageOf('kitty.animation.stopped-hold', 'hold'), build = { earliest: 50_000, latest: 50_400 };
    assert.equal(evaluateAnimationTiming(hold, { build, capture: { start: 56_000, end: 57_000 } }).valid, true);
    assert.match(evaluateAnimationTiming(hold, { build, capture: { start: 54_000, end: 55_000 } }).reason, /outlast the root hold/);
    assert.match(evaluateAnimationTiming(hold, { capture: { start: 56_000, end: 57_000 } }).reason, /outlast the root hold/);
    assert.deepEqual(evaluateAnimationTiming(stageOf('kitty.animation.playback', 'blank'), {}), { valid: true, reason: null });
});
