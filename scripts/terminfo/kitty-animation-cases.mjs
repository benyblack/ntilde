// Planned protocol variants and the independent playback timeline only; importing performs no terminal I/O.
import { animationFrameInput, animationPngInput, placeholderHalvesInput, placeholderQuadrantsInput } from './kitty-animation-inputs.mjs';

/**
 * Suite versions: 1 is the three-case suite that retained runs still carry (#988); 2 adds PNG and root-frame composition (#1015) and is what full collections produce;
 * 3 (#1030) is the restore suite: the same animation state is read again after the App is terminated with SIGTERM and started again on the same persistent session, so it only runs in the
 * persistent-restore collection (a direct tab has nothing to reattach to).
 * 4 (#1027) is the frame-composition suite (`a=c`): full-frame compositions and refused ones, only in the persistent-compose collection (persistent image policy 4); full collections stay on 2.
 * 5 (#989) is the Unicode placeholder suite (`U=1` virtual placements and U+10EEEE cells): one image shown through placeholder cells, in the placeholder collections only (an ordinary tab and a persistent tab, image policy 5).
 * 6 (#989) is the placeholder grid suite: what suite 5 left unmeasured (a grid of more than one row, the inheritance of a nonzero row, and cells that scroll, are erased and are inserted with their text), in its own two collections.
 */
export const animationSuiteIds = Object.freeze({ 1: 'ntilde-owned-kitty-animation-v1', 2: 'ntilde-owned-kitty-animation-v2', 3: 'ntilde-owned-kitty-animation-restore-v1', 4: 'ntilde-owned-kitty-animation-compose-v1', 5: 'ntilde-owned-kitty-placeholders-v1', 6: 'ntilde-owned-kitty-placeholders-grid-v1' });
/** The one suite version whose stages relaunch the App (#1030); every other version runs without one. */
export const restoreAnimationVersion = 3;
export const currentAnimationVersion = 2;
export const animationSuiteId = animationSuiteIds[currentAnimationVersion];
export const animationAnchor = Object.freeze({ col: 3, row: 4 });
export const animationCellEnvelope = Object.freeze({ cols: 4, rows: 2 });
/** Every frame, root included, is shown for this many milliseconds (z=, and a=a r=1,z= for the root). */
export const animationHoldMs = 5000;
export const animationHoldCycle = Object.freeze(['root', 'second', 'third']);
/** Largest protocol-processing lag a read-back must tolerate when it brackets the moment a command took effect. */
export const animationAppliedMarginMs = 250;

