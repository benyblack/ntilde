import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseColorReply, parseQueryFrame, querySequence, querySpec } from './color-query.mjs';
const da = '\x1b[?62;4;22c';
const osc = value => '\x1b]' + value + '\x1b\\';
test('only explicit non-acting color queries can be serialized', () => {
    assert.equal(querySequence({ code: 17 }), osc('17;?'));
    assert.equal(querySequence({ code: 4, index: 200 }), osc('4;200;?'));
    for (const spec of [null, {}, { code: 52 }, { code: 17, index: 0 }, { code: 4, index: -1 }, { code: 4, index: 256 }, { code: 17, acting: true }]) assert.throws(() => querySpec(spec));
});
test('DA proves absence without a wall-clock no-reply inference', () => assert.deepEqual(parseQueryFrame(da, { code: 19 }), { barrier: da, reply: null }));
for (const spec of [{ code: 4, index: 200 }, { code: 10 }, { code: 11 }, { code: 12 }, { code: 17 }, { code: 19 }]) {
    const prefix = `${spec.code};${spec.code === 4 ? spec.index + ';' : ''}`;
    const raw = osc(prefix + 'rgb:1212/3434/5656'), full = raw + da;
    test('partial frames never complete early for code ' + spec.code, () => {
        for (let end = 0; end < full.length; end++) assert.equal(parseQueryFrame(full.slice(0, end), spec), null, String(end));
        assert.deepEqual(parseQueryFrame(full, spec), { barrier: da, reply: { raw, channels16: [0x1212, 0x3434, 0x5656] } });
    });
}
test('preserves 16-bit evidence without quantizing a mismatched response into an 8-bit success', () => {
    assert.deepEqual(parseColorReply(osc('17;rgb:1234/5678/9abc'), { code: 17 }).channels16, [0x1234, 0x5678, 0x9abc]);
});
for (const value of [osc('19;rgb:1212/3434/5656') + da, osc('4;1;rgb:1212/3434/5656') + da,
    osc('17;rgb:12/3434/5656') + da, osc('17;rgb:gggg/3434/5656') + da,
    osc('17;rgb:1212/3434/5656') + osc('17;rgb:1212/3434/5656') + da,
    da + 'extra', 'typed input', '\x1b]17;rgb:1212/3434/5656\x07' + da, 'x'.repeat(513)]) {
    test('refuses malformed, mismatched, duplicate or trailing replies ' + JSON.stringify(value.slice(0, 28)), () => assert.throws(() => parseQueryFrame(value, { code: 17 })));
}
