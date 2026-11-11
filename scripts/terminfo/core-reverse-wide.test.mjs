import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { extractOwnedArchive, loadMapping, root } from '../terminfo-core-results.mjs';
import { snapshotHash, OwnedCellObserver } from './agent-observer.mjs';
import { deriveReverseWideCoreReport, readableReverseWideReport } from '../terminfo-core-reverse-wide-results.mjs';
import { loadMeasurement as loadSgrMeasurement, publication as sgrPublication } from '../terminfo-core-sgr-raster-publication.mjs';
import { tabStopsSuiteId } from './core-tab-stops.mjs';
import { deriveSgrRasterCoreReport as previousDerive } from '../terminfo-core-sgr-raster-results.mjs';
import { includesReverseWideEvidence, checkReverseWideEvidence } from './core-reverse-wide-receipt.mjs';
import { reverseWideIds, reverseWidePlan, reverseWideSteps, reverseWideSuiteId, reverseWideNonce, reverseWideSpecimens, reverseModes, cjkChar, cjkUtf8Hex,
    decset5, decrst5, restoreFeed, gradeReverseWide, buildReverseWideReport, collectReverseWide, ReverseWideObserver } from './core-reverse-wide.mjs';

const dir = mkdtempSync(join(tmpdir(), 'nt-rw-'));
extractOwnedArchive(root, loadMapping(), dir);
const baseline = JSON.parse(readFileSync(join(dir, 'cg-snapshots.jsonl'), 'utf8').split('\n')[0]).screen;
rmSync(dir, { recursive: true, force: true });
const { rows, cols } = baseline;
const barrier = '\x1b[?62;4c';

/** A deliberately separate terminal model: a cell grid with per-cell inverse, DECSCNM, CUP, SGR 7/0, ED 2, wide characters and nothing else. */
function model(feeds) {
    let grid, row, col, inverse, reverse;
    const power = () => { grid = Array.from({ length: rows }, () => Array.from({ length: cols }, () => ({ char: ' ', kind: 'narrow', inverse: false }))); row = 0; col = 0; inverse = false; reverse = false; };
    power();
    const wide = ch => /[ᄀ-ᅟ⺀-꓏가-힣豈-﫿＀-｠]/.test(ch);
    const out = [];
    for (const feed of feeds) {
        for (let i = 0; i < feed.length; i++) {
            const ch = feed[i];
            if (ch === '\x1b') {
                const next = feed[++i];
                if (next === '[') {
                    let params = '';
                    while (!/[@-~]/.test(feed[i + 1])) params += feed[++i];
                    const final = feed[++i], q = params.startsWith('?'), numbers = params.replace('?', '').split(';').map(x => (x === '' ? 0 : Number(x)));
                    if (final === 'H') { row = (numbers[0] || 1) - 1; col = (numbers[1] || 1) - 1; }
                    else if (final === 'J' && numbers[0] === 2) grid.forEach(line => line.forEach(c => Object.assign(c, { char: ' ', kind: 'narrow', inverse: false })));
                    else if (final === 'm') for (const n of numbers) { if (n === 0) inverse = false; else if (n === 7) inverse = true; }
                    else if (q && numbers[0] === 5 && (final === 'h' || final === 'l')) reverse = final === 'h';
                }
            } else if (wide(ch)) { grid[row][col] = { char: ch, kind: 'wide', inverse }; grid[row][col + 1] = { char: '', kind: 'continuation', inverse }; col += 2; }
            else { grid[row][col++] = { char: ch, kind: 'narrow', inverse }; }
        }
        const ink = [];
        grid.forEach((line, r) => line.forEach((c, k) => { if (!(c.char === ' ' && c.kind === 'narrow' && !c.inverse)) ink.push({ row: r, col: k, char: c.char, kind: c.kind, inverse: c.inverse }); }));
        out.push({ ink, cursor: [row, col], reverse });
    }
    return out;
}
test('the plan expectations equal an independent byte-level terminal model', () => {
    const steps = reverseWideSteps();
    // The model keeps one screen across the plan, like the terminal; every setup clears with ED 2 and homes the cursor.
    const observed = model(steps.map(step => step.feed.replaceAll('\x1b[?1049l', '')));
    steps.forEach((step, index) => {
        assert.deepEqual(observed[index].ink, step.ink, step.role);
        assert.deepEqual(observed[index].cursor, step.cursor, step.role);
        if (step.part === 'reverse') assert.equal(observed[index].reverse, step.stored, step.role);
    });
});

test('the exact bytes: DECSET 5 and DECRST 5 are separate writes, and the target is one write of U+4E2D encoded as UTF-8', () => {
    const feeds = reverseWidePlan().filter(x => x.feed !== undefined).map(x => x.feed);
    assert.equal(decset5, '\x1b[?5h'); assert.equal(decrst5, '\x1b[?5l');
    assert.equal(feeds.filter(feed => feed === decset5).length, 1);
    assert.equal(feeds.filter(feed => feed === cjkChar).length, 1);
    assert.equal(cjkChar.codePointAt(0), 0x4e2d); assert.equal(cjkUtf8Hex, 'e4b8ad');
    assert.equal(Buffer.from(cjkChar, 'utf8').length, 3); assert.equal(cjkChar.length, 1);
    // DECSET 5 appears nowhere else: not in any setup, not in the target, not in a control.
    assert.equal(feeds.filter(feed => feed.includes('\x1b[?5h')).length, 1);
    const order = feeds.map(feed => (feed === decset5 ? 'set' : feed === decrst5 ? 'rst' : feed === cjkChar ? 'cjk' : '')).filter(Boolean);
    assert.deepEqual(order, ['rst', 'set', 'rst', 'cjk']);
    assert.deepEqual(reverseModes, [5, 7, 25]);
    assert.ok(restoreFeed(false).includes('\x1b[?5l') && !restoreFeed(false).includes('\x1b[?5h'));
    assert.ok(restoreFeed(true).endsWith('\x1b[?5h\x1b7\x1b[?47l'));
});
test('the target sits away from the right edge so wrapping cannot mask width', () => {
    for (const step of reverseWideSteps().filter(s => s.part === 'wide')) assert.ok(step.cursor[1] <= 2 && cols - step.cursor[1] > 40);
});

