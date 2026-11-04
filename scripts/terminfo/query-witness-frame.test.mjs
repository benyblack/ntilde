import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inspectQueryWitness } from './query-witness-frame.mjs';
const context = { rows: 42, cols: 90, top: 3, bottom: 30 };
function capture(raw, nonces = [41001, 51002]) {
    const bytes = Buffer.isBuffer(raw) ? raw : Buffer.from(raw, 'ascii');
    return { hex: bytes.toString('hex'), byteLength: bytes.length,
        barrier: nonces.map(n => `\x1b[?${n};0$y`).join(''), nonces };
}
const positives = [
    ['q07.device.tertiary-da', '\x1bP!|00000000\x1b\\'],
    ['q07.device.xtwinops-18', '\x1b[8;42;90t'],
    ['q12.decrqss.vertical-margins', '\x1bP1$r3;30r\x1b\\'],
];
for (const [id, reply] of positives) {
    test('only exact independently supplied bytes satisfy ' + id, () => {
        const good = inspectQueryWitness(context, id, capture(reply));
        assert.equal(good.contractMatched, true);
        assert.equal(good.expectedEvidence, 'positive-reply');
        for (const bad of ['', reply + reply, reply + 'x', '\x1b[?62;4;22c', reply.slice(1),
            reply.slice(0, -1), Buffer.concat([Buffer.from(reply), Buffer.from([0x80])])]) {
            assert.equal(inspectQueryWitness(context, id, capture(bad)).contractMatched, false);
        }
    });
}
test('actual geometry and absolute margins must match, including a one-row terminal', () => {
    assert.equal(inspectQueryWitness(context, 'q07.device.xtwinops-18', capture('\x1b[8;90;42t')).contractMatched, false);
    assert.equal(inspectQueryWitness(context, 'q12.decrqss.vertical-margins', capture('\x1bP1$r1;28r\x1b\\')).contractMatched, false);
    assert.equal(inspectQueryWitness({ rows: 1, cols: 9, top: 1, bottom: 1 }, 'q12.decrqss.vertical-margins', capture('\x1bP1$r1;1r\x1b\\')).contractMatched, true);
});
for (const [id, negative, other] of [
    ['q12.decrqss.sgr', '\x1bP0$r\x1b\\', '\x1bP1$r0m\x1b\\'],
    ['q12.xtgettcap.terminal-name', '\x1bP0+r\x1b\\', '\x1bP1+r544e=787465726d\x1b\\'],
]) {
    test('bounded rejection is distinct evidence, never positive support: ' + id, () => {
        const good = inspectQueryWitness(context, id, capture(negative));
        assert.equal(good.contractMatched, true);
        assert.equal(good.expectedEvidence, 'negative-reply');
        for (const bad of ['', other, negative.replace('0', '1'), negative + negative, '\x1b[?62;4;22c']) {
            assert.equal(inspectQueryWitness(context, id, capture(bad)).contractMatched, false);
        }
    });
}
test('completed empty raw capture is silence, while an unrelated DA is not a feature reply', () => {
    const id = 'q07.device.xtwinops-21';
    const result = inspectQueryWitness(context, id, capture(''));
    assert.equal(result.contractMatched, true);
    assert.equal(result.expectedEvidence, 'silence');
    assert.equal(result.observedEvidence, 'silence');
    assert.equal(Object.hasOwn(result, 'supported'), false);
    const missingPositive = inspectQueryWitness(context, 'q07.device.tertiary-da', capture(''));
    assert.equal(missingPositive.expectedEvidence, 'positive-reply');
    assert.equal(missingPositive.observedEvidence, 'silence');
    assert.equal(missingPositive.contractMatched, false);
    assert.equal(inspectQueryWitness(context, id, capture('\x1b[?62;4;22c')).contractMatched, false);
    assert.equal(inspectQueryWitness(context, id, capture('\x1b]lcaption\x1b\\')).contractMatched, false);
});
test('bad capture facts cannot become semantic replies or silent successes', () => {
    const id = 'q07.device.tertiary-da', good = capture('\x1bP!|00000000\x1b\\');
    for (const value of [null, {}, { ...good, hex: '1b8' }, { ...good, hex: good.hex.toUpperCase() },
        { ...good, hex: '00'.repeat(513), byteLength: 513 }, { ...good, byteLength: 999 },
        { ...good, byteLength: String(good.byteLength) }, { ...good, barrier: '' },
        { ...good, barrier: good.barrier + 'x' }, { ...good, nonces: [41001, 41001] },
        { ...good, nonces: [1, 51002] }, { ...good, nonces: [41001, 65536] },
        { ...good, nonces: [41001, 51002, 60000] }, { ...good, nonces: [41001, NaN] },
        { ...good, nonces: [41001, ,], barrier: '\x1b[?41001;0$y' }, { ...good, extra: undefined }]) {
        assert.throws(() => inspectQueryWitness(context, id, value));
    }
    assert.throws(() => inspectQueryWitness(context, id + '.unknown', good));
});
test('byte evidence and challenge facts are immutable copies', () => {
    const fact = capture(''), result = inspectQueryWitness(context, 'q07.device.xtwinops-21', fact);
    fact.nonces[0] = 60000;
    assert.deepEqual(result.orderingNonces, [41001, 51002]);
    assert.equal(Object.isFrozen(result), true);
    assert.equal(Object.isFrozen(result.orderingNonces), true);
});

test('changing raw-fact and nonce getters cannot alter a validated comparison or returned evidence', () => {
    const original = capture('\x1bP!|00000000\x1b\\'), reads = {};
    const fact = {};
    for (const key of Object.keys(original)) Object.defineProperty(fact, key, { enumerable: true,
        get() { reads[key] = (reads[key] ?? 0) + 1; return reads[key] === 1 ? original[key] : null; } });
    let nonceReads = 0;
    Object.defineProperty(original.nonces, 0, { enumerable: true,
        get() { return ++nonceReads === 1 ? 41001 : 0; } });
    const result = inspectQueryWitness(context, 'q07.device.tertiary-da', fact);
    assert.equal(result.contractMatched, true);
    assert.equal(result.receivedHex, original.hex);
    assert.equal(result.byteLength, original.byteLength);
    assert.deepEqual(result.orderingNonces, [41001, 51002]);
    assert.deepEqual(reads, { hex: 1, byteLength: 1, barrier: 1, nonces: 1 });
    assert.equal(nonceReads, 1);
});
