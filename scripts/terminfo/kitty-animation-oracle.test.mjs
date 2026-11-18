import { createHash } from 'node:crypto';
import test from 'node:test';
import assert from 'node:assert/strict';
import { animationAppearanceColor, animationAppearanceIds, animationComposedIds, animationFrameInput, animationInputPins, animationFrameIds, animationPngIds, animationPngInput, animationPngPins, composeOver, composedTolerance } from './kitty-animation-inputs.mjs';
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
        value.pixels.set(border ? edge : animationAppearanceColor(frameId).color, ((rect.y + y) * value.width + rect.x + x) * 4);
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
    const stageOf = (caseId, stageId) => animationCases.find(item => item.id === caseId).stages.find(item => item.id === stageId);
    assert.deepEqual(animationExpectedReplies(stageOf('kitty.animation.playback', 'build'), 7), ['\x1b_Gi=7,p=1;OK\x1b\\', ...Array(3).fill('\x1b_Gi=7;OK\x1b\\')], 'a=T echoes its placement id');
    assert.deepEqual(animationExpectedReplies(stageOf('kitty.animation.playback', 'start'), 7), ['\x1b_Gi=7;OK\x1b\\']); assert.deepEqual(animationExpectedReplies(stageOf('kitty.animation.playback', 'clear'), 7), []);
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