// ---- Synthetic frames: complete screens built from the measured baseline shape. ----
const flagOf = c => 0x300 | (c.inverse ? 0x2 : 0) | (c.kind === 'wide' ? 0x800 : 0) | (c.kind === 'continuation' ? 0x1000 : 0);
const runsOf = (cells, make) => {
    const out = [];
    for (const cell of cells) {
        const value = make(cell), last = out.at(-1);
        if (last && last.flags === value.flags) last.count++; else out.push({ ...value, count: 1 });
    }
    return out;
};
/** A full screen: `ink` placements (row, col, char, kind, inverse), the cursor, DECSCNM, alternate-screen state and an optional per-cell tweak. */
function screenOf({ ink = [], cursor = [0, 0], reverse = false, alternate = true, tweak, cells: cellsOnly = ink.some(p => p.kind !== 'narrow') } = {}) {
    const screen = structuredClone(baseline), cells = Array.from({ length: rows * cols }, () => ({ char: ' ', kind: 'narrow', inverse: false }));
    for (const p of ink) cells[p.row * cols + p.col] = { char: p.char, kind: p.kind, inverse: p.inverse };
    tweak?.(cells);
    screen.lines = Array.from({ length: rows }, (_, r) => cells.slice(r * cols, (r + 1) * cols).filter(c => c.kind !== 'continuation').map(c => c.char).join('').trimEnd());
    screen.cellDetails = runsOf(cells, c => ({ overline: false, flags: flagOf(c), fg: -1, bg: -1, foregroundArgb: null, backgroundArgb: null, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null }));
    screen.attributeRuns = runsOf(cells, c => ({ flags: flagOf(c), fg: -1, bg: -1 }));
    const { foregroundArgb: fg, backgroundArgb: bg } = baseline.colorBinding;
    screen.colorBinding = { ...screen.colorBinding, reverseVideo: reverse, foregroundArgb: reverse ? bg : fg, backgroundArgb: reverse ? fg : bg };
    screen.resolvedColors = [{ count: rows * cols, fg: { source: 'default', index: -1, argb: screen.colorBinding.foregroundArgb }, bg: { source: 'default', index: -1, argb: screen.colorBinding.backgroundArgb } }];
    [screen.cursorRow, screen.cursorCol] = cursor;
    screen.cursorVisible = true;
    screen.bufferState = { ...screen.bufferState, alternateScreen: alternate, originMode: false, autoWrap: true, insertMode: false, pendingWrap: false, scrollTop: 0, scrollBottom: rows - 1, scrollbackLines: 0 };
    screen.history = { startLine: 0, totalLines: 0, lines: [] };
    screen.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: cols - 1 };
    // A frame holding a wide cell is a stored-cell-only frame: no attribute runs, binding or resolved colors.
    if (cellsOnly) { delete screen.attributeRuns; delete screen.colorBinding; delete screen.resolvedColors; }
    return screen;
}
const modeEntry = (mode, status) => ({ mode, barrier, reply: { raw: `\x1b[?${mode};${status}$y`, mode, status } });
const modesFor = (step, overrides = {}) => step.modes.map(mode => modeEntry(mode, overrides[mode] ?? (mode === 5 ? (step.status ?? 2) : 1)));
const frame = (step, overrides) => ({ screen: screenOf({ ink: step.ink, cursor: step.cursor, reverse: step.stored ?? false }), modes: step.modes.length ? modesFor(step, overrides) : [] });
const good = () => Object.fromEntries(reverseWideSteps().map(step => [step.role, frame(step)]));
const start = (reverse = false) => ({ screen: screenOf({ ink: [], cursor: [4, 0], reverse, alternate: false }), modes: [modeEntry(5, reverse ? 1 : 2)] });
const grade = (roles, original = false, finish = start(original), first = start(original)) => gradeReverseWide(roles, first, finish, original);
const reverse = result => result['modes.reverse-video'], wide = result => result['text.wide.cjk'];

test('both rows are supported from a complete, ordered, controlled observation and graded separately', () => {
    const result = grade(good());
    assert.deepEqual(Object.keys(result), reverseWideIds);
    assert.deepEqual([reverse(result).outcome, wide(result).outcome], ['supported', 'supported']);
    assert.deepEqual(reverse(result).transition.set, { decrpm: 1, stored: true, defaultsSwapped: true });
    assert.deepEqual(reverse(result).transition.reset, { decrpm: 2, stored: false, defaultsRestored: true });
    assert.equal(reverse(result).transition.toggleLeavesStoredCellsUntouched, true);
    assert.deepEqual(wide(result).observed, { char: cjkChar, kind: 'wide', continuation: 'continuation', cursor: [0, 2] });
});
test('an original set DECSCNM state is restored, not assumed reset', () => {
    const set = grade(good(), true);
    assert.equal(reverse(set).outcome, 'supported');
    assert.equal(reverse(set).transition.initial.stored, true);
    const lost = grade(good(), true, start(false));
    assert.deepEqual([reverse(lost).outcome, reverse(lost).reason], ['inconclusive', 'observation-controls-invalid']);
});

