import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { stockClipboardCallback } from './fixtures/private-clipboard-callback.mjs';
import { preparePrivateClipboardCallback, preparePrivateClipboardSource, PRIVATE_CLIPBOARD_CALLBACK_SHA256 } from './private-clipboard-source.mjs';

const nonce = 'terminfo-osc52-11111111-1111-1111-1111-111111111111';
const packet = '\x1b]52;c;' + btoa(nonce) + '\x07';
const adapted = preparePrivateClipboardCallback(stockClipboardCallback);
const controls = `const randomUUID = () => ${JSON.stringify(nonce.slice(15))};\n`
    + String.raw`const osc52Reply = /\x1b\]52;c;([A-Za-z0-9+/=]*)(?:\x07|\x1b\\)/;` + '\n';
const { liveClipboardProbe: callback } = await import('data:text/javascript;base64,'
    + Buffer.from(controls + adapted + '\nexport { liveClipboardProbe };').toString('base64'));

test('callback source pin and original packet/grader are preserved around the sole awaited seam', () => {
    assert.equal(createHash('sha256').update(stockClipboardCallback).digest('hex'), PRIVATE_CLIPBOARD_CALLBACK_SHA256);
    const insertion = '      if (typeof fixture.beforeOsc52Write !== "function") throw new Error("Missing private clipboard preparation");\n'
        + '      await fixture.beforeOsc52Write(nonce);\n';
    assert.equal(adapted.replace(insertion, ''), stockClipboardCallback);
    assert.throws(() => preparePrivateClipboardCallback(stockClipboardCallback + '\n'), /pin mismatch/);
    assert.throws(() => preparePrivateClipboardSource(stockClipboardCallback), /source pin mismatch/);
});

test('zero terminal bytes or cursor queries before nonce-arm acknowledgment; exact packet then query', async () => {
    let release, armed;
    const ack = new Promise(resolve => { release = resolve; });
    const events = [];
    const running = callback({ withClipboardFixture: work => work({
        beforeOsc52Write: async value => { armed = value; await ack; }, readText: async () => nonce,
    }), write: value => events.push(value), queryCursorPosition: async () => { events.push('cursor'); return {}; } }, 'write');
    await Promise.resolve();
    assert.equal(armed, nonce); assert.deepEqual(events, []);
    release(); const result = await running;
    assert.deepEqual(events, [packet, 'cursor']); assert.equal(result.pass, true);
});

for (const fixture of [{}, { beforeOsc52Write: async () => { throw new Error('arm refused'); } }]) {
    test('missing or failed preparation sends no packet or cursor query', async () => {
        const events = [];
        await assert.rejects(callback({ withClipboardFixture: work => work(fixture),
            write: value => events.push(value), queryCursorPosition: () => events.push('cursor') }, 'write'));
        assert.deepEqual(events, []);
    });
}

test('ordinary stock context stays policy-refused with zero terminal output', async () => {
    let writes = 0;
    const result = await callback({ write: () => writes++ }, 'write');
    assert.equal(writes, 0); assert.equal(result.pass, false); assert.equal(result.observation.reason, 'policy-refused');
});

test('independently seeded private value cannot convert the original empty OSC52 denial into support', async () => {
    let seeded, query;
    const result = await callback({ withClipboardFixture: work => work({ writeText: async value => { seeded = value; } }),
        queryOutcome: async value => { query = value; return { raw: '\x1b]52;c;\x07', match: true, reason: 'reply' }; } }, 'read');
    assert.equal(seeded, nonce); assert.equal(query, '\x1b]52;c;?\x07');
    assert.equal(result.pass, false); assert.equal(result.observation.reason, 'insufficient-evidence');
});
