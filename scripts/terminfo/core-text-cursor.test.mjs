import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { snapshotHash } from './agent-observer.mjs';
import { fileURLToPath } from 'node:url';
import { textCursorIds, textCursorPlan, gradeTextCursor, buildTextCursorReport, collectTextCursor, textCursorSuiteId, textCursorNonce } from './core-text-cursor.mjs';

const archive = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-11-core-results/owned-run.zip', import.meta.url));
const baseline = JSON.parse(execFileSync('unzip', ['-p', archive, 'cg-snapshots.jsonl'], { maxBuffer: 100_000_000 }).toString().split('\n')[0]).screen;
const frame = (lines, cursor) => {
    const screen = structuredClone(baseline);
    screen.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: screen.cols - 1 };
    screen.lines = Array(screen.rows).fill('');
    lines.forEach((line, i) => { screen.lines[i] = line; });
    screen.lines[9] = 'GUARD';
    [screen.cursorRow, screen.cursorCol] = cursor;
    return { screen };
};
const good = () => {
    const roles = {};
    for (const id of textCursorIds) roles[id + ':setup'] = frame([], [0, 0]);
    for (const key of ['save:setup', 'control:ind:setup', 'control:nel:setup', 'control:cr:setup']) roles[key] = frame([], [0, 0]);
    const facts = {
        'text.cr': [['CB'], [0, 1]], 'text.newline': [['A', 'B'], [1, 1]],
        'text.backspace': [['AC'], [0, 2]], 'text.index': [['A'], [1, 1]],
        'text.next-line': [['ABC'], [1, 0]], 'cursor.move.up': [[], [3, 0]],
        'cursor.save-restore': [['AB'], [0, 2]], 'cursor.hpa': [['ABCDEFGH'], [0, 4]],
    };
    for (const [id, [lines, cursor]] of Object.entries(facts)) roles[id + ':result'] = frame(lines, cursor);
    roles['save:displaced'] = frame(['AB'], [4, 4]);
    roles['save:restored'] = frame(['AB'], [0, 2]);
    roles['control:ind'] = frame(['ABC'], [1, 3]); roles['control:nel'] = frame(['ABC'], [1, 0]);
    roles['control:cr'] = frame(['ABC'], [0, 0]);
    return roles;
};

test('the eight joined byte sequences are each one feed', () => {
    const feeds = textCursorPlan().filter(x => x.feed).map(x => x.feed);
    for (const bytes of ['AB\rC', 'A\r\nB', 'AB\x08C', 'A\x1bD', 'ABC\x1bE', '\x1b[5B\x1b[2A',
        'AB\x1b7\x1b[5;5H\x1b8', 'ABCDEFGH\x1b[5`']) assert.ok(feeds.includes(bytes));
});

test('independent physical-cell and cursor expectations distinguish each effect', () => {
    const results = gradeTextCursor(good());
    assert.deepEqual(Object.keys(results), textCursorIds);
    assert.ok(Object.values(results).every(result => result.outcome === 'supported'));
    for (const id of textCursorIds) {
        const roles = good(); roles[id + ':result'].screen.cursorCol++;
        assert.equal(gradeTextCursor(roles)[id].outcome, 'unsupported', id);
    }
    for (const id of ['text.cr', 'text.newline', 'text.backspace', 'text.index', 'text.next-line', 'cursor.save-restore', 'cursor.hpa']) {
        const roles = good(); roles[id + ':result'].screen.lines[0] = 'wrong';
        assert.equal(gradeTextCursor(roles)[id].outcome, 'unsupported', id);
    }
    const ignored = good(); ignored['cursor.move.up:result'].screen.lines[9] = '';
    assert.equal(gradeTextCursor(ignored)['cursor.move.up'].outcome, 'unsupported');
});

test('bad setup, missing displacement, wrong controls and altered modes refuse grading', () => {
    for (const mutate of [
        r => { r['text.cr:setup'].screen.cursorCol = 1; },
        r => { r['save:displaced'].screen.cursorRow = 0; },
        r => { r['text.newline:setup'].screen.bufferState.insertMode = true; },
    ]) { const roles = good(); mutate(roles); assert.throws(() => gradeTextCursor(roles)); }
});