test('reverse video needs a measured transition: an ignored DECSET fails, and recognition or a stale set reply cannot pass', () => {
    // DECSET ignored: DECRPM says reset and the stored readback agrees; with valid controls this is a measured fail.
    const ignored = good(); ignored['rv:set'] = frame({ ...reverseWideSteps()[2], status: 2, stored: false });
    assert.deepEqual([reverse(grade(ignored)).outcome, reverse(grade(ignored)).reason], ['unsupported', 'decset-ignored']);
    assert.equal(wide(grade(ignored)).outcome, 'supported');
    // A stale "set" reply: the reset step still carries Ps 1 beside a reset stored state.
    const stale = good(); stale['rv:reset'] = frame(reverseWideSteps()[3], { 5: 1 });
    assert.deepEqual([reverse(grade(stale)).outcome, reverse(grade(stale)).reason], ['inconclusive', 'transition-observation-incoherent']);
    // The reply says set, the stored readback says reset (or the reverse): the layers disagree, so nothing is concluded.
    const disagree = good(); disagree['rv:set'] = { ...disagree['rv:set'], screen: screenOf({ ink: reverseWideSteps()[2].ink, cursor: [2, 5], reverse: false }) };
    assert.equal(reverse(grade(disagree)).outcome, 'inconclusive');
    // Recognition alone (the as-found reset reply) is never a pass: the set and reset observations are required.
    for (const role of ['rv:set', 'rv:reset', 'rv:off']) { const roles = good(); delete roles[role]; assert.throws(() => grade(roles), /missing observation/); }
    // DECRPM statuses other than set/reset after DECSET cannot pass.
    for (const status of [0, 3, 4]) {
        const roles = good(); roles['rv:set'] = frame(reverseWideSteps()[2], { 5: status });
        assert.equal(reverse(grade(roles)).outcome, 'inconclusive', 'status ' + status);
    }
    // An absent reply is not a status.
    const absent = good(); absent['rv:set'].modes = absent['rv:set'].modes.map(entry => (entry.mode === 5 ? { ...entry, reply: null } : entry));
    assert.equal(reverse(grade(absent)).outcome, 'inconclusive');
});
test('per-cell inverse (SGR 7) is distinct from DECSCNM: neither substitutes for the other', () => {
    // Inverse cells appear at activation while the mode stays reset: this is not a set mode.
    const sgr = good(); sgr['rv:set'] = { ...sgr['rv:set'], screen: screenOf({ ink: reverseWideSteps()[2].ink.map(p => ({ ...p, inverse: true })), cursor: [2, 5], reverse: false }), modes: modesFor({ ...reverseWideSteps()[2], status: 2 }) };
    assert.notEqual(reverse(grade(sgr)).outcome, 'supported');
    // The control needs per-cell inverse cells beside a reset mode; without them the distinction is not demonstrated.
    const none = good(); for (const role of ['rv:seed', 'rv:off']) none[role] = { ...none[role], screen: screenOf({ ink: reverseWideSteps()[0].ink.map(p => ({ ...p, inverse: false })), cursor: [2, 5], reverse: false }) };
    assert.equal(reverse(grade(none)).outcome, 'inconclusive');
    // Per-cell inverse cells cannot be created by the toggle itself.
    const rewritten = good(); rewritten['rv:set'] = { ...rewritten['rv:set'], screen: screenOf({ ink: reverseWideSteps()[2].ink.map(p => ({ ...p, inverse: !p.inverse })), cursor: [2, 5], reverse: true }) };
    assert.equal(reverse(grade(rewritten)).outcome, 'inconclusive');
});
test('the toggle must not rewrite text, rendition, cursor, margins or unrelated modes', () => {
    const text = good(); text['rv:set'].screen = screenOf({ ink: reverseWideSteps()[2].ink.filter(p => p.char !== 'E'), cursor: [2, 5], reverse: true });
    const cursor = good(); cursor['rv:set'].screen = screenOf({ ink: reverseWideSteps()[2].ink, cursor: [2, 6], reverse: true });
    const margins = good(); margins['rv:set'].screen.horizontalMargins.leftMargin = 3;
    const origin = good(); origin['rv:set'].screen.bufferState.originMode = true;
    for (const [label, roles] of [['text', text], ['cursor', cursor], ['margins', margins], ['origin', origin]]) assert.notEqual(reverse(grade(roles)).outcome, 'supported', label);
    const neighbor = good(); neighbor['rv:set'] = frame(reverseWideSteps()[2], { 7: 2 });
    assert.deepEqual([reverse(grade(neighbor)).outcome, reverse(grade(neighbor)).reason], ['inconclusive', 'observation-controls-invalid']);
    const cursorMode = good(); cursorMode['rv:set'] = frame(reverseWideSteps()[2], { 25: 2 });
    assert.equal(reverse(grade(cursorMode)).outcome, 'inconclusive');
    // Presented default colors swap exactly with the mode: a set mode that did not swap them is incoherent, not a pass.
    const unswapped = good(); unswapped['rv:set'].screen.colorBinding.foregroundArgb = baseline.colorBinding.foregroundArgb;
    unswapped['rv:set'].screen.colorBinding.backgroundArgb = baseline.colorBinding.backgroundArgb;
    unswapped['rv:set'].screen.resolvedColors = [{ count: rows * cols, fg: { source: 'default', index: -1, argb: baseline.colorBinding.foregroundArgb }, bg: { source: 'default', index: -1, argb: baseline.colorBinding.backgroundArgb } }];
    assert.equal(reverse(grade(unswapped)).outcome, 'inconclusive');
});
test('missing or inconsistent controls and incomplete restoration give no verdict for reverse video', () => {
    const notOff = good(); notOff['rv:off'] = frame(reverseWideSteps()[1], { 5: 1 });
    assert.deepEqual([reverse(grade(notOff)).outcome, reverse(grade(notOff)).reason], ['inconclusive', 'observation-controls-invalid']);
    const unrestored = grade(good(), false, { ...start(false), modes: [modeEntry(5, 1)], screen: screenOf({ ink: [], cursor: [4, 0], reverse: true, alternate: false }) });
    assert.deepEqual([reverse(unrestored).outcome, reverse(unrestored).reason], ['inconclusive', 'observation-controls-invalid']);
    const incoherentStart = grade(good(), false, start(false), { ...start(false), modes: [modeEntry(5, 1)] });
    assert.equal(reverse(incoherentStart).outcome, 'inconclusive');
    assert.equal(wide(incoherentStart).outcome, 'supported');
    const missing = good(); missing['rv:seed'].screen.cursorCol += 1;
    assert.equal(reverse(grade(missing)).outcome, 'inconclusive');
});

