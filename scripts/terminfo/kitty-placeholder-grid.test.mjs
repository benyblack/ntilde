import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { syntheticAnimationJournal } from './fixtures/kitty-animation-journal.mjs';
import { buildAnimationReport, animationInputs, animationSuites, animationVersionOfPolicy, ownedAnimationPolicies } from './kitty-animation.mjs';
import { animationCasesByVersion, animationCommand, animationExpectedReplies, animationSuiteIds, placeholderGridEnvelope, placeholderGridVersion, placeholderGridStageCells } from './kitty-animation-cases.mjs';
import { placeholderGridAppearances, placeholderQuadrantColors, placeholderQuadrantOf, placeholderQuadrantsInput, placeholderQuadrantInputPin } from './kitty-animation-inputs.mjs';
import { inspectPlaceholderGridPixels } from './kitty-animation-oracle.mjs';
import { inputRunId } from './fixtures/input-journal.mjs';
import { snapshotHash } from './agent-observer.mjs';

const version = placeholderGridVersion;
const [rows, scroll] = ['rows', 'scroll'].map(name => 'kitty.placeholders.' + name);
function withGrid(options, action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-kpg-'));
    try {
        const journal = syntheticAnimationJournal(output, { version, ...options });
        action({ output, ...journal, build: (frames = journal.frames, actions = journal.actions, seal = journal.seal) => buildAnimationReport(frames, actions, seal, inputRunId, output, [], version) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
const results = report => Object.fromEntries(report.observations.map(item => [item.featureId, item.result]));
const paintAs = (caseId, stageId, appearance) => (entry, shown) => (entry.caseId === caseId && entry.stageId === stageId ? appearance : shown);

test('the quadrant input is a pinned 16x16 RGBA image whose four 8x8 quadrants are the four named colors', () => {
    const input = placeholderQuadrantsInput();
    assert.equal(input.bytes.length, 16 * 16 * 4); assert.equal(input.sha256, placeholderQuadrantInputPin);
    const colorAt = (x, y) => [...input.bytes.subarray((y * 16 + x) * 4, (y * 16 + x + 1) * 4)];
    for (const [x, y, letter] of [[0, 0, 'r'], [15, 0, 'g'], [0, 15, 'b'], [15, 15, 'y'], [7, 7, 'r'], [8, 7, 'g'], [7, 8, 'b'], [8, 8, 'y']]) assert.deepEqual(colorAt(x, y), placeholderQuadrantColors[letter], `${x},${y}`);
    assert.equal(new Set(Object.values(placeholderQuadrantColors).map(String)).size, 4);
    input.bytes.fill(0); assert.deepEqual([...placeholderQuadrantsInput().bytes.subarray(0, 4)], placeholderQuadrantColors.r);
    assert.deepEqual(animationInputs(version).map(item => item.file), ['animation-inputs/quadrants.rgba']);
});

test('the grid suite plans two static cases on a 4 x 2 grid with no relaunch or elapsed time', () => {
    assert.deepEqual(animationCasesByVersion[version].map(item => item.id), [rows, scroll]);
    assert.equal(animationSuiteIds[version], 'ntilde-owned-kitty-placeholders-grid-v1');
    assert.equal(animationVersionOfPolicy(ownedAnimationPolicies[version]), version);
    assert.equal(animationSuites[version].plan.length, 1 + 14 + 2 + 1); assert.ok((animationSuites[version].plan.length + 1) * 2 <= 64);
    assert.deepEqual(placeholderGridEnvelope, { cols: 4, rows: 2 });
    for (const item of animationCasesByVersion[version]) {
        assert.equal(item.stages[0].id, 'blank'); assert.equal(item.stages.at(-1).id, 'clear'); assert.equal(item.stages[1].id, 'build');
        for (const stage of item.stages) {
            assert.equal(stage.clock, 'static'); assert.ok(!stage.relaunch);
            assert.ok(Object.hasOwn(placeholderGridAppearances, stage.appearance), stage.appearance);
        }
    }
    assert.deepEqual(animationCasesByVersion[version].flatMap(item => item.stages).filter(stage => stage.anchorShift !== undefined).map(stage => [stage.id, stage.anchorShift]), [['scrolled', -1], ['insert-line', 1]]);
});

test('commands are the protocol\'s own bytes: marks 0 to 3 written out, each stage starting from an erased screen', () => {
    const id = 0x0102ab, color = '\x1b[38;2;1;2;171m\x1b[58;5;1m', end = '\x1b[39m\x1b[59m', pl = '\u{10EEEE}', m = ['̅', '̍', '̎', '̐'];
    const cell = (row, column) => pl + m[row] + m[column], bare = pl;
    const command = (caseId, stageId) => animationCommand(caseId, stageId, { imageId: id }, version);
    const run = (screenRow, ...cells) => `\x1b[${screenRow};4H` + color + cells.join('') + end;
    const upper = [0, 1, 2, 3].map(column => cell(0, column)), lower = [0, 1, 2, 3].map(column => cell(1, column));
    const build = command(rows, 'build').split('\x1b\\').filter(Boolean);
    assert.equal(build.length, 2); assert.match(build[0], new RegExp(`^\\x1b_Ga=t,f=32,s=16,v=16,t=d,i=${id};`)); assert.equal(build[1], `\x1b_Ga=p,U=1,i=${id},p=1,c=4,r=2`);
    assert.equal(Buffer.from(build[0].split(';')[1], 'base64').equals(placeholderQuadrantsInput().bytes), true);
    assert.equal(command(rows, 'grid'), '\x1b[2J' + run(5, ...upper) + run(6, ...lower));
    assert.equal(command(rows, 'rows-swapped'), '\x1b[2J' + run(5, ...lower) + run(6, ...upper));
    assert.equal(command(rows, 'lower-tiles-on-top'), '\x1b[2J' + run(5, ...lower));
    assert.equal(command(rows, 'inherit-rows'), '\x1b[2J' + run(5, cell(0, 0), bare, bare, bare) + run(6, cell(1, 0), bare, bare, bare));
    assert.equal(command(scroll, 'shown'), command(rows, 'grid'));
    assert.equal(command(scroll, 'scrolled'), command(rows, 'grid') + '\x1b[S');
    assert.equal(command(scroll, 'erase-lower'), command(rows, 'grid') + '\x1b[6;1H\x1b[2K');
    assert.equal(command(scroll, 'insert-line'), command(rows, 'grid') + '\x1b[5;1H\x1b[L');
    assert.equal(command(rows, 'clear'), `\x1b_Ga=d,d=I,i=${id}\x1b\\\x1b[2J\x1b[H`);
    assert.equal(command(rows, 'blank'), '');
    for (const item of animationCasesByVersion[version]) for (const stage of item.stages) assert.doesNotMatch(command(item.id, stage.id), /\x1b\[0?m|\x1b\[48/);
    assert.throws(() => animationCommand(rows, 'grid', { imageId: 0x1000000 }, version)); assert.throws(() => animationCommand(rows, 'nonexistent', { imageId: id }, version));
    const replies = (caseId, stageId) => animationExpectedReplies(animationCasesByVersion[version].find(item => item.id === caseId).stages.find(stage => stage.id === stageId), id);
    assert.deepEqual(replies(rows, 'build'), [`\x1b_Gi=${id};OK\x1b\\`, `\x1b_Gi=${id},p=1;OK\x1b\\`]); assert.deepEqual(replies(rows, 'grid'), []);
});

test('the cells each grid stage leaves on the screen are these literal rows and columns', () => {
    const where = (stageId) => placeholderGridStageCells(stageId, 7).map(cell => `${cell.row}:${cell.column}`).join(' ');
    const both = '4:3 4:4 4:5 4:6 5:3 5:4 5:5 5:6';
    assert.equal(where('grid'), both); assert.equal(where('rows-swapped'), both); assert.equal(where('inherit-rows'), both); assert.equal(where('shown'), both);
    assert.equal(where('lower-tiles-on-top'), '4:3 4:4 4:5 4:6');
    assert.equal(where('scrolled'), '3:3 3:4 3:5 3:6 4:3 4:4 4:5 4:6');                  // scrolled up one line
    assert.equal(where('erase-lower'), '4:3 4:4 4:5 4:6');                                 // the lower text row erased
    assert.equal(where('insert-line'), '5:3 5:4 5:5 5:6 6:3 6:4 6:5 6:6');                 // a line inserted above the grid
    assert.equal(where('build'), ''); assert.equal(where('clear'), '');
});

// Pixels: a 4 x 2 grid of 9x19 cells (a typical desktop font): side 36, slack 0 along x and 2 along y, half 18.
const grid = { originXPx: 2, originYPx: 4, cellWidthPx: 9, cellHeightPx: 19, renderWidthPx: 200, renderHeightPx: 200 };
const anchor = { col: 3, row: 4 };
const blank = () => {
    const value = { width: 200, height: 200, pixels: Buffer.alloc(200 * 200 * 4) };
    for (let i = 0; i < value.pixels.length; i += 4) value.pixels.set([16, 16, 16, 255], i);
    return value;
};
const gridX = 2 + 3 * 9, gridY = 4 + 4 * 19;
/**
 * Paints what the App would for a pattern: each cell shows the tile of the 36x36 square image its letter names (tile row = the letter's quadrant row, tile column = the cell's own), the image sitting `offsetY` rows
 * from the grid's top; cells the pattern leaves absent stay blank. `tint` recolors one pixel of the image at (10,10), `hole` puts a stray gray pixel at grid-relative (x, y).
 */
function drawImage(pattern, { offsetY = 0, tint = null, hole = null } = {}) {
    const value = blank(), side = 36, half = 18;
    pattern.split('/').forEach((line, row) => [...line].forEach((letter, column) => {
        if (letter === '.') return;
        const tileRow = placeholderQuadrantOf[letter][0];
        for (let ly = 0; ly < 19; ly++) for (let lx = 0; lx < 9; lx++) {
            const gx = column * 9 + lx, gy = tileRow * 19 + ly - offsetY;
            if (gx >= side || gy < 0 || gy >= side) continue;
            const color = tint && gx === 10 && gy === 10 ? tint : placeholderQuadrantColors[[['r', 'g'], ['b', 'y']][gy < half ? 0 : 1][gx < half ? 0 : 1]];
            value.pixels.set(color, ((gridY + row * 19 + ly) * value.width + gridX + column * 9 + lx) * 4);
        }
    }));
    if (hole) value.pixels.set([200, 200, 200, 255], ((gridY + hole[1]) * value.width + gridX + hole[0]) * 4);
    return value;
}
const inspect = (observed, appearance, at = anchor) => inspectPlaceholderGridPixels({ baseline: blank(), observed, grid, anchor: at, cells: placeholderGridEnvelope, appearance });
const all = items => items.every(item => item.passed);

test('each grid appearance passes for the cells it names, wherever the image sits in the grid, and for no other pattern', () => {
    for (const [id, pattern] of Object.entries(placeholderGridAppearances)) {
        for (const offsetY of [0, 1, 2]) {
            const result = inspect(drawImage(pattern, { offsetY }), id);
            assert.ok(all(result.controls) && all(result.assertions), `${id} at ${offsetY}`);
            assert.equal(result.classification.cells, pattern);
            assert.equal(result.classification.kind, id === 'absent' ? 'background' : 'tiles');
        }
        for (const other of Object.keys(placeholderGridAppearances).filter(name => name !== id)) {
            assert.equal(all(inspect(drawImage(pattern), other).assertions), false, `${id} must not pass as ${other}`);
        }
    }
});

test('a wrong color, a cell that should be blank, a stray pixel and a blank cell that should paint are all caught', () => {
    // A stray pixel on the edge of a quadrant (the last column of the upper left one, which the sample keeps clear of) is not read; one inside the sampled interior is.
    const edge = drawImage('rrgg/bbyy'); edge.pixels.set([10, 200, 10, 255], ((gridY + 10) * edge.width + gridX + 17) * 4);
    assert.equal(all(inspect(edge, 'full').assertions), true, 'a pixel outside the sampled interior of its quadrant is not read');
    assert.equal(all(inspect(drawImage('rrgg/bbyy', { tint: [10, 200, 10, 255] }), 'full').assertions), false, 'a pixel inside it is');
    const wrongRow = drawImage('rrgg/bbyy'); // the rows of the image swapped under the same pattern
    assert.equal(all(inspect(wrongRow, 'rowsSwapped').assertions), false);
    const spill = drawImage('rrgg/....'), spilled = inspect(spill, 'full'); // the lower row left blank where tiles were expected
    assert.equal(all(spilled.assertions), false); assert.deepEqual(spilled.classification.cells.split('/')[1], '????');
    const stray = drawImage('rrgg/....', { hole: [3, 25] }); // one stray pixel in a cell that must stay blank
    assert.equal(all(inspect(stray, 'upperRowOnly').assertions), false);
    assert.equal(inspect(stray, 'upperRowOnly').classification.cells, 'rrgg/?...');
});

test('a tile cannot pass by painting only the region that is sampled', () => {
    // Only the sampled rectangles painted (the retained 9x19 geometry's: x 1..7, y 3..16 above, 2..15 below in cell terms): every sampled pixel is right, the rest of each tile is still the background.
    const sparse = blank();
    for (const [row, column, letter] of [[0, 0, 'r'], [0, 1, 'r'], [0, 2, 'g'], [0, 3, 'g'], [1, 0, 'b'], [1, 1, 'b'], [1, 2, 'y'], [1, 3, 'y']]) {
        for (let y = row === 0 ? 3 : 2; y < (row === 0 ? 17 : 16); y++) for (let x = 1; x < 8; x++) {
            sparse.pixels.set(placeholderQuadrantColors[letter], ((gridY + row * 19 + y) * sparse.width + gridX + column * 9 + x) * 4);
        }
    }
    const result = inspect(sparse, 'full');
    assert.equal(all(result.assertions), false); assert.equal(result.classification.cells, '????/????');
    // A fully painted tile at the same geometry still passes.
    assert.ok(all(inspect(drawImage('rrgg/bbyy'), 'full').assertions));
});

test('the controls catch a dirty baseline or changes outside the target, and the reading follows an anchor shift', () => {
    const bleeding = drawImage('rrgg/bbyy'); bleeding.pixels.set([255, 255, 255, 255], 0);
    assert.equal(inspect(bleeding, 'full').controls[1].passed, false);
    const dirty = blank(); dirty.pixels.set([255, 0, 0, 255], (gridY * dirty.width + gridX) * 4);
    assert.equal(inspectPlaceholderGridPixels({ baseline: dirty, observed: drawImage('rrgg/bbyy'), grid, anchor, cells: placeholderGridEnvelope, appearance: 'full' }).controls[0].passed, false);
    // The same picture read one row up or down is what the shifted stages read: it passes only at the right anchor row.
    const shifted = blank(); const image = drawImage('rrgg/bbyy');
    for (let y = 0; y < 38; y++) for (let x = 0; x < 36; x++) shifted.pixels.set(image.pixels.subarray(((gridY + y) * 200 + gridX + x) * 4, ((gridY + y) * 200 + gridX + x + 1) * 4), ((gridY - 19 + y) * 200 + gridX + x) * 4);
    assert.ok(all(inspect(shifted, 'full', { col: 3, row: 3 }).assertions) && all(inspect(shifted, 'full', { col: 3, row: 3 }).controls));
    assert.equal(all(inspect(shifted, 'full').assertions), false);
    assert.throws(() => inspect(drawImage('rrgg/bbyy'), 'nope')); assert.throws(() => inspect(drawImage('rrgg/bbyy'), '__proto__'));
    assert.throws(() => inspectPlaceholderGridPixels({ baseline: blank(), observed: drawImage('rrgg/bbyy'), grid, anchor, cells: { cols: 2, rows: 2 }, appearance: 'full' }));
});

test('a faithful grid run grades every stage passed, in a persistent tab and in an ordinary one', () => {
    for (const direct of [false, true]) {
        withGrid({ direct }, ({ build, frames, actions }) => {
            const report = build();
            assert.equal(report.suiteId, 'ntilde-owned-kitty-placeholders-grid-v1'); assert.equal(report.observations.length, 14);
            assert.ok(report.observations.every(item => item.result === 'passed'), JSON.stringify(report.observations.filter(item => item.result !== 'passed').map(item => [item.featureId, item.inconclusiveReason])));
            assert.deepEqual(report.caseResults.map(item => item.result), ['passed', 'passed']);
            assert.deepEqual(report.observations.find(item => item.featureId === rows + '.rows-swapped').classification.cells, 'bbyy/rrgg');
            assert.deepEqual(report.observations.find(item => item.featureId === scroll + '.scrolled').classification.cells, 'rrgg/bbyy');
            assert.match(report.scope, /one exact color per cell/); assert.doesNotMatch(report.scope, /in a persistent tab|in an ordinary tab/);
            assert.equal(report.inputs.length, 1); assert.equal(frames.length, 19);
            if (direct) assert.ok(frames.every(frame => frame.ownership.daemon === undefined) && actions.every(action => action.ownership.daemon === undefined));
        });
    }
});

test('an App that ignores the row marks, the inheritance, or the text the tiles ride with fails the stage that shows it', () => {
    withGrid({ paint: (entry, shown) => (entry.kind === 'read' ? 'absent' : shown) }, ({ build }) => {
        const value = results(build());
        assert.equal(value[rows + '.grid'], 'failed'); assert.equal(value[rows + '.rows-swapped'], 'inconclusive'); assert.equal(value[scroll + '.scrolled'], 'inconclusive');
    });
    withGrid({ paint: paintAs(rows, 'rows-swapped', 'full') }, ({ build }) => assert.equal(results(build())[rows + '.rows-swapped'], 'failed'));              // the row marks ignored
    withGrid({ paint: paintAs(rows, 'inherit-rows', 'upperRowOnly') }, ({ build }) => assert.equal(results(build())[rows + '.inherit-rows'], 'failed'));      // a missing row defaulted to 0 would paint the upper tiles twice, here: only one row
    withGrid({ paint: paintAs(rows, 'lower-tiles-on-top', 'upperRowOnly') }, ({ build }) => assert.equal(results(build())[rows + '.lower-tiles-on-top'], 'failed'));
    withGrid({ paint: paintAs(scroll, 'scrolled', 'absent') }, ({ build }) => assert.equal(results(build())[scroll + '.scrolled'], 'failed'));                // the tiles did not follow the scroll
    withGrid({ paint: paintAs(scroll, 'erase-lower', 'full') }, ({ build }) => assert.equal(results(build())[scroll + '.erase-lower'], 'failed'));            // the erased row still painted
    withGrid({ paint: paintAs(scroll, 'insert-line', 'absent') }, ({ build }) => assert.equal(results(build())[scroll + '.insert-line'], 'failed'));          // the tiles did not follow the inserted line
});

test('the cell grid must hold exactly the cells a grid stage left, on the rows it left them', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-kpg-screen-'));
    try {
        const journal = syntheticAnimationJournal(output, { version });
        const rebuild = frames => buildAnimationReport(frames, journal.actions, journal.seal, inputRunId, output, [], version);
        assert.doesNotThrow(() => rebuild(journal.frames));
        const at = (caseId, stageId) => journal.frames.findIndex(frame => frame.caseId === caseId && frame.stageId === stageId);
        const tamper = (caseId, stageId, change) => { const frames = structuredClone(journal.frames); change(frames[at(caseId, stageId)]); return frames; };
        assert.throws(() => rebuild(tamper(scroll, 'scrolled', frame => { frame.screen.lines[4] = ''; })));                        // a cell row missing
        assert.throws(() => rebuild(tamper(scroll, 'scrolled', frame => { frame.screen.lines[5] = frame.screen.lines[4]; })));     // the cells where they were before the scroll
        assert.throws(() => rebuild(tamper(rows, 'grid', frame => { frame.screen.cellDetails[1].count = 3; })));                  // one cell short in a run
        assert.throws(() => rebuild(tamper(scroll, 'insert-line', frame => { frame.screen.lines[4] = frame.screen.lines[5]; })));
        // A terminal that does not erase with the background (the scrolled-in or inserted line holds the default cell) is as valid: that screen is accepted too, and no other change to those lines is.
        const exposed = (caseId, stageId, row) => frame => { // the exposed row holds the default cell (default foreground and background)
            const details = []; let at = 0;
            for (const run of frame.screen.cellDetails) {
                const from = at, to = at + run.count; at = to;
                const start = row * frame.screen.cols, end = start + frame.screen.cols;
                if (to <= start || from >= end) { details.push(run); continue; }
                if (from < start) details.push({ ...run, count: start - from });
                details.push({ ...run, count: Math.min(to, end) - Math.max(from, start), flags: run.flags | 512, backgroundArgb: null });
                if (to > end) details.push({ ...run, count: to - end });
            }
            frame.screen.cellDetails = details; frame.afterScreen = structuredClone(frame.screen);
        };
        for (const [caseId, stageId, row] of [[scroll, 'scrolled', 9], [scroll, 'insert-line', 4]]) {
            const frames = tamper(caseId, stageId, exposed(caseId, stageId, row));
            frames[at(caseId, stageId)].sha256 = snapshotHash(frames[at(caseId, stageId)].screen); frames[at(caseId, stageId)].afterSha256 = frames[at(caseId, stageId)].sha256;
            assert.doesNotThrow(() => rebuild(frames), `${stageId} with a default-filled new line`);
        }
        const wrongRow = tamper(scroll, 'scrolled', exposed(scroll, 'scrolled', 7));
        wrongRow[at(scroll, 'scrolled')].sha256 = snapshotHash(wrongRow[at(scroll, 'scrolled')].screen); wrongRow[at(scroll, 'scrolled')].afterSha256 = wrongRow[at(scroll, 'scrolled')].sha256;
        assert.throws(() => rebuild(wrongRow));                                                                                      // a default-filled line anywhere else is still refused
        const text = (caseId, stageId) => journal.frames[at(caseId, stageId)].screen.lines;
        assert.notEqual(text(scroll, 'scrolled')[3], ''); assert.equal(text(scroll, 'scrolled')[5], '');
        assert.equal(text(scroll, 'insert-line')[4], ''); assert.notEqual(text(scroll, 'insert-line')[6], '');
    } finally { rmSync(output, { recursive: true, force: true }); }
});
