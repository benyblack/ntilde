import test from 'node:test';
import assert from 'node:assert/strict';
import { modeReportQuery, modeReportExpectation, parseModeReport, parseModeReportFrame, validateModeReport } from './mode-report.mjs';

const da = '\x1b[?62;4c';

test('the request is DECRQM for exactly the named private mode', () => {
    assert.equal(modeReportQuery(5), '\x1b[?5$p');
    assert.equal(modeReportQuery(2026), '\x1b[?2026$p');
    for (const bad of [0, -1, 10000, 1.5, '5', null, undefined, NaN]) assert.throws(() => modeReportQuery(bad), /Invalid owned mode report/);
    assert.deepEqual(modeReportExpectation(5), { kind: 'mode-report', mode: 5 });
    assert.throws(() => modeReportExpectation(0), /Invalid/);
});
test('a complete DECRPM reply parses with its mode and status, and any other shape is refused', () => {
    assert.deepEqual(parseModeReport('\x1b[?5;1$y', 5), { raw: '\x1b[?5;1$y', mode: 5, status: 1 });
    assert.deepEqual(parseModeReport('\x1b[?5;2$y', 5), { raw: '\x1b[?5;2$y', mode: 5, status: 2 });
    for (const [raw, mode, message] of [['\x1b[?7;1$y', 5, /different mode/], ['\x1b[?5;5$y', 5, /status/], ['\x1b[?5;9$y', 5, /status/],
        ['\x1b[5;1$y', 5, /Unexpected/], ['\x1b[?5;1$', 5, /Unexpected/], [da, 5, /Unexpected/], ['\x1b[?5;12$y', 5, /Unexpected/], ['', 5, /Unexpected/],
        ['\x1b[?5;1$y', 0, /Unexpected/], [undefined, 5, /Unexpected/]]) assert.throws(() => parseModeReport(raw, mode), message, JSON.stringify(raw));
});
test('an exchange is the DECRPM reply then the DA barrier; a DA fence reply is never a DECRPM reply', () => {
    assert.deepEqual(parseModeReportFrame('\x1b[?5;1$y' + da, 5), { barrier: da, reply: { raw: '\x1b[?5;1$y', mode: 5, status: 1 } });
    // A complete DA with no reply is an explicit absence: the barrier alone never stands in for a reply.
    assert.deepEqual(parseModeReportFrame(da, 5), { barrier: da, reply: null });
    for (const partial of ['', '\x1b', '\x1b[', '\x1b[?', '\x1b[?5', '\x1b[?5;', '\x1b[?5;1', '\x1b[?5;1$', '\x1b[?5;1$y', '\x1b[?5;1$y\x1b', '\x1b[?5;1$y\x1b[?62;']) {
        assert.equal(parseModeReportFrame(partial, 5), null, JSON.stringify(partial));
    }
    for (const [bytes, message] of [['\x1b[?7;1$y' + da, /different mode/], ['\x1b[?5;7$y' + da, /status/], ['\x1b[?5;1$y' + 'x', /barrier bytes/],
        ['\x1b[?5;1$y\x1b[?5;1$y' + da, /barrier bytes|different|Unexpected/], ['x' + da, /barrier bytes|Unexpected/], ['a'.repeat(200), /Oversized/]]) {
        assert.throws(() => parseModeReportFrame(bytes, 5), message, JSON.stringify(bytes));
    }
});
test('a retained exchange must be canonical, for one mode, with a matching raw reply and a DA barrier', () => {
    const good = { mode: 5, barrier: da, reply: { raw: '\x1b[?5;2$y', mode: 5, status: 2 } };
    assert.deepEqual(validateModeReport(good), good);
    assert.deepEqual(validateModeReport({ mode: 5, barrier: da, reply: null }), { mode: 5, barrier: da, reply: null });
    for (const bad of [null, {}, { ...good, mode: 7 }, { ...good, barrier: 'x' }, { ...good, reply: { raw: '\x1b[?5;1$y', mode: 5, status: 2 } },
        { ...good, reply: { raw: '\x1b[?5;2$y', mode: 7, status: 2 } }, { mode: 5, barrier: da }, { ...good, reply: { raw: da, mode: 5, status: 2 } },
        { ...good, reply: { raw: '\x1b[?7;2$y', mode: 7, status: 2 } }]) assert.throws(() => validateModeReport(bad), /Invalid retained|Unexpected|different|disagrees/);
});