test('text.wide.cjk needs the exact character, the wide flag, the continuation cell, the cursor and untouched surroundings', () => {
    const target = reverseWideSteps().find(s => s.role === 'wide:target');
    const withCells = (ink, cursor = [0, 2]) => { const r = good(); r['wide:target'] = { screen: screenOf({ ink: [...ink, ...target.ink.filter(p => p.row > 0)], cursor }), modes: [] }; return r; };
    const base = [{ row: 0, col: 0, char: cjkChar, kind: 'wide', inverse: false }, { row: 0, col: 1, char: '', kind: 'continuation', inverse: false }];
    assert.equal(wide(grade(withCells(base))).outcome, 'supported');
    // A different CJK character with correct width cannot stand in.
    const other = withCells([{ ...base[0], char: '世' }, base[1]]);
    assert.deepEqual([wide(grade(other)).outcome, wide(grade(other)).reason], ['unsupported', 'target-mismatch:cells']);
    // Correct text stored narrow with the cursor two columns on: the cursor advance and the string alone prove nothing.
    const narrow = withCells([{ ...base[0], kind: 'narrow' }]);
    assert.deepEqual([wide(grade(narrow)).outcome, wide(grade(narrow)).reason], ['unsupported', 'target-mismatch:cells']);
    const narrowPair = withCells([{ ...base[0], kind: 'narrow' }, { row: 0, col: 1, char: ' ', kind: 'narrow', inverse: false }].slice(0, 1));
    assert.equal(wide(grade(narrowPair)).outcome, 'unsupported');
    // Wide flag without its continuation, or a continuation without the wide flag, is an incoherent row: refused, never a pass.
    const noContinuation = withCells([base[0]]);
    assert.throws(() => grade(noContinuation), /no continuation/);
    const orphan = withCells([{ ...base[0], kind: 'narrow' }, base[1]]);
    assert.throws(() => grade(orphan), /Orphan wide continuation/);
    // Wrong cursor, a stray neighbor cell or a changed surrounding row are measured mismatches.
    assert.equal(wide(grade(withCells(base, [0, 1]))).reason, 'target-mismatch:cursor');
    assert.equal(wide(grade(withCells([...base, { row: 0, col: 2, char: 'x', kind: 'narrow', inverse: false }]))).reason, 'target-mismatch:cells');
    const surround = good(); surround['wide:target'].screen = screenOf({ ink: [...base, ...target.ink.filter(p => p.row === 9)], cursor: [0, 2] });
    assert.equal(wide(grade(surround)).reason, 'target-mismatch:cells');
    // An empty target (nothing written) is a measured mismatch with valid controls, not an inconclusive.
    const empty = good(); empty['wide:target'].screen = screenOf({ ink: target.ink.filter(p => p.row > 0), cursor: [0, 0] });
    assert.equal(wide(grade(empty)).reason, 'target-mismatch:cells');
    // An attribute the contract never sets (here bold on the stored wide cell) is a measured mismatch of the stored tuple.
    const bold = good(); bold['wide:target'].screen = screenOf({ ink: [...base, ...target.ink.filter(p => p.row > 0)], cursor: [0, 2] });
    bold['wide:target'].screen.cellDetails[0] = { ...bold['wide:target'].screen.cellDetails[0], flags: bold['wide:target'].screen.cellDetails[0].flags | 1 };
    assert.deepEqual([wide(grade(bold)).outcome, wide(grade(bold)).reason], ['unsupported', 'target-mismatch:stored-attributes']);
});
test('the wide-cell controls guard the target: stale wide cells, ASCII, and cursor-only agreement', () => {
    for (const role of ['wide:stale-seed', 'wide:setup', 'ctl:ascii:setup', 'ctl:narrow2:setup']) {
        const roles = good(), step = reverseWideSteps().find(s => s.role === role);
        roles[role].screen = screenOf({ ink: step.ink, cursor: [step.cursor[0], step.cursor[1] + 1] });
        assert.deepEqual([wide(grade(roles)).outcome, wide(grade(roles)).reason], ['inconclusive', 'observation-controls-invalid'], role);
        assert.equal(reverse(grade(roles)).outcome, 'supported');
    }
    // The setup must be genuinely empty: a stale wide cell left at home voids the target.
    const stale = good(); stale['wide:setup'].screen = screenOf({ ink: reverseWideSteps().find(s => s.role === 'wide:stale-seed').ink, cursor: [0, 0] });
    assert.equal(wide(grade(stale)).outcome, 'inconclusive');
    // Two narrow characters reach the same cursor column as one wide character; the control must be stored narrow.
    const pair = good(); pair['ctl:narrow2'].screen = screenOf({ ink: reverseWideSteps().find(s => s.role === 'wide:target').ink, cursor: [0, 2] });
    assert.equal(wide(grade(pair)).outcome, 'inconclusive');
    const target = reverseWideSteps().find(s => s.role === 'wide:target');
    assert.deepEqual(target.cursor, reverseWideSteps().find(s => s.role === 'ctl:narrow2').cursor);
    assert.notDeepEqual(target.ink.filter(p => p.row === 0), reverseWideSteps().find(s => s.role === 'ctl:narrow2').ink.filter(p => p.row === 0));
});

// ---- Journal ----
function journal() {
    const id = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111';
    const make = (screen, kind, index, extra = {}, bar = barrier) => ({ index, kind, paneId, barrier: index ? bar : null, screen, sha256: snapshotHash(screen), ...extra });
    const startFrame = start(false), bind = screenOf({ ink: [], cursor: [0, 0], alternate: true });
    bind.lines[0] = reverseWideNonce + id; bind.cursorCol = bind.lines[0].length;
    const frames = [make(startFrame.screen, 'rw-start', 0, { modes: startFrame.modes }), make(bind, 'rw-bind', 1)], roles = good(), reads = [], plan = reverseWidePlan();
    for (const step of plan.filter(x => x.read)) {
        const { screen, modes } = roles[step.role], row = screen.cursorRow + 1, col = screen.cursorCol + 1;
        const cpr = { barrier, reply: { raw: `\x1b[${row};${col}R`, row, col } };
        reads.push({ role: step.role, modes, cpr, projection: step.projection }); frames.push(make(screen, 'rw-read', frames.length, { cpr, modes, projection: step.projection }));
    }
    const end = start(false);
    frames.push(make(end.screen, 'rw-finish', frames.length, { modes: end.modes }));
    const feeds = plan.filter(x => x.feed !== undefined).map(x => x.feed);
    return { id, frames, run: { suiteId: reverseWideSuiteId, runId: id, original: false, restoreFeed: restoreFeed(false), feeds, barriers: feeds.map(() => barrier),
        initial: { modes: startFrame.modes }, reads, final: { modes: end.modes } } };
}
test('journal rederivation retains raw DECRPM and CPR replies beside the observations and rejects altered bytes, stale frames and incomplete restoration', () => {
    const { id, frames, run } = journal(), report = buildReverseWideReport(frames, run, id);
    assert.deepEqual(Object.keys(report.effects), reverseWideIds);
    const rv = report.effects['modes.reverse-video'], cjk = report.effects['text.wide.cjk'];
    assert.deepEqual([rv.outcome, rv.fixture], ['supported', decset5 + ' then ' + decrst5]);
    assert.deepEqual(rv.observed.before.modes.map(m => m.raw), ['\x1b[?5;2$y', '\x1b[?7;1$y', '\x1b[?25;1$y']);
    assert.deepEqual(rv.observed.after.modes.map(m => m.raw), ['\x1b[?5;1$y', '\x1b[?7;1$y', '\x1b[?25;1$y']);
    assert.deepEqual([rv.observed.before.reverseVideo, rv.observed.after.reverseVideo], [false, true]);
    assert.deepEqual([cjk.outcome, cjk.fixture, cjk.specimen.utf8Hex, cjk.observed.after.cursor, cjk.observed.after.cpr.raw], ['supported', cjkChar, 'e4b8ad', [0, 2], '\x1b[1;3R']);
    assert.deepEqual(cjk.observed.after.ink.filter(p => p.row === 0).map(p => [p.col, p.char, p.kind]), [[0, cjkChar, 'wide'], [1, '', 'continuation']]);
    for (const [from, to] of [[decset5, '\x1b[?5l'], [cjkChar, '世'], [cjkChar, 'AB']]) {
        const changed = structuredClone(run), index = changed.feeds.findIndex(feed => feed === from);
        assert.ok(index >= 0); changed.feeds[index] = to;
        assert.throws(() => buildReverseWideReport(frames, changed, id), /fixture bytes/);
    }
    assert.throws(() => buildReverseWideReport(frames, run, 'b'.repeat(32)), /identity/);
    assert.throws(() => buildReverseWideReport(frames.slice(0, -1), run, id), /Incomplete/);
    const stale = structuredClone(frames); stale[3].screen.cursorCol++;
    assert.throws(() => buildReverseWideReport(stale, run, id), /snapshot/);
    const unrestored = structuredClone(frames); unrestored.at(-1).screen.colorBinding.reverseVideo = true; unrestored.at(-1).sha256 = snapshotHash(unrestored.at(-1).screen);
    assert.throws(() => buildReverseWideReport(unrestored, run, id), /restored/);
    const forgedRestore = structuredClone(run); forgedRestore.restoreFeed = restoreFeed(true);
    assert.throws(() => buildReverseWideReport(frames, forgedRestore, id), /restoring bytes/);
    const forgedOriginal = structuredClone(run); forgedOriginal.original = true;
    assert.throws(() => buildReverseWideReport(frames, forgedOriginal, id), /original state/);
});
test('journal rederivation rejects stale, swapped, malformed, missing or incoherent mode replies and reordered reads', () => {
    const { id, frames, run } = journal(), setIndex = reverseWidePlan().filter(x => x.read).findIndex(x => x.role === 'rv:set');
    const swap = (mutate, message) => {
        const f = structuredClone(frames), r = structuredClone(run); mutate(f, r);
        for (const fr of f) fr.sha256 = snapshotHash(fr.screen);
        assert.throws(() => buildReverseWideReport(f, r, id), message);
    };
    // A stale "reset" reply retained in the set read, in the journal and in the run record alike.
    swap((f, r) => { const stale = modeEntry(5, 2); f[2 + setIndex].modes[0] = stale; r.reads[setIndex].modes[0] = stale; }, /DECRPM disagrees/);
    // A reply of another mode in the slot of mode 5, a malformed status, a missing reply, an extra mode.
    swap((f, r) => { const other = modeEntry(6, 1); f[2 + setIndex].modes[0] = other; r.reads[setIndex].modes[0] = other; }, /retained mode reports differ/);
    swap((f, r) => { f[2 + setIndex].modes[0].reply.raw = '\x1b[?5;9$y'; r.reads[setIndex].modes[0].reply.raw = '\x1b[?5;9$y'; }, /status|Unexpected/);
    swap((f, r) => { f[2 + setIndex].modes[0].reply = null; r.reads[setIndex].modes[0].reply = null; }, /no DECRPM reply/);
    swap((f, r) => { f[2 + setIndex].modes.push(modeEntry(1, 2)); r.reads[setIndex].modes.push(modeEntry(1, 2)); }, /retained mode reports differ/);
    // The worker's record and the parent journal must agree.
    swap((f, r) => { r.reads[setIndex].modes[0] = modeEntry(5, 2); }, /read order or retained reports changed/);
    swap((f, r) => { r.initial.modes[0] = modeEntry(5, 1); }, /as-found mode report changed/);
    swap((f, r) => { [r.reads[1], r.reads[2]] = [r.reads[2], r.reads[1]]; }, /read order/);
    // A CPR reply that disagrees with the physical cursor, or a barrier that is not the read's.
    swap((f, r) => { f[2 + setIndex].cpr.reply = { raw: '\x1b[9;9R', row: 9, col: 9 }; r.reads[setIndex].cpr = f[2 + setIndex].cpr; }, /CPR disagrees/);
    swap((f) => { f[2 + setIndex].barrier = '\x1b[?1;2c'; }, /ordered CPR/);
    // The final DECRPM must report the restored state.
    swap((f, r) => { const wrong = modeEntry(5, 1); f.at(-1).modes[0] = wrong; r.final.modes[0] = wrong; }, /not coherent/);
});

