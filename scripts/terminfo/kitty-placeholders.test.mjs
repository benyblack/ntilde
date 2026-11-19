import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { syntheticAnimationJournal } from './fixtures/kitty-animation-journal.mjs';
import { buildAnimationReport, animationInputs, animationSuites, animationVersionOfPolicy, ownedAnimationPolicies } from './kitty-animation.mjs';
import { animationCasesByVersion, animationCommand, animationExpectedReplies, animationSuiteIds, placeholderAnimationVersion, placeholderCellEnvelope, placeholderMaxImageId } from './kitty-animation-cases.mjs';
import { placeholderAppearances, placeholderColors, placeholderHalvesInput, placeholderInputPin } from './kitty-animation-inputs.mjs';
import { inspectPlaceholderPixels } from './kitty-animation-oracle.mjs';
import { inputRunId } from './fixtures/input-journal.mjs';

const version = placeholderAnimationVersion;
const [tiles, mapping, unbound] = ['tiles', 'cell-mapping', 'unbound'].map(name => 'kitty.placeholders.' + name);
function withPlaceholders(options, action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-kpl-'));
    try {
        const journal = syntheticAnimationJournal(output, { version, ...options });
        action({ output, ...journal, build: (frames = journal.frames, actions = journal.actions, seal = journal.seal) => buildAnimationReport(frames, actions, seal, inputRunId, output, [], version) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
const results = report => Object.fromEntries(report.observations.map(item => [item.featureId, item.result]));
const paintAs = (caseId, stageId, appearance) => (entry, shown) => (entry.caseId === caseId && entry.stageId === stageId ? appearance : shown);

test('the red/blue input is a pinned 16x16 RGBA image whose halves are the two named colors', () => {
    const input = placeholderHalvesInput();
    assert.equal(input.bytes.length, 16 * 16 * 4); assert.equal(input.sha256, placeholderInputPin);
    for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) {
        assert.deepEqual([...input.bytes.subarray((y * 16 + x) * 4, (y * 16 + x + 1) * 4)], placeholderColors[x < 8 ? 'red' : 'blue']);
    }
    input.bytes.fill(0); assert.deepEqual([...placeholderHalvesInput().bytes.subarray(0, 4)], placeholderColors.red);
    assert.deepEqual(animationInputs(version).map(item => item.file), ['animation-inputs/halves.rgba']);
    assert.notDeepEqual(placeholderColors.red, placeholderColors.blue);
});

test('the placeholder suite plans three static cases that need no relaunch, elapsed time or second kind of tab', () => {
    assert.deepEqual(animationCasesByVersion[version].map(item => item.id), [tiles, mapping, unbound]);
    assert.equal(animationSuiteIds[version], 'ntilde-owned-kitty-placeholders-v1');
    assert.equal(animationVersionOfPolicy(ownedAnimationPolicies[version]), version);
    assert.equal(animationSuites[version].plan.length, 1 + 19 + 3 + 1); assert.ok((animationSuites[version].plan.length + 1) * 2 <= 64);
    assert.deepEqual(placeholderCellEnvelope, { cols: 2, rows: 1 });
    for (const item of animationCasesByVersion[version]) {
        assert.equal(item.stages[0].id, 'blank'); assert.equal(item.stages.at(-1).id, 'clear'); assert.equal(item.stages[1].id, 'build');
        assert.equal(new Set(item.stages.map(stage => stage.id)).size, item.stages.length);
        for (const stage of item.stages) {
            assert.equal(stage.clock, 'static'); assert.ok(!stage.relaunch);
            assert.ok(Object.hasOwn(placeholderAppearances, stage.appearance), stage.appearance);
            assert.equal(animationSuites[version].relaunches({ kind: 'read', caseId: item.id, stageId: stage.id }), false);
        }
    }
});

test('commands are the protocol\'s own bytes from an explicit 24-bit image id, with the diacritics and colours written out', () => {
    const id = 0x0102ab, color = '\x1b[38;2;1;2;171m', placeholder = '\u{10EEEE}';
    const command = (caseId, stageId) => animationCommand(caseId, stageId, { imageId: id }, version);
    const build = command(tiles, 'build').split('\x1b\\').filter(Boolean);
    assert.equal(build.length, 2);
    assert.match(build[0], new RegExp(`^\\x1b_Ga=t,f=32,s=16,v=16,t=d,i=${id};`)); assert.equal(build[1], `\x1b_Ga=p,U=1,i=${id},p=1,c=2,r=1`);
    assert.equal(Buffer.from(build[0].split(';')[1], 'base64').equals(placeholderHalvesInput().bytes), true);
    const run = (placement, ...cells) => color + `\x1b[58;5;${placement}m` + cells.join('') + '\x1b[39m\x1b[59m';
    const cell = (row, column) => placeholder + (row === undefined ? '' : ['̅', '̍', '̎'][row] + ['̅', '̍', '̎'][column]);
    assert.equal(command(tiles, 'show'), run(1, cell(0, 0), cell(0, 1)));
    assert.equal(command(tiles, 'erase'), '\x1b[2J');
    assert.equal(command(mapping, 'swapped'), run(1, cell(0, 1), cell(0, 0)));
    assert.equal(command(mapping, 'single'), '\x1b[C' + run(1, cell(0, 0)));
    assert.equal(command(mapping, 'inherit'), run(1, cell(0, 0), cell()));
    assert.equal(command(unbound, 'wrong-image'), '\x1b[38;2;1;2;172m' + `\x1b[58;5;1m` + cell(0, 0) + cell(0, 1) + '\x1b[39m\x1b[59m');
    assert.equal(command(unbound, 'stale-placement'), run(2, cell(0, 0), cell(0, 1)));
    assert.equal(command(unbound, 'outside-grid'), run(1, cell(1, 0), cell(1, 1)));
    assert.equal(command(unbound, 'valid'), command(tiles, 'show'));
    assert.equal(command(unbound, 'deleted'), `\x1b_Ga=d,d=i,i=${id}\x1b\\`);
    assert.equal(command(tiles, 'clear'), `\x1b_Ga=d,d=I,i=${id}\x1b\\\x1b[2J\x1b[H`);
    assert.equal(command(tiles, 'blank'), '');
    // No stage leaves the controlled cell background (SGR 0 or 48) in the command, so transparent pixels show the known background.
    for (const item of animationCasesByVersion[version]) for (const stage of item.stages) assert.doesNotMatch(command(item.id, stage.id), /\x1b\[0?m|\x1b\[48/);
    assert.throws(() => animationCommand(tiles, 'show', { imageId: placeholderMaxImageId + 1 }, version)); assert.throws(() => animationCommand(tiles, 'show', {}, version));
    assert.throws(() => animationCommand(tiles, 'nonexistent', { imageId: id }, version));
    const replies = (caseId, stageId) => animationExpectedReplies(animationCasesByVersion[version].find(item => item.id === caseId).stages.find(stage => stage.id === stageId), id);
    assert.deepEqual(replies(tiles, 'build'), [`\x1b_Gi=${id};OK\x1b\\`, `\x1b_Gi=${id},p=1;OK\x1b\\`]);
    assert.deepEqual(replies(tiles, 'show'), []);
});

// Pixels. A 16x18 envelope: two 8x18 cells; the 16x16 source fits as a 16-tall band.
const grid = { originXPx: 2, originYPx: 4, cellWidthPx: 8, cellHeightPx: 18, renderWidthPx: 120, renderHeightPx: 160 };
const anchor = { col: 3, row: 4 };
const blank = () => {
    const value = { width: 120, height: 160, pixels: Buffer.alloc(120 * 160 * 4) };
    for (let i = 0; i < value.pixels.length; i += 4) value.pixels.set([16, 16, 16, 255], i);
    return value;
};
const cellX = cell => 2 + 3 * 8 + cell * 8, bandY = 4 + 4 * 18;
function drawBands(colors, { top = 0, height = 16, edge = null } = {}) {
    const value = blank();
    colors.forEach((name, cell) => {
        if (name === 'absent') return;
        for (let y = 0; y < height; y++) for (let x = 0; x < 8; x++) {
            const border = edge && (x === 0 || y === 0 || x === 7 || y === height - 1);
            value.pixels.set(border ? edge : (placeholderColors[name] ?? name), ((bandY + top + y) * value.width + cellX(cell) + x) * 4);
        }
    });
    return value;
}
const inspect = (observed, appearance) => inspectPlaceholderPixels({ baseline: blank(), observed, grid, anchor, cells: placeholderCellEnvelope, appearance });
const all = items => items.every(item => item.passed);

test('each placeholder appearance passes only for the tiles it names, wherever the band sits inside the cell', () => {
    for (const [id, colors] of Object.entries(placeholderAppearances)) {
        for (const top of [0, 1, 2]) {
            const result = inspect(drawBands(colors, { top }), id);
            assert.ok(all(result.controls) && all(result.assertions), `${id} at ${top}`);
            assert.equal(result.classification.kind, id === 'absent' ? 'background' : 'tiles');
            assert.deepEqual(result.classification.tiles, colors);
        }
        for (const other of Object.keys(placeholderAppearances).filter(name => name !== id)) assert.equal(all(inspect(drawBands(colors), other).assertions), false, `${id} must not pass as ${other}`);
    }
    assert.deepEqual(inspect(drawBands(['red', 'blue']), 'redBlue').measurement.contentBounds, { x: cellX(0), y: bandY, width: 16, height: 16 });
});

test('a distorted, misaligned, mixed or shifted tile fails, and antialiased edges do not change the named color', () => {
    assert.equal(all(inspect(drawBands(['red', 'blue'], { height: 12 }), 'redBlue').assertions), false, 'a squashed band is not the undistorted fit');
    assert.equal(all(inspect(drawBands(['red', 'blue'], { height: 18 }), 'redBlue').assertions), false, 'a band taller than the fit is distorted');
    const staggered = drawBands(['red', 'absent']);
    const second = drawBands(['absent', 'blue'], { top: 2 });
    for (let y = 0; y < staggered.height; y++) for (let x = cellX(1); x < cellX(1) + 8; x++) staggered.pixels.set(second.pixels.subarray((y * second.width + x) * 4, (y * second.width + x + 1) * 4), (y * staggered.width + x) * 4);
    const staggeredResult = inspect(staggered, 'redBlue');
    assert.equal(all(staggeredResult.assertions), false, 'two painted cells must agree on where the band sits'); assert.equal(staggeredResult.classification.geometryOk, false);
    assert.equal(all(inspect(drawBands([[10, 200, 10, 255], 'blue']), 'redBlue').assertions), false, 'an unknown color fails');
    assert.equal(inspect(drawBands([[10, 200, 10, 255], 'blue']), 'redBlue').classification.kind, 'mixed');
    assert.ok(all(inspect(drawBands(['red', 'blue'], { edge: [200, 40, 40, 255] }), 'redBlue').assertions), 'a one-pixel antialiased border is not the interior');
    assert.equal(all(inspect(drawBands(['blue', 'red']), 'redBlue').assertions), false, 'swapped tiles are not the expected order');
});

test('changed pixels outside the target or a dirty baseline fail the controls, and malformed input is refused', () => {
    const bleeding = drawBands(['red', 'blue']); bleeding.pixels.set([255, 255, 255, 255], 0);
    assert.equal(inspect(bleeding, 'redBlue').controls[1].passed, false);
    const dirty = blank(); dirty.pixels.set([255, 0, 0, 255], (bandY * dirty.width + cellX(0)) * 4);
    assert.equal(inspectPlaceholderPixels({ baseline: dirty, observed: drawBands(['red', 'blue']), grid, anchor, cells: placeholderCellEnvelope, appearance: 'redBlue' }).controls[0].passed, false);
    assert.throws(() => inspect(drawBands(['red', 'blue']), 'greenGreen')); assert.throws(() => inspect(drawBands(['red', 'blue']), '__proto__'));
    assert.throws(() => inspectPlaceholderPixels({ baseline: blank(), observed: drawBands(['red', 'blue']), grid, anchor, cells: { cols: 4, rows: 1 }, appearance: 'redBlue' }));
    assert.throws(() => inspectPlaceholderPixels({ baseline: blank(), observed: { ...blank(), width: 119 }, grid, anchor, cells: placeholderCellEnvelope, appearance: 'redBlue' }));
});

test('a faithful placeholder run grades every stage passed, in a persistent tab and in an ordinary one', () => {
    for (const direct of [false, true]) {
        withPlaceholders({ direct }, ({ build, frames, actions }) => {
            const report = build();
            assert.equal(report.suiteId, 'ntilde-owned-kitty-placeholders-v1'); assert.equal(report.observations.length, 19);
            assert.ok(report.observations.every(item => item.result === 'passed'), JSON.stringify(report.observations.filter(item => item.result !== 'passed').map(item => [item.featureId, item.inconclusiveReason])));
            assert.deepEqual(report.caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
            const build2 = report.observations.find(item => item.featureId === tiles + '.build');
            assert.deepEqual(build2.reply.statuses, ['OK', 'OK']); assert.equal(build2.classification.kind, 'background');
            assert.deepEqual(report.observations.find(item => item.featureId === mapping + '.swapped').classification.tiles, ['blue', 'red']);
            assert.match(report.scope, /one uniform color per cell/);
            assert.doesNotMatch(report.scope, /in a persistent tab|in an ordinary tab/, 'the carrier report names no kind of tab');
            assert.equal(report.inputs.length, 1); assert.equal(frames.length, 25);
            if (direct) assert.ok(frames.every(frame => frame.ownership.daemon === undefined) && actions.every(action => action.ownership.daemon === undefined));
        });
    }
});

test('an App that paints nothing for placeholders fails the positive stages and leaves the negative ones passing only as controls', () => {
    withPlaceholders({ paint: (entry, shown) => (entry.kind === 'read' ? 'absent' : shown) }, ({ build }) => {
        const value = results(build());
        assert.equal(value[tiles + '.show'], 'failed'); assert.equal(value[tiles + '.erase'], 'inconclusive', 'the erase stage needs the show stage that proves tiles existed');
        assert.equal(value[mapping + '.swapped'], 'failed'); assert.equal(value[unbound + '.valid'], 'failed');
        assert.equal(value[unbound + '.deleted'], 'inconclusive', 'a deletion cannot be credited when the valid cells never painted');
    });
});

test('an App that ignores the marks, the placement or the image id fails the stage that depends on it', () => {
    withPlaceholders({ paint: paintAs(mapping, 'swapped', 'redBlue') }, ({ build }) => assert.equal(results(build())[mapping + '.swapped'], 'failed'));      // marks ignored: left-to-right order
    withPlaceholders({ paint: paintAs(mapping, 'single', 'redBlue') }, ({ build }) => assert.equal(results(build())[mapping + '.single'], 'failed'));         // a lone cell painted both tiles
    withPlaceholders({ paint: paintAs(mapping, 'inherit', 'absent') }, ({ build }) => assert.equal(results(build())[mapping + '.inherit'], 'failed'));        // no inheritance
    for (const stageId of ['wrong-image', 'stale-placement', 'outside-grid']) {
        withPlaceholders({ paint: paintAs(unbound, stageId, 'redBlue') }, ({ build }) => {                                                                    // cells that must paint nothing painted
            const value = results(build());
            assert.equal(value[unbound + '.' + stageId], 'failed'); assert.equal(value[unbound + '.valid'], 'inconclusive', 'the positive control needs its previous witness');
        });
    }
    withPlaceholders({ paint: paintAs(unbound, 'deleted', 'redBlue') }, ({ build }) => assert.equal(results(build())[unbound + '.deleted'], 'failed'));      // d=i did not stop the cells painting
    withPlaceholders({ paint: paintAs(tiles, 'erase', 'redBlue') }, ({ build }) => assert.equal(results(build())[tiles + '.erase'], 'failed'));               // erased cells still painted
    withPlaceholders({ paint: paintAs(tiles, 'build', 'redBlue') }, ({ build }) => assert.equal(results(build())[tiles + '.build'], 'failed'));              // the virtual placement alone drew
});

test('a refused virtual placement or transmission fails its stage on the reply, never on pixels alone', () => {
    const reply = (entry, raw) => (entry.stageId === 'build' ? raw.replace(/;OK/g, ';ENOTSUP:Graphics action not supported') : raw);
    withPlaceholders({ reply }, ({ build }) => {
        const report = build(), value = results(report);
        assert.equal(value[tiles + '.build'], 'failed'); assert.equal(value[tiles + '.show'], 'inconclusive');
        assert.deepEqual(report.observations.find(item => item.featureId === tiles + '.build').reply.passed, false);
        assert.deepEqual(report.caseResults.map(item => item.result), ['failed', 'failed', 'failed']);
    });
});