// appearance: 'absent', a frame whose solid color must fill the placement, or a composed frame (overRoot, overEdit) whose color the protocol's
// own blend formula gives (see kitty-animation-inputs.mjs).
// expect: the ordered correlated replies the stage's command produces ('placed' is the a=T reply, which echoes its placement id; a=d success is silent).
// relaunchAtMs: where in the running timeline the relaunch begins (the App is back about 2-3 s later, inside the second frame's window, so a playhead the new App restarted would
// show different frames from a restored one at the later reads).
// relaunch: the parent terminates the App (SIGTERM) and starts it again on the same private root before this stage is admitted; the stage reads what the reattached pane shows.
// appearance 'timeline' (a relaunch stage whose frame depends on when the App came back): the frame the independent timeline gives for the capture span.
// clock: 'static' (no running animation expected to move), 'running' (elapsed time since a=a s=3 selects the frame),
// 'frozen' (stopped on the frame that was showing; it must outlast a hold the model says would have advanced it).
// targetMs: where in the running timeline the read aims, centered in a hold window so capture latency cannot straddle it.
const stage = (id, appearance, extra = {}) => Object.freeze({ id, appearance, clock: 'static', expect: Object.freeze([]), ...extra });
const replies = (...kinds) => Object.freeze(kinds);
const build = stage('build', 'root', { reply: 'build', expect: replies('placed', 'ok', 'ok', 'ok') });
const clear = stage('clear', 'absent', { reply: 'delete' });
const timedCases = Object.freeze([
    Object.freeze({ id: 'kitty.animation.stopped-hold', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('hold', 'root', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.playback', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        stage('second', 'second', { clock: 'running', targetMs: 7500 }),
        stage('third', 'third', { clock: 'running', targetMs: 12500 }),
        stage('loop', 'root', { clock: 'running', targetMs: 17500 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.stop-freeze', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        stage('second', 'second', { clock: 'running', targetMs: 6200 }),
        stage('stop', 'second', { clock: 'frozen', reply: 'stop', expect: replies('ok') }),
        stage('hold', 'second', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
]);
// PNG frames and the root frame as a base and an edit target, each shown by an explicit a=a,c= so no elapsed time is involved.
const compositionCase = Object.freeze({ id: 'kitty.animation.png-and-root-frames', stages: Object.freeze([stage('blank', 'absent'),
    stage('build', 'root', { reply: 'build', expect: replies('placed', 'ok') }),
    stage('show', 'overRoot', { reply: 'show', expect: replies('ok') }),
    stage('edit', 'third', { reply: 'edit', expect: replies('ok', 'ok') }),
    stage('compose', 'overEdit', { reply: 'compose', expect: replies('ok', 'ok') }), clear]) });
// The restore cases (#1030). Each reads, after a relaunch, state that only a restored snapshot can carry: a running playhead, a stopped one, and edited and PNG-derived frames.
const reattach = (appearance, extra = {}) => stage('reattach', appearance, { relaunch: true, ...extra });
const restoreCases = Object.freeze([
    Object.freeze({ id: 'kitty.animation.restore-playing', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        reattach('timeline', { clock: 'running', relaunchAtMs: 4000 }),
        stage('third', 'third', { clock: 'running', targetMs: 12500 }),
        stage('loop', 'root', { clock: 'running', targetMs: 17500 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.restore-stopped', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        stage('second', 'second', { clock: 'running', targetMs: 6200 }),
        stage('stop', 'second', { clock: 'frozen', reply: 'stop', expect: replies('ok') }),
        reattach('second', { clock: 'frozen' }),
        stage('hold', 'second', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.restore-frames', stages: Object.freeze([stage('blank', 'absent'),
        stage('build', 'root', { reply: 'build', expect: replies('placed', 'ok') }),
        stage('show', 'overRoot', { reply: 'show', expect: replies('ok') }),
        stage('edit', 'third', { reply: 'edit', expect: replies('ok', 'ok') }),
        reattach('third'),
        // The PNG-derived frame 2 is selected again after the relaunch without uploading it: its pixels, not only its slot, came back from the snapshot.
        stage('again', 'overRoot', { reply: 'show', expect: replies('ok') }),
        stage('compose', 'overEdit', { reply: 'compose', expect: replies('ok', 'ok') }), clear]) }),
]);
// The composition cases (#1027): whole-frame `a=c` compositions only, so every observation is one uniform color the classifier can name. Rectangle offsets and sizes are pinned by unit tests, not here.
// compose-replace-blend: a half-transparent green PNG frame 4 (built on no base, so it keeps its alpha) replaces the opaque blue frame 3 with C=1 (the result keeps the source's alpha, so it shows over the terminal
// background: a blend would have given green over blue instead), then the same frame is blended by default onto the opaque red root (green over red).
// compose-refused: a missing frame (ENOENT), a rectangle wider than the image (EINVAL) and one frame onto itself with overlapping rectangles (EINVAL) change nothing: frame 2 is still green afterwards.
const composeReplaceBlendCaseId = 'kitty.animation.compose-replace-blend', composeRefusedCaseId = 'kitty.animation.compose-refused';
const composeCases = Object.freeze([
    Object.freeze({ id: composeReplaceBlendCaseId, stages: Object.freeze([stage('blank', 'absent'), build,
        stage('replace', 'overBackground', { reply: 'replace', expect: replies('ok', 'ok', 'ok') }),
        stage('blend', 'overRoot', { reply: 'blend', expect: replies('ok', 'ok') }), clear]) }),
    Object.freeze({ id: composeRefusedCaseId, stages: Object.freeze([stage('blank', 'absent'), build,
        stage('refuse', 'root', { reply: 'refuse', expect: replies('enoent', 'einval', 'einval') }),
        stage('intact', 'second', { reply: 'intact', expect: replies('ok') }), clear]) }),
]);
// The placeholder cases (#989). One 16x16 image whose left half is red and right half blue is transmitted (a=t), then given a virtual placement (a=p,U=1) of a 2 x 1 cell grid: that draws nothing. Cells printing U+10EEEE then
// show the image, each its own tile, so the pair of cells the stage reads is [left, right] (appearance ids name the two tile colors, see kitty-animation-inputs.mjs). Every command is the protocol's own: the foreground colour names
// the image (a 24-bit colour, so ids stay below 2^24 and need no third mark), the underline colour (SGR 58) names the placement, and combining marks from kitty's rowcolumn-diacritics table give the row and column.
// tiles: the virtual placement alone paints nothing, the cells then show the two halves in order, and erasing the cells removes the image again.
// cell-mapping: the marks, not the screen position, choose the tile (swapped columns swap the colors; a lone cell shows its own tile wherever it is printed), and a cell without marks inherits row and column plus one from the cell to its left.
// unbound: cells that name no image, no such placement or a tile outside the grid paint nothing; the valid cells (the positive control) then paint; deleting the image's placements (a=d,d=i) makes the very same cells paint nothing again.
export const placeholderAnimationVersion = 5;
export const placeholderCellEnvelope = Object.freeze({ cols: 2, rows: 1 });
const placeholderBuild = stage('build', 'absent', { reply: 'build', expect: replies('ok', 'placed') });
const placeholderCases = Object.freeze([
    Object.freeze({ id: 'kitty.placeholders.tiles', stages: Object.freeze([stage('blank', 'absent'), placeholderBuild,
        stage('show', 'redBlue', { reply: 'show' }), stage('erase', 'absent', { reply: 'erase' }), clear]) }),
    Object.freeze({ id: 'kitty.placeholders.cell-mapping', stages: Object.freeze([stage('blank', 'absent'), placeholderBuild,
        stage('swapped', 'blueRed', { reply: 'swapped' }), stage('single', 'absentRed', { reply: 'single' }), stage('inherit', 'redBlue', { reply: 'inherit' }), clear]) }),
    Object.freeze({ id: 'kitty.placeholders.unbound', stages: Object.freeze([stage('blank', 'absent'), placeholderBuild,
        stage('wrong-image', 'absent', { reply: 'wrong-image' }), stage('stale-placement', 'absent', { reply: 'stale-placement' }), stage('outside-grid', 'absent', { reply: 'outside-grid' }),
        stage('valid', 'redBlue', { reply: 'valid' }), stage('deleted', 'absent', { reply: 'delete' }), clear]) }),
]);
// The placeholder grid cases (#989). The first placeholder suite read one row of two cells; this one reads a 4 x 2 grid (which with a 16x16 image is about as square as a cell font allows), over an image in four colored quadrants,
// so every one of the eight cells has an interior color that names the tile it shows, row and column. A stage is a row of cells per screen row; the cells' marks are written out from the protocol (see placeholderGridPrints).
// rows: the whole grid, the row marks (a swapped pair of rows, and the lower tiles on the top screen row alone), and a bare placeholder inheriting the row of a nonzero row and the next column from its left neighbour.
// scroll: the tiles move, shrink and grow with the text rows that carry them (scroll up one line, erase the lower text row, insert a line above the grid): `anchorShift` is how many rows the reading moves from the anchor.
export const placeholderGridVersion = 6;
export const placeholderGridEnvelope = Object.freeze({ cols: 4, rows: 2 });
const gridBuild = stage('build', 'absent', { reply: 'build', expect: replies('ok', 'placed') });
const placeholderGridCases = Object.freeze([
    Object.freeze({ id: 'kitty.placeholders.rows', stages: Object.freeze([stage('blank', 'absent'), gridBuild,
        stage('grid', 'full', { reply: 'grid' }), stage('rows-swapped', 'rowsSwapped', { reply: 'rows-swapped' }), stage('lower-tiles-on-top', 'lowerTilesOnTopRow', { reply: 'lower-tiles-on-top' }),
        stage('inherit-rows', 'full', { reply: 'inherit-rows' }), clear]) }),
    Object.freeze({ id: 'kitty.placeholders.scroll', stages: Object.freeze([stage('blank', 'absent'), gridBuild,
        stage('shown', 'full', { reply: 'shown' }), stage('scrolled', 'full', { reply: 'scrolled', anchorShift: -1 }), stage('erase-lower', 'upperRowOnly', { reply: 'erase-lower' }),
        stage('insert-line', 'full', { reply: 'insert-line', anchorShift: 1 }), clear]) }),
]);
export const animationCasesByVersion = Object.freeze({ 1: timedCases, 2: Object.freeze([...timedCases, compositionCase]), 3: restoreCases, 4: composeCases, 5: placeholderCases, 6: placeholderGridCases });
export const animationCases = animationCasesByVersion[currentAnimationVersion];

const uint = value => Number.isSafeInteger(value) && value >= 1 && value <= 0xffffffff;
const apc = params => '\x1b_G' + params + '\x1b\\';
const clearScreen = '\x1b[2J\x1b[H';
const frameData = frameId => animationFrameInput(frameId).bytes.toString('base64');
const pngData = pngId => animationPngInput(pngId).bytes.toString('base64');
const frameControls = 'f=32,s=16,v=16,t=d';

const compositionCaseId = 'kitty.animation.png-and-root-frames', restoreFramesCaseId = 'kitty.animation.restore-frames';
// Frame 2: the half-transparent green PNG over the root (c=1). Frame 3: the half-transparent red PNG over the root after it was edited blue (r=1).
const compositionCommands = Object.freeze({
    build: (imageId, root) => root + apc(`a=f,f=100,t=d,i=${imageId},c=1,z=${animationHoldMs};` + pngData('overRoot')),
    show: imageId => apc(`a=a,i=${imageId},c=2`),
    again: imageId => apc(`a=a,i=${imageId},c=2`),
    edit: imageId => apc(`a=f,${frameControls},i=${imageId},r=1;` + frameData('third')) + apc(`a=a,i=${imageId},c=1`),
    compose: imageId => apc(`a=f,f=100,t=d,i=${imageId},c=1,z=${animationHoldMs};` + pngData('overEdit')) + apc(`a=a,i=${imageId},c=3`),
});

const composeCommands = Object.freeze({
    replace: imageId => apc(`a=f,f=100,t=d,i=${imageId},z=${animationHoldMs};` + pngData('overRoot')) + apc(`a=c,i=${imageId},r=4,c=3,C=1`) + apc(`a=a,i=${imageId},c=3`),
    blend: imageId => apc(`a=c,i=${imageId},r=4,c=1`) + apc(`a=a,i=${imageId},c=1`),
    refuse: imageId => apc(`a=c,i=${imageId},r=9,c=2`) + apc(`a=c,i=${imageId},r=1,c=2,w=17`) + apc(`a=c,i=${imageId},r=2,c=2,w=8,X=0,x=4`),
    intact: imageId => apc(`a=a,i=${imageId},c=2`),
});

// Placeholder commands (#989). These literals are the protocol's, written out here and not taken from the App: U+10EEEE, and kitty's row/column diacritics numbers 0 to 3 (U+0305, U+030D, U+030E, U+0310).
const placeholderChar = '\u{10EEEE}', placeholderMarks = Object.freeze(['\u0305', '\u030D', '\u030E', '\u0310']);
/** The largest image id a placeholder stage may use: it is carried by a 24-bit foreground colour (no third mark), and the wrong-image stage names the next id. */
export const placeholderMaxImageId = 0xfffffe;
const placeholderCell = (row, column) => placeholderChar + (row === undefined ? '' : placeholderMarks[row] + placeholderMarks[column]);
const foreground = id => `\x1b[38;2;${(id >>> 16) & 255};${(id >>> 8) & 255};${id & 255}m`;
/** The cells in one run: the image id as the foreground colour and the placement id as the underline colour (SGR 58, palette form), then both colours reset (the cell background stays the controlled one). */
const placeholderRun = (imageId, placement, ...cells) => foreground(imageId) + `\x1b[58;5;${placement}m` + cells.join('') + '\x1b[39m\x1b[59m';
// What each printing stage writes into the anchor row: how many columns the cursor skips, which image id and placement the colours name, and the (row, column) marks of each cell ([] is a bare placeholder, inheriting from its left neighbour).
// The row is erased first, so a stage never sees the cells an earlier one printed; the `deleted` stage prints nothing and leaves the `valid` stage's cells in place.
const placeholderPrints = Object.freeze({
    show: { cells: [[0, 0], [0, 1]] },
    swapped: { cells: [[0, 1], [0, 0]] },
    single: { skip: 1, cells: [[0, 0]] },
    inherit: { cells: [[0, 0], []] },
    'wrong-image': { idOffset: 1, cells: [[0, 0], [0, 1]] },
    'stale-placement': { placement: 2, cells: [[0, 0], [0, 1]] },
    'outside-grid': { cells: [[1, 0], [1, 1]] },
    valid: { cells: [[0, 0], [0, 1]] },
});
const printCommand = (print, imageId) => '\x1b[2K' + '\x1b[C'.repeat(print.skip ?? 0)
    + placeholderRun(imageId + (print.idOffset ?? 0), print.placement ?? 1, ...print.cells.map(marks => placeholderCell(...marks)));
const placeholderCommands = Object.freeze({
    build: imageId => apc(`a=t,${frameControls},i=${imageId};` + placeholderHalvesInput().bytes.toString('base64')) + apc(`a=p,U=1,i=${imageId},p=1,c=2,r=1`),
    erase: () => '\x1b[2J',
    delete: imageId => apc(`a=d,d=i,i=${imageId}`),
    ...Object.fromEntries(Object.entries(placeholderPrints).map(([stageId, print]) => [stageId, imageId => printCommand(print, imageId)])),
});
/**
 * The placeholder cells the anchor row holds once a stage's command has run, as the cell grid reports them: where each one sits (a column of the row) and the grapheme it holds, with the image id its 24-bit foreground names.
 * Empty for a stage that leaves the row blank (a build, an erase, a clear). The grapheme text is the stage's own command text, so no expectation is read from the App.
 */
export function placeholderStageCells(stageId, imageId, anchorCol) {
    const print = placeholderPrints[stageId === 'deleted' ? 'valid' : stageId];
    if (!print) return [];
    return print.cells.map((marks, index) => ({ column: anchorCol + (print.skip ?? 0) + index, text: placeholderCell(...marks), imageId: imageId + (print.idOffset ?? 0) }));
}

// Grid stages (#989). `print` is what the stage writes, per screen row (anchor row 4 and the one below it): the (row, column) marks of each cell, [] being a bare placeholder that inherits from its left neighbour.
// `operation` runs after the printing and moves the cells; `final` is where the cells are once it has run, per screen row (the screen the cell grid must then show).
const gridMarks = (row, columns = [0, 1, 2, 3]) => columns.map(column => [row, column]);
const gridInherit = row => [[row, 0], [], [], []];
const gridFull = Object.freeze({ 4: gridMarks(0), 5: gridMarks(1) });
const placeholderGridPrints = Object.freeze({
    grid: { print: gridFull },
    'rows-swapped': { print: { 4: gridMarks(1), 5: gridMarks(0) } },
    'lower-tiles-on-top': { print: { 4: gridMarks(1) } },
    'inherit-rows': { print: { 4: gridInherit(0), 5: gridInherit(1) } },
    shown: { print: gridFull },
    scrolled: { print: gridFull, operation: '\x1b[S', final: { 3: gridFull[4], 4: gridFull[5] } },                  // SU: the screen scrolls up one line
    'erase-lower': { print: gridFull, operation: '\x1b[6;1H\x1b[2K', final: { 4: gridFull[4] } },                    // EL 2 on the lower text row
    'insert-line': { print: gridFull, operation: '\x1b[5;1H\x1b[L', final: { 5: gridFull[4], 6: gridFull[5] } },    // IL at the grid's first text row
});
const gridCommand = (spec, imageId) => '\x1b[2J' + Object.entries(spec.print).map(([row, marks]) => `\x1b[${Number(row) + 1};${animationAnchor.col + 1}H` + placeholderRun(imageId, 1, ...marks.map(item => placeholderCell(...item)))).join('') + (spec.operation ?? '');
const placeholderGridCommands = Object.freeze({
    build: imageId => apc(`a=t,${frameControls},i=${imageId};` + placeholderQuadrantsInput().bytes.toString('base64')) + apc(`a=p,U=1,i=${imageId},p=1,c=4,r=2`),
    ...Object.fromEntries(Object.entries(placeholderGridPrints).map(([stageId, spec]) => [stageId, imageId => gridCommand(spec, imageId)])),
});
/** The placeholder cells the screen holds once a grid stage has run: where each sits (screen row and column) and its grapheme, with the image id the foreground names. Empty for a stage that leaves the screen blank. */
export function placeholderGridStageCells(stageId, imageId) {
    const spec = placeholderGridPrints[stageId];
    if (!spec) return [];
    return Object.entries(spec.final ?? spec.print).flatMap(([row, marks]) => marks.map((item, index) => ({ row: Number(row), column: animationAnchor.col + index, text: placeholderCell(...item), imageId })));
}

/** Exact bounded bytes; the image id is parent-chosen per case, never guessed from a reply. */
export function animationCommand(caseId, stageId, { imageId } = {}, version = currentAnimationVersion) {
    const spec = animationCasesByVersion[version].find(item => item.id === caseId);
    const entry = spec?.stages.find(item => item.id === stageId);
    if (!entry) throw new Error('Unknown animation stage.');
    if (stageId === 'blank' || entry.relaunch || (entry.waitMs && !entry.reply)) return '';
    if (!uint(imageId)) throw new Error('Animation image id must be an explicit positive uint32.');
    if (version === placeholderGridVersion) {
        if (imageId > placeholderMaxImageId) throw new Error('A placeholder image id must fit a 24-bit foreground colour.');
        if (stageId === 'clear') return apc(`a=d,d=I,i=${imageId}`) + clearScreen;
        const command = placeholderGridCommands[stageId];
        if (!command) throw new Error('Unknown placeholder grid stage.');
        return command(imageId);
    }
    if (version === placeholderAnimationVersion) {
        if (imageId > placeholderMaxImageId) throw new Error('A placeholder image id must fit a 24-bit foreground colour with room for the next id.');
        if (stageId === 'clear') return apc(`a=d,d=I,i=${imageId}`) + clearScreen;
        const command = placeholderCommands[entry.reply === 'delete' ? 'delete' : stageId];
        if (!command) throw new Error('Unknown placeholder stage.');
        return command(imageId);
    }
    const root = apc(`a=T,${frameControls},i=${imageId},p=1,c=4,r=2,C=1;` + frameData('root'));
    const composition = [compositionCaseId, restoreFramesCaseId].includes(caseId) ? compositionCommands[stageId] : undefined;
    if (composition) return composition(imageId, root);
    if ([composeReplaceBlendCaseId, composeRefusedCaseId].includes(caseId) && composeCommands[stageId]) return composeCommands[stageId](imageId);
    if (stageId === 'build') {
        const gap = `z=${animationHoldMs}`;
        return root
            + apc(`a=f,${frameControls},i=${imageId},${gap};` + frameData('second'))
            + apc(`a=f,${frameControls},i=${imageId},${gap};` + frameData('third'))
            + apc(`a=a,i=${imageId},r=1,${gap}`);
    }
    if (stageId === 'start') return apc(`a=a,i=${imageId},s=3`);
    if (stageId === 'stop') return apc(`a=a,i=${imageId},s=1`);
    if (stageId === 'clear') return apc(`a=d,d=I,i=${imageId}`) + clearScreen;
    return '';
}

/** The ordered correlated replies one stage's command produces; a=d success is silent. The a=T reply echoes the placement id it was given (p=1). */
export function animationExpectedReplies(stage, imageId) {
    if (!uint(imageId)) throw new Error('Animation image id must be an explicit positive uint32.');
    const status = text => `\x1b_Gi=${imageId};${text}\x1b\\`, placed = `\x1b_Gi=${imageId},p=1;OK\x1b\\`;
    const replies = { ok: status('OK'), placed, enoent: status('ENOENT'), einval: status('EINVAL') };
    return stage.expect.map(kind => replies[kind]);
}