test('the plan fixes which reads are stored-cell-only; a substituted projection is refused and the observer restores its flags', async () => {
    const reads = reverseWidePlan().filter(x => x.read), target = reads.findIndex(x => x.role === 'wide:target');
    assert.deepEqual(reads.filter(x => x.projection === 'cells').map(x => x.role), ['wide:stale-seed', 'wide:target']);
    assert.ok(reads.filter(x => x.projection === 'full').every(x => !x.role.endsWith('target')));
    const { id, frames, run } = journal();
    assert.equal(frames[2 + target].screen.attributeRuns, undefined); assert.equal(frames[2 + target].screen.colorBinding, undefined);
    const rehash = list => { for (const f of list) f.sha256 = snapshotHash(f.screen); return list; };
    // A frame with the full projection where the plan says cells, a record that claims another projection, and a cells frame where the plan says full.
    const full = structuredClone(frames); full[2 + target].screen = screenOf({ ink: reverseWideSteps().find(s => s.role === 'wide:target').ink, cursor: [0, 2], cells: false });
    assert.throws(() => buildReverseWideReport(rehash(full), run, id), /read projection changed/);
    const claimed = structuredClone(run); claimed.reads[target].projection = 'full';
    assert.throws(() => buildReverseWideReport(frames, claimed, id), /read projection changed/);
    const cellsOnly = structuredClone(frames); cellsOnly[2].screen = screenOf({ ink: reverseWideSteps()[0].ink, cursor: [2, 5], cells: true });
    assert.throws(() => buildReverseWideReport(rehash(cellsOnly), run, id), /read projection changed/);
    // A cells frame that still carries a binding is not a stored-cell-only frame.
    const mixed = structuredClone(frames); mixed[2 + target].screen.colorBinding = structuredClone(baseline.colorBinding);
    assert.throws(() => buildReverseWideReport(rehash(mixed), run, id), /projection it cannot support|read projection changed/);
    // The observer drops the attribute strings and resolved colors for a cells read only, even when the read fails.
    const original = OwnedCellObserver.prototype.handle, seen = [], observer = Object.create(ReverseWideObserver.prototype);
    Object.assign(observer, { frames: [], includeAttributes: true, includeResolvedColors: true });
    OwnedCellObserver.prototype.handle = async function () { seen.push([this.includeAttributes, this.includeResolvedColors]); if (seen.length === 2) throw new Error('refused'); this.frames.push({}); return 'ok'; };
    try {
        const cpr = { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } };
        await observer.handleRequest({ kind: 'rw-read', barrier, cpr, modes: [], projection: 'cells' }, 5);
        await assert.rejects(observer.handleRequest({ kind: 'rw-read', barrier, cpr, modes: [], projection: 'cells' }, 5), /refused/);
        await observer.handleRequest({ kind: 'rw-read', barrier, cpr, modes: [], projection: 'full' }, 5);
        assert.deepEqual(seen, [[false, false], [false, false], [true, true]]);
        assert.deepEqual([observer.includeAttributes, observer.includeResolvedColors], [true, true]);
        assert.equal(observer.frames[0].projection, 'cells'); assert.equal(observer.frames[1].projection, 'full');
    } finally { OwnedCellObserver.prototype.handle = original; }
});