test('the PNG inputs are independent, pinned, valid 16x16 RGBA PNGs and composed colors follow the protocol blend', () => {
    assert.deepEqual(animationPngIds, ['overRoot', 'overEdit']);
    for (const id of animationPngIds) {
        const input = animationPngInput(id);
        assert.equal(input.sha256, animationPngPins[id]); assert.equal(input.bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
        assert.equal(input.bytes.readUInt32BE(16), 16); assert.equal(input.bytes.readUInt32BE(20), 16); assert.equal(input.bytes[24], 8); assert.equal(input.bytes[25], 6);
        assert.equal(createHash('sha256').update(animationPngInput(id).bytes).digest('hex'), input.sha256, 'deterministic');
    }
    assert.throws(() => animationPngInput('nope')); assert.throws(() => animationPngInput('__proto__'));
    // Half-transparent green over the opaque red root, and half-transparent red over the opaque blue edited root.
    assert.deepEqual(composeOver([0, 255, 0, 128], [255, 0, 0, 255]), [127, 128, 0, 255]);
    assert.deepEqual(composeOver([255, 0, 0, 128], [0, 0, 255, 255]), [128, 0, 127, 255]);
    assert.deepEqual(animationComposedIds, ['overRoot', 'overEdit']);
    assert.deepEqual(animationAppearanceIds, ['root', 'second', 'third', 'overRoot', 'overEdit']);
    assert.deepEqual(animationAppearanceColor('overRoot'), { color: [127, 128, 0, 255], tolerance: composedTolerance });
    assert.deepEqual(animationAppearanceColor('root'), { color: [255, 0, 0, 255], tolerance: 0 });
});

for (const id of animationComposedIds) test(`a composed ${id} frame passes within the blend rounding step and only its own appearance`, () => {
    const { color } = animationAppearanceColor(id), near = draw(id);
    const step = (value, by) => (value + by > 255 || value + by < 0 ? value - by : value + by);   // stays a valid byte
    for (let i = 0; i < near.pixels.length; i += 4) if (near.pixels[i] === color[0] && near.pixels[i + 1] === color[1] && near.pixels[i + 2] === color[2]) near.pixels.set([step(color[0], 2), color[1], step(color[2], 2), 255], i);
    const value = inspect(near, id);
    assert.ok(all(value.controls)); assert.ok(all(value.assertions)); assert.equal(value.classification.frameId, id);
    const far = draw(id); for (let i = 0; i < far.pixels.length; i += 4) if (far.pixels[i] === color[0] && far.pixels[i + 1] === color[1]) far.pixels.set([step(color[0], 3), color[1], color[2], 255], i);
    assert.equal(inspect(far, id).assertions[0].passed, false, 'three steps off is not the composed color');
    for (const other of animationAppearanceIds.filter(item => item !== id)) assert.equal(inspect(draw(id), other).assertions[0].passed, false);
});

test('the PNG and root frames case is bounded exact bytes with explicit frame choices and no elapsed time', () => {
    const caseId = 'kitty.animation.png-and-root-frames', item = animationCases.find(value => value.id === caseId);
    assert.deepEqual(item.stages.map(stage => [stage.id, stage.appearance, stage.clock]), [['blank', 'absent', 'static'], ['build', 'root', 'static'], ['show', 'overRoot', 'static'],
        ['edit', 'third', 'static'], ['compose', 'overEdit', 'static'], ['clear', 'absent', 'static']]);
    const apcs = id => animationCommand(caseId, id, { imageId: 9 }).split('\x1b\\').filter(Boolean);
    const build = apcs('build'); assert.equal(build.length, 2);
    assert.match(build[0], /^\x1b_Ga=T,f=32,s=16,v=16,t=d,i=9,p=1,c=4,r=2,C=1;/);
    assert.equal(build[1], `\x1b_Ga=f,f=100,t=d,i=9,c=1,z=${animationHoldMs};` + animationPngInput('overRoot').bytes.toString('base64'));
    assert.deepEqual(apcs('show'), ['\x1b_Ga=a,i=9,c=2']);
    const edit = apcs('edit'); assert.equal(edit.length, 2);
    assert.equal(edit[0], '\x1b_Ga=f,f=32,s=16,v=16,t=d,i=9,r=1;' + animationFrameInput('third').bytes.toString('base64')); assert.equal(edit[1], '\x1b_Ga=a,i=9,c=1');
    const compose = apcs('compose'); assert.equal(compose.length, 2);
    assert.equal(compose[0], `\x1b_Ga=f,f=100,t=d,i=9,c=1,z=${animationHoldMs};` + animationPngInput('overEdit').bytes.toString('base64')); assert.equal(compose[1], '\x1b_Ga=a,i=9,c=3');
    assert.equal(animationCommand(caseId, 'blank', { imageId: 9 }), ''); assert.equal(animationCommand(caseId, 'clear', { imageId: 9 }), '\x1b_Ga=d,d=I,i=9\x1b\\\x1b[2J\x1b[H');
    const replies = id => animationExpectedReplies(item.stages.find(stage => stage.id === id), 9);
    assert.deepEqual(replies('build'), ['\x1b_Gi=9,p=1;OK\x1b\\', '\x1b_Gi=9;OK\x1b\\']);
    assert.deepEqual(replies('show'), ['\x1b_Gi=9;OK\x1b\\']); assert.deepEqual(replies('compose'), Array(2).fill('\x1b_Gi=9;OK\x1b\\')); assert.deepEqual(replies('clear'), []);
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

test('after a relaunch a read must differ from what a playhead the App restarted or resumed would show', () => {
    const original = { earliest: 0, latest: 200 }, window = (start, end) => ({ start, end });
    const timeline = { clock: 'running', appearance: 'timeline' };
    // The original start gives the second frame at 9.4 s, and so would a playhead restarted anywhere in 4-9 s: the read cannot tell a restore from a restart.
    assert.match(evaluateAnimationTiming(timeline, { start: original, relaunch: { earliest: 4000, latest: 9000 }, capture: window(9400, 9600) }).reason, /restarted by the relaunch could show the second frame/);
    // Back by 5.7 s, a restart shows the root at 5.8-6.2 s, the restored playhead the second frame.
    assert.deepEqual(evaluateAnimationTiming(timeline, { start: original, relaunch: { earliest: 4000, latest: 5700 }, capture: window(5800, 6200) }), { valid: true, reason: null });
    // A stopped animation stays on its second frame past a hold; a playhead resumed from that frame when the App came back would have moved on at 14.7 s but shows it again a cycle later.
    const hold = { clock: 'frozen', appearance: 'second', waitMs: 6000 }, stop = { earliest: 6200, latest: 6300 }, relaunch = { earliest: 6400, latest: 8200 };
    assert.deepEqual(evaluateAnimationTiming(hold, { start: original, stop, relaunch, capture: window(14_700, 14_860) }), { valid: true, reason: null });
    assert.match(evaluateAnimationTiming(hold, { start: original, stop, relaunch, capture: window(26_000, 26_200) }).reason, /resumed from the frozen frame by the relaunch could still show second/);
});
