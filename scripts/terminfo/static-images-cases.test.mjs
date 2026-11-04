import test from 'node:test';
import assert from 'node:assert/strict';
import { staticImageCases, staticImageCommand, staticKittyReply, parseStaticKittyReply } from './static-images-cases.mjs';

const reply = value => '\x1b_G' + value + '\x1b\\';
test('finite planned stages generate bounded deterministic protocol bytes without any terminal I/O', () => {
    assert.equal(staticImageCases.length, 5);
    assert.equal(new Set(staticImageCases.map(item => item.id)).size, 5);
    for (const item of staticImageCases) for (const stage of item.stages) {
        const bytes = staticImageCommand(item.id, stage.id, { number: 137, firstId: 99 });
        assert.equal(bytes, staticImageCommand(item.id, stage.id, { number: 137, firstId: 99 }));
        assert.ok(Buffer.byteLength(bytes) < 2048);
    }
    const stretch = staticImageCommand('iterm.explicit-stretch', 'show');
    assert.ok(stretch.includes(';preserveAspectRatio=0:'));
    assert.ok(!staticImageCommand('iterm.default-aspect', 'show').includes('preserveAspectRatio='));
    assert.ok(staticImageCommand('kitty.numbered-lifecycle', 'store', { number: 137 }).startsWith('\x1b_Ga=t,'));
    assert.equal(staticImageCommand('kitty.numbered-lifecycle', 'place', { number: 137 }), reply('a=p,I=137,p=1,c=4,r=2,C=1'));
    assert.equal(staticImageCommand('kitty.numbered-lifecycle', 'soft-delete', { number: 137 }), reply('a=d,d=n,I=137'));
    assert.equal(staticImageCommand('kitty.numbered-lifecycle', 'hard-delete', { number: 137 }), reply('a=d,d=N,I=137'));
    assert.notEqual(staticImageCommand('kitty.number-reuse', 'store-first', { number: 137 }), staticImageCommand('kitty.number-reuse', 'store-second', { number: 137 }));
});

test('plan refuses unknown stages, zero/overflow/implicit ids and old-id commands without a real allocation', () => {
    assert.throws(() => staticImageCommand('unknown', 'store'));
    assert.throws(() => staticImageCommand('kitty.numbered-lifecycle', 'animation'));
    for (const number of [undefined, 0, -1, 1.1, '137', 4294967296]) assert.throws(() => staticImageCommand('kitty.numbered-lifecycle', 'store', { number }));
    assert.throws(() => staticImageCommand('kitty.number-reuse', 'place-old-id', { number: 137 }));
});

test('number allocations, placements, old-id controls and missing replies correlate without invented ids', () => {
    assert.equal(staticKittyReply(reply('i=99,I=137;OK'), { number: 137, allocation: true }).id, 99);
    assert.equal(staticKittyReply(reply('i=100,I=137;OK'), { number: 137, allocation: true, seenIds: [99] }).id, 100);
    assert.equal(staticKittyReply(reply('i=100,I=137,p=1;OK'), { number: 137, expectedId: 100, placement: 1 }).placement, 1);
    assert.equal(staticKittyReply(reply('i=99,p=1;OK'), { expectedId: 99, placement: 1 }).id, 99);
    assert.equal(staticKittyReply(reply('I=137,p=1;ENOENT:Image not found'), { number: 137, placement: 1, missing: true }).id, null);
    assert.equal(staticKittyReply(reply('i=4294967295,I=137;OK'), { number: 137, allocation: true }).id, 4294967295);
});

for (const [name, raw, context] of [
    ['reused allocation id', 'i=99,I=137;OK', { number: 137, allocation: true, seenIds: [99] }],
    ['wrong number', 'i=99,I=138;OK', { number: 137, allocation: true }],
    ['wrong placement', 'i=99,I=137,p=2;OK', { number: 137, placement: 1 }],
    ['wrong id', 'i=98,I=137,p=1;OK', { number: 137, expectedId: 99, placement: 1 }],
    ['missing allocation', 'I=137;OK', { number: 137, allocation: true }],
    ['duplicate key', 'i=99,I=137,I=137;OK', { number: 137 }],
    ['overflow', 'i=4294967296,I=137;OK', { number: 137 }],
    ['unknown field', 'i=99,I=137,x=1;OK', { number: 137 }],
    ['stale successful status after deletion', 'I=137,p=1;OK', { number: 137, placement: 1, missing: true }],
    ['error instead of success', 'i=99,I=137;EINVAL:bad', { number: 137 }],
]) test(`reply rejects ${name}`, () => assert.throws(() => staticKittyReply(reply(raw), context)));

test('reply parser does not accept batches, trailing data, controls or oversized responses', () => {
    for (const raw of [reply('i=99,I=137;OK') + reply('i=99,I=137;OK'), reply('i=99,I=137;OK') + 'tail', reply('i=99,I=137;OK\rINJECT'), 'x'.repeat(513)]) {
        assert.throws(() => staticKittyReply(raw, { number: 137 }));
    }
});
test('complete Kitty framing rejects line terminators both before and after ST', () => {
    for (const tail of ['\n', '\r', '\r\n', '\u2028', '\u2029']) {
        for (const raw of [reply('i=99,I=137;OK') + tail, reply('i=99,I=137;OK' + tail)])
            assert.throws(() => staticKittyReply(raw, { number: 137, allocation: true }));
    }
});
test('each numeric Kitty field consumes its complete spelling before comma or semicolon', () => {
    for (const tail of ['\n', '\r', '\r\n', '\u2028', '\u2029']) for (const field of ['i', 'I', 'p']) {
        const raw = reply(`i=99${field === 'i' ? tail : ''},I=137${field === 'I' ? tail : ''},p=1${field === 'p' ? tail : ''};OK`);
        assert.throws(() => staticKittyReply(raw, { number: 137, expectedId: 99, placement: 1 }));
    }
});
test('actual numbered missing-placement error retains the unresolved zero image-id echo', () => {
    // AnsiParser.KittyLifecycle.cs ReplyKittyCommand emits i unconditionally.
    const raw = reply('i=0,I=137,p=1;ENOENT:Image not found');
    assert.equal(staticKittyReply(raw, { number: 137, placement: 1, missing: true }).id, 0);
});
test('the zero error echo cannot become a successful ID, zero I/p, or noncanonical decimal spelling', () => {
    for (const value of ['i=0,I=137;OK', 'i=0,I=0;ENOENT:missing', 'i=0,I=137,p=0;ENOENT:missing',
        'i=00,I=137;ENOENT:missing', 'i=0,I=137;unknown status', 'i=0;ENOENT:missing']) assert.throws(() => parseStaticKittyReply(reply(value)));
    assert.throws(() => staticKittyReply(reply('i=0,p=1;ENOENT:missing'), { expectedId: 99, placement: 1, missing: true }));
});