test('receipt rejects stripped artifacts and a coherently rehashed fixture alteration', () => {
    const out = mkdtempSync(join(tmpdir(), 'nt-rw-receipt-'));
    try {
        const { id, frames, run } = journal(), hash = data => createHash('sha256').update(data).digest('hex');
        const runBytes = Buffer.from(JSON.stringify(run)), result = Buffer.from(JSON.stringify(buildReverseWideReport(frames, run, id)));
        writeFileSync(join(out, 'rw-run.json'), runBytes); writeFileSync(join(out, 'rw-observations.json'), result);
        writeFileSync(join(out, 'rw-snapshots.jsonl'), frames.map(f => JSON.stringify(f)).join('\n') + '\n');
        const context = { reverseWideSuiteId }, evidence = { sgrRasterEvidence: {}, coreWrapEvidence: {}, tabStopsEvidence: {}, coreEraseEvidence: {}, textCursorEvidence: {}, reverseWideEvidence: { runId: id, suiteId: reverseWideSuiteId,
            file: 'rw-observations.json', runFile: 'rw-run.json', sha256: hash(result), runSha256: hash(runBytes), provisionalUntil: 'cell-receipt.json' } };
        assert.ok(includesReverseWideEvidence(out, evidence, context));
        assert.doesNotThrow(() => checkReverseWideEvidence(out, evidence, id, context));
        const forged = structuredClone(run); forged.feeds[forged.feeds.indexOf(cjkChar)] = '世';
        const bytes = Buffer.from(JSON.stringify(forged)); writeFileSync(join(out, 'rw-run.json'), bytes); evidence.reverseWideEvidence.runSha256 = hash(bytes);
        assert.throws(() => checkReverseWideEvidence(out, evidence, id, context), /fixture bytes/);
        writeFileSync(join(out, 'rw-run.json'), runBytes); evidence.reverseWideEvidence.runSha256 = hash(runBytes);
        const mismatched = structuredClone(evidence); mismatched.reverseWideEvidence.runId = 'c'.repeat(32);
        assert.throws(() => checkReverseWideEvidence(out, mismatched, id, context), /binding mismatch/);
        const noPredecessor = structuredClone(evidence); delete noPredecessor.sgrRasterEvidence;
        assert.throws(() => checkReverseWideEvidence(out, noPredecessor, id, context), /predecessor/);
        // An altered observation byte (a changed conclusion) fails its digest and its re-derivation.
        const tampered = JSON.parse(result); tampered.effects['text.wide.cjk'].outcome = 'unsupported';
        const tamperedBytes = Buffer.from(JSON.stringify(tampered)); writeFileSync(join(out, 'rw-observations.json'), tamperedBytes);
        assert.throws(() => checkReverseWideEvidence(out, evidence, id, context), /digest mismatch/);
        evidence.reverseWideEvidence.sha256 = hash(tamperedBytes);
        assert.throws(() => checkReverseWideEvidence(out, evidence, id, context), /rederivation mismatch/);
        writeFileSync(join(out, 'rw-observations.json'), result);
        writeFileSync(join(out, 'rw-snapshots.jsonl'), '');
        assert.throws(() => checkReverseWideEvidence(out, evidence, id, context));
        assert.equal(includesReverseWideEvidence(out, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v44' }), true);
    } finally { rmSync(out, { recursive: true, force: true }); }
});

test('observer journals the DECRPM exchanges and CPR bound to each completed observation and rejects mismatches', async () => {
    const original = OwnedCellObserver.prototype.handle, events = [], observer = Object.create(ReverseWideObserver.prototype);
    observer.frames = [];
    OwnedCellObserver.prototype.handle = async function (kind, budget, bar) { events.push([kind, budget, bar]); this.frames.push({}); return 'ok'; };
    try {
        const cpr = { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } }, modes = [modeEntry(5, 2)];
        assert.equal(await observer.handleRequest({ kind: 'rw-read', barrier, cpr, modes, projection: 'full' }, 5), 'ok');
        assert.deepEqual(events, [['rw-read', 5, barrier]]); assert.deepEqual(observer.frames[0].cpr, cpr); assert.deepEqual(observer.frames[0].modes, modes);
        assert.equal(observer.frames[0].projection, 'full');
        await assert.rejects(observer.handleRequest({ kind: 'rw-read', barrier: 'wrong', cpr, modes, projection: 'full' }, 5), /barrier mismatch/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-read', barrier, cpr, modes }, 5), /unknown projection/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-read', barrier, cpr, modes, projection: 'attributes' }, 5), /unknown projection/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-start', barrier: null, modes, projection: 'cells' }, 5), /unexpected projection/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-start', barrier: null, cpr, modes }, 5), /unexpected CPR/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-bind', barrier, modes }, 5), /unexpected mode reports/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-read', barrier, cpr, projection: 'full' }, 5), /unexpected mode reports/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-finish', barrier: '\x1b[?1;2c', modes }, 5), /barrier mismatch/);
        await assert.rejects(observer.handleRequest({ kind: 'rw-read', barrier, cpr, modes: [{ ...modes[0], reply: { raw: '\x1b[?7;2$y', mode: 5, status: 2 } }], projection: 'full' }, 5), /Unexpected|different|disagrees/);
        assert.equal(await observer.handleRequest({ kind: 'rw-start', barrier: null, modes }, 5), 'ok');
        assert.equal(await observer.handleRequest({ kind: 'rw-finish', barrier, modes }, 5), 'ok');
    } finally { OwnedCellObserver.prototype.handle = original; }
});
test('collector serializes writes, DECRPM, CPR and observation, and restores the original DECSCNM state after a refusal', async () => {
    for (const originalReverse of [false, true]) {
        const events = [];
        const fence = { write: async bytes => { events.push(['write', bytes]); return barrier; },
            exchange: async (sequence, parse) => { const mode = Number(/^\x1b\[\?(\d+)\$p$/.exec(sequence)[1]); events.push(['mode', mode]); const status = mode === 5 ? 2 : 1;
                const bytes = `\x1b[?${mode};${status}$y${barrier}`; return parse(bytes); },
            cursorReport: async () => { events.push(['cpr']); return { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } }; } };
        let reads = 0;
        const owner = async kind => { events.push([kind]); if (kind === 'rw-read' && ++reads === 3) throw new Error('refused');
            const screen = structuredClone(baseline); screen.colorBinding.reverseVideo = originalReverse; return { screen }; };
        await assert.rejects(collectReverseWide({ config: { runId: 'a'.repeat(32) }, fence, owner }), /refused/);
        // The as-found DECRPM precedes the start observation, which precedes every write.
        assert.deepEqual(events.slice(0, 2), [['mode', 5], ['rw-start']]);
        const set = events.findIndex(event => event[0] === 'write' && event[1] === decset5);
        assert.ok(set > 0); assert.deepEqual(events.slice(set + 1, set + 6), [['mode', 5], ['mode', 7], ['mode', 25], ['cpr'], ['rw-read']]);
        const finish = events.at(-1), mode = events.at(-2), last = events.at(-3);
        assert.deepEqual([finish[0], mode], ['rw-finish', ['mode', 5]]);
        assert.deepEqual(last, ['write', restoreFeed(originalReverse)]);
    }
});
test('a failed fence restores the original DECSCNM state through the bounded cleanup path and observes nothing further', async () => {
    for (const originalReverse of [false, true]) {
        const events = [];
        let failed = false;
        const fence = { get failed() { return failed; },
            write: async bytes => { if (failed) throw new Error('fence failed'); events.push(['write', bytes]); return barrier; },
            writeCleanup: async bytes => { if (!failed) throw new Error('cleanup needs a failed fence'); events.push(['cleanup', bytes]); },
            exchange: async sequence => { events.push(['exchange', sequence]);
                // The reply to the DECRPM asked right after DECSET 5 never completes: the fence marks itself failed.
                if (events.filter(e => e[0] === 'write' && e[1] === decset5).length && events.filter(e => e[0] === 'exchange').length > 1) { failed = true; throw new Error('Cell barrier timed out.'); }
                const mode = Number(/^\x1b\[\?(\d+)\$p$/.exec(sequence)[1]);
                return { barrier, reply: { raw: `\x1b[?${mode};2$y`, mode, status: 2 } }; },
            cursorReport: async () => ({ barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } }) };
        const owner = async kind => { events.push([kind]); const screen = structuredClone(baseline); screen.colorBinding.reverseVideo = originalReverse; return { screen }; };
        await assert.rejects(collectReverseWide({ config: { runId: 'a'.repeat(32) }, fence, owner }), /Cell barrier timed out/);
        // The restoration bytes were transmitted (after DECSET 5 was written) through the cleanup path, and nothing was observed afterwards.
        const set = events.findIndex(e => e[0] === 'write' && e[1] === decset5), cleanup = events.findIndex(e => e[0] === 'cleanup');
        assert.ok(set > 0 && cleanup > set, 'cleanup follows the failed exchange');
        assert.deepEqual(events[cleanup], ['cleanup', restoreFeed(originalReverse)]);
        assert.deepEqual(events.slice(cleanup + 1), []);
        assert.ok(!events.some(e => e[0] === 'rw-finish'));
    }
    // A healthy fence still observes the restoration through DECRPM and the finish frame (covered by the collector test above).
});
test('specimen table keeps the exact bytes and expectations for the two rows', () => {
    assert.deepEqual(Object.keys(reverseWideSpecimens), reverseWideIds);
    assert.deepEqual(reverseWideSpecimens['modes.reverse-video'].sequence, [decset5, decrst5]);
    assert.equal(reverseWideSpecimens['text.wide.cjk'].fixture, cjkChar);
    assert.equal(reverseWideSpecimens['text.wide.cjk'].codePoint, 'U+4E2D');
});