test('failed operations in independent controls are observed failures', () => {
    const save = good(); save['save:restored'].screen.cursorCol = 4;
    assert.equal(gradeTextCursor(save)['cursor.save-restore'].outcome, 'unsupported');
    const ind = good(); ind['control:ind'].screen.cursorCol = 0;
    assert.equal(gradeTextCursor(ind)['text.index'].outcome, 'unsupported');
});

function journal() {
    const id = 'a'.repeat(32), paneId = '11111111-1111-1111-1111-111111111111', barrier = '\x1b[?1;2c';
    const blank = structuredClone(baseline); blank.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: blank.cols - 1 };
    const bind = structuredClone(blank); bind.lines[0] = textCursorNonce + id; bind.cursorCol = bind.lines[0].length;
    const make = (screen, kind, index, cpr) => ({ index, kind, paneId, barrier: index ? barrier : null, screen, sha256: snapshotHash(screen), ...(cpr && { cpr }) });
    const frames = [make(blank, 'tc-start', 0), make(bind, 'tc-bind', 1)];
    const plan = textCursorPlan(), roles = good(), reads = [];
    for (const step of plan.filter(x => x.read)) {
        const screen = roles[step.role].screen;
        const row = screen.cursorRow + 1, col = screen.cursorCol + 1;
        const cpr = { barrier, reply: { raw: `\x1b[${row};${col}R`, row, col } };
        reads.push({ role: step.role, cpr }); frames.push(make(screen, 'tc-read', frames.length, cpr));
    }
    frames.push(make(structuredClone(blank), 'tc-finish', frames.length));
    const feeds = plan.filter(x => x.feed).map(x => x.feed);
    return { id, frames, run: { suiteId: textCursorSuiteId, runId: id, feeds, barriers: feeds.map(() => barrier), reads } };
}

test('journal rederivation rejects altered bytes, stale frames, CPR and incomplete restoration', () => {
    const { id, frames, run } = journal();
    assert.equal(buildTextCursorReport(frames, run, id).cases.length, 8);
    const bytes = structuredClone(run); bytes.feeds[1] = 'AB\r';
    assert.throws(() => buildTextCursorReport(frames, bytes, id), /fixture bytes/);
    assert.throws(() => buildTextCursorReport(frames.slice(0, -1), run, id), /Incomplete/);
    assert.throws(() => buildTextCursorReport(frames, run, 'b'.repeat(32)), /identity/);
    const stale = structuredClone(frames); stale[3].screen.cursorCol++;
    assert.throws(() => buildTextCursorReport(stale, run, id), /snapshot/);
    const changed = structuredClone(frames); changed.at(-1).screen.cursorCol++; changed.at(-1).sha256 = snapshotHash(changed.at(-1).screen);
    assert.throws(() => buildTextCursorReport(changed, run, id), /restored/);
    const wrong = structuredClone(run); wrong.reads[0].cpr.reply.raw = '\x1b[2;1R';
    assert.throws(() => buildTextCursorReport(frames, wrong, id), /CPR changed/);
});

test('collector serializes whole fixture writes before CPR and restores after refusal', async () => {
    const events = [], barrier = '\x1b[?1;2c';
    const fence = { write: async bytes => { events.push(['write', bytes]); return barrier; },
        cursorReport: async () => { events.push(['cpr']); return { barrier, reply: { raw: '\x1b[1;1R', row: 1, col: 1 } }; } };
    let reads = 0;
    const owner = async kind => { events.push([kind]); if (kind === 'tc-read' && ++reads === 2) throw new Error('refused'); return { screen: baseline }; };
    await assert.rejects(collectTextCursor({ config: { runId: 'a'.repeat(32) }, fence, owner }), /refused/);
    const fixture = events.findIndex(event => event[0] === 'write' && event[1] === 'AB\rC');
    assert.ok(fixture > 0); assert.deepEqual(events[fixture + 1], ['cpr']);
    assert.equal(events.at(-1)[0], 'tc-finish');
    assert.equal(events.filter(event => event[0] === 'write' && event[1] === 'A\r\nB').length, 0);
});