/** The SGR reset raster report is the base here, so closing exactly the two rows yields 80/80; production builds on the merged SGR reset raster publication. */
function derivation(mutateEvidence = () => {}, mutatePrevious = {}) {
    const temp = mkdtempSync(join(tmpdir(), 'nt-rw-rows-'));
    try {
        const m = loadSgrMeasurement(), mapping = loadMapping();
        extractOwnedArchive(root, { archives: { 'owned-run.zip': m.ownedArchiveSha256 } }, temp, join(root, sgrPublication, 'owned-run.zip'));
        const { frames, run, id } = journal(), supplement = buildReverseWideReport(frames, run, id);
        supplement.runId = m.measurement.runId; mutateEvidence(supplement);
        writeFileSync(join(temp, 'rw-observations.json'), JSON.stringify(supplement));
        const suite = { suiteId: tabStopsSuiteId }, identity = { context: { runId: m.measurement.runId },
            receipt: { reverseWideEvidence: { suiteId: reverseWideSuiteId }, sgrRasterEvidence: { suiteId: 'ntilde-core-sgr-raster-v1' }, coreWrapEvidence: { suiteId: 'ntilde-core-wrap-v1' }, tabStopsEvidence: suite, coreEraseEvidence: { suiteId: 'ntilde-core-erase-v1' } } };
        const result = deriveReverseWideCoreReport(temp, mapping, identity, m.measurement, mutatePrevious.previous ?? undefined);
        return { ...result, mapping };
    } finally { rmSync(temp, { recursive: true, force: true }); }
}
test('80-row derivation closes exactly the two rows and computes 80/80 from the evidence', () => {
    const { report, mapping } = derivation();
    assert.equal(report.rows.length, 80); assert.equal(new Set(report.rows.map(r => r.id)).size, 80);
    assert.deepEqual(report.totals, { pass: 80, fail: 0, inconclusive: 0 }); assert.equal(report.corePassPercentage, 100);
    for (const id of reverseWideIds) {
        const row = report.rows.find(r => r.id === id);
        assert.equal(row.conclusion, 'pass'); assert.equal(row.historicalConclusion, 'inconclusive');
        assert.equal(row.historicalPinnedProbe.stockVerdict, 'supported'); assert.deepEqual(row.pinnedProbe.qualified, []);
        assert.equal(row.exactFixtureEffects.length, 1); assert.ok(row.controls['rv:set'] && row.controls['ctl:narrow2'] && row.controls.restored);
    }
    const rv = report.rows.find(r => r.id === 'modes.reverse-video').exactFixtureEffects[0];
    assert.deepEqual(rv.observed.after.modes.map(m => m.raw), ['\x1b[?5;1$y', '\x1b[?7;1$y', '\x1b[?25;1$y']);
    const text = readableReverseWideReport(report, mapping);
    assert.ok(!text.includes('[object Object]') && text.includes('80 pass / 0 fail / 0 inconclusive'));
    assert.ok(text.includes('no rendered color inversion is claimed') && text.includes('not general Unicode width correctness'));
    // The wrapping, tab-stop and erase closures of the publications it builds on are retained, not lost.
    const closed = report.rows.filter(r => r.conclusion === 'pass').map(r => r.id);
    for (const id of ['sgr.selective-reset.bold', 'sgr.reset', 'reset.sgr', 'text.wrap', 'modes.auto-wrap', 'text.tab', 'unicode.tab-stops', 'erase.line.right', 'erase.screen.above', 'erase.screen.below', 'modes.reverse-video', 'text.wide.cjk']) assert.ok(closed.includes(id), id);
    assert.ok(text.includes('## text.wrap (#948, retained)') && text.includes('## modes.auto-wrap (#948, retained)') && text.includes('## SGR reset raster experiments'));
});
test('negative results with an unreadable stored cell still render: a null decoded projection is reported as unavailable, never thrown on', () => {
    const { report, mapping } = derivation(s => {
        // The wide target holds a stored attribute the contract never sets: a measured mismatch whose decoded projection is unavailable.
        Object.assign(s.effects['text.wide.cjk'], { outcome: 'unsupported', reason: 'target-mismatch:stored-attributes' });
        for (const key of ['before', 'after']) s.effects['text.wide.cjk'].observed[key].ink = null;
        s.effects['text.wide.cjk'].observed.char = null;
        for (const role of ['wide:stale-seed', 'ctl:ascii', 'ctl:narrow2']) s.controls[role].ink = null;
        // The per-cell inverse seed control is unreadable too: reverse video is inconclusive and renders.
        Object.assign(s.effects['modes.reverse-video'], { outcome: 'inconclusive', reason: 'observation-controls-invalid' });
        s.controls['rv:seed'].ink = null;
    });
    const text = readableReverseWideReport(report, mapping);
    assert.equal(report.rows.find(r => r.id === 'text.wide.cjk').conclusion, 'fail');
    assert.equal(report.rows.find(r => r.id === 'modes.reverse-video').conclusion, 'inconclusive');
    assert.ok(text.includes('unavailable (a stored cell carries an attribute the contract never sets)'));
    assert.ok(!text.includes('[object Object]') && !text.includes('undefined'));
    // The positive rendering is unchanged by the fallback.
    assert.ok(readableReverseWideReport(derivation().report, mapping).includes('per-cell inverse cells present with the mode off (seed): "CD"'));
});
test('a failed, inconclusive or mislabeled specimen is never reported as a pass', () => {
    const fail = derivation(s => { Object.assign(s.effects['modes.reverse-video'], { outcome: 'unsupported', reason: 'decset-ignored' }); });
    assert.equal(fail.report.rows.find(r => r.id === 'modes.reverse-video').conclusion, 'fail');
    assert.match(fail.report.rows.find(r => r.id === 'modes.reverse-video').explanation, /DECSET 5 was sent/);
    assert.deepEqual(fail.report.totals, { pass: 79, fail: 1, inconclusive: 0 });
    const open = derivation(s => { Object.assign(s.effects['text.wide.cjk'], { outcome: 'inconclusive', reason: 'observation-controls-invalid' }); });
    const row = open.report.rows.find(r => r.id === 'text.wide.cjk');
    assert.equal(row.conclusion, 'inconclusive'); assert.match(row.blocker, /required control/);
    assert.deepEqual(open.report.totals, { pass: 79, fail: 0, inconclusive: 1 });
});
test('mode recognition, per-cell inverse, a different specimen, cursor-only width and tampered evidence cannot become a pass', () => {
    // Recognition alone: the as-found reply with no measured set is not a transition.
    assert.throws(() => derivation(s => { s.effects['modes.reverse-video'].transition.set = { decrpm: 2, stored: false, defaultsSwapped: false }; }), /verdict without a measured transition/);
    assert.throws(() => derivation(s => { s.effects['modes.reverse-video'].transition.set.decrpm = 3; }), /verdict without a measured transition/);
    assert.throws(() => derivation(s => { s.effects['modes.reverse-video'].transition.restored.decrpm = 1; }), /verdict without a measured transition/);
    assert.throws(() => derivation(s => { s.effects['modes.reverse-video'].transition.toggleLeavesStoredCellsUntouched = false; }), /verdict without a measured transition/);
    assert.throws(() => derivation(s => { delete s.effects['modes.reverse-video'].transition; }), /incomplete transition/);
    assert.throws(() => derivation(s => { s.effects['modes.reverse-video'].observed.after.modes.pop(); }), /incomplete transition/);
    // A different CJK specimen, or the right character without width evidence.
    assert.throws(() => derivation(s => { s.effects['text.wide.cjk'].fixture = '\u4e16'; }), /invalid specimen text.wide.cjk/);
    assert.throws(() => derivation(s => { s.effects['text.wide.cjk'].specimen.utf8Hex = 'e4b896'; }), /invalid specimen text.wide.cjk/);
    assert.throws(() => derivation(s => { s.effects['text.wide.cjk'].observed.char = '\u4e16'; }), /verdict without stored wide-cell evidence/);
    assert.throws(() => derivation(s => { s.effects['text.wide.cjk'].observed.kind = 'narrow'; }), /verdict without stored wide-cell evidence/);
    assert.throws(() => derivation(s => { s.effects['text.wide.cjk'].observed.continuation = 'narrow'; }), /verdict without stored wide-cell evidence/);
    assert.throws(() => derivation(s => { s.effects['text.wide.cjk'].observed.cursor = [0, 1]; }), /verdict without stored wide-cell evidence/);
    assert.throws(() => derivation(s => { s.effects['text.wide.cjk'].observed.after.cpr.raw = '\x1b[1;2R'; }), /verdict without stored wide-cell evidence/);
    // Missing, duplicate, reordered or stale evidence.
    assert.throws(() => derivation(s => { delete s.effects['text.wide.cjk']; }), /missing, duplicate/);
    assert.throws(() => derivation(s => { s.effects = { 'text.wide.cjk': s.effects['text.wide.cjk'], 'modes.reverse-video': s.effects['modes.reverse-video'] }; }), /missing, duplicate or reordered/);
    assert.throws(() => derivation(s => { delete s.effects['modes.reverse-video'].observed.after; }), /invalid specimen modes.reverse-video/);
    assert.throws(() => derivation(s => { s.runId = 'c'.repeat(32); }), /stale reverse-video/);
    assert.throws(() => derivation(s => { s.suiteId = 'ntilde-core-tab-stops-v1'; }), /stale reverse-video/);
    assert.throws(() => derivation(s => { s.effects['modes.reverse-video'].fixture = decset5; }), /invalid specimen modes.reverse-video/);
});
test('a conclusion of the preceding publication cannot be lost or silently changed, and its bytes are pinned', () => {
    assert.throws(() => derivation(() => {}, { previous: { derive: () => { throw new Error('unused'); }, report: 'docs/qa/terminfo/2026-11-11-core-sgr-raster-results/report.json', sha256: '0'.repeat(64) } }), /preceding publication changed/);
    const real = readFileSync(join(root, 'docs/qa/terminfo/2026-11-11-core-sgr-raster-results/report.json'));
    const sha256 = createHash('sha256').update(real).digest('hex');
    const base = { report: 'docs/qa/terminfo/2026-11-11-core-sgr-raster-results/report.json', sha256 };
    const lossy = { ...base, derive: (...args) => { const r = previousDerive(...args); r.report.rows.find(x => x.id === 'text.wrap').conclusion = 'inconclusive'; return r; } };
    assert.throws(() => derivation(() => {}, { previous: lossy }), /unattributed conclusion change or lost merged conclusion: text.wrap/);
    const lostSgr = { ...base, derive: (...args) => { const r = previousDerive(...args); r.report.rows.find(x => x.id === 'sgr.reset').conclusion = 'inconclusive'; return r; } };
    assert.throws(() => derivation(() => {}, { previous: lostSgr }), /unattributed conclusion change or lost merged conclusion: sgr.reset/);
    const short = { ...base, derive: (...args) => { const r = previousDerive(...args); r.report.rows.pop(); return r; } };
    assert.throws(() => derivation(() => {}, { previous: short }), /80 exact unique/);
});
