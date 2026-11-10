import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { queryWitnessContext, queryWitnessPlan } from './query-witness-cases.mjs';
const context = { rows: 42, cols: 90, top: 3, bottom: 30 };
test('finite plan covers each frozen Q07 observation once, plus every audited Q12 setting', () => {
    const frozen = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-02-inconclusive-triage.json', import.meta.url)));
    const plan = queryWitnessPlan(context);
    assert.equal(plan.length, 40);
    assert.equal(new Set(plan.map(x => x.id)).size, 40);
    const q07 = frozen.observations.filter(x => x.action === 'Q07').map(x => x.original.featureId).sort();
    assert.equal(q07.length, 18);
    assert.deepEqual(plan.filter(x => x.id.startsWith('q07.')).map(x => x.featureId).sort(), q07);
    assert.equal(plan.filter(x => x.expectedEvidence === 'silence').length, 16);
    assert.equal(plan.filter(x => x.expectedEvidence === 'negative-reply').length, 21);
    assert.equal(plan.filter(x => x.expectedEvidence === 'positive-reply').length, 3);
    assert.equal(Object.isFrozen(plan), true);
    for (const item of plan) {
        assert.equal(Object.isFrozen(item), true);
        assert.ok(Buffer.byteLength(item.request, 'ascii') <= 64);
        assert.ok(!/[\x80-\uffff]/.test(item.request));
    }
});
test('independent literal spelling controls preserve query families, case and terminators', () => {
    const plan = new Map(queryWitnessPlan(context).map(x => [x.id, x]));
    const literal = [
        ['q07.device.tertiary-da', '1b5b3d63'],
        ['q07.device.xtwinops-18', '1b5b313874'],
        ['q12.decrqss.vertical-margins', '1b502471721b5c'],
        ['q07.device.xtgetxres', '1b502b51373436353732366434653631366436351b5c'],
        ['q07.device.xtreportcolors', '1b5b2352'],
        ['q07.extensions.sixel-geometry-report', '1b5b3f323b313b3053'],
        ['q07.extensions.osc1337-capabilities', '1b5d313333373b526571756573744361706162696c697469657307'],
        ['q07.extensions.osc21-kitty-color', '1b5d32313b666f726567726f756e643d3f1b5c'],
        ['q12.decrqss.cursor-style', '1b50247120711b5c'],
        ['q12.decrqss.conformance', '1b50247122701b5c'],
        ['q12.xtgettcap.list', '1b502b71353434653b343336661b5c'],
        ['q12.xtgettcap.empty', '1b502b711b5c'],
    ];
    for (const [id, hex] of literal) assert.equal(Buffer.from(plan.get(id).request, 'ascii').toString('hex'), hex, id);
});
test('geometry/margins are observed bounded integers, never environment guesses', () => {
    for (const value of [null, {}, { ...context, cols: 0 }, { ...context, rows: 4097 },
        { ...context, rows: '42' }, { ...context, top: 0 }, { ...context, top: 31 },
        { ...context, bottom: 43 }, { ...context, cols: NaN }, { ...context, rows: 1.5 },
        { ...context, ignored: 1 }, Object.assign(Object.create({ rows: 42 }), { cols: 90, top: 3, bottom: 30 })]) {
        assert.throws(() => queryWitnessContext(value));
    }
    assert.deepEqual(queryWitnessContext({ rows: 1, cols: 1, top: 1, bottom: 1 }), { rows: 1, cols: 1, top: 1, bottom: 1 });
    const source = { ...context }, copied = queryWitnessContext(source);
    source.rows = 1;
    assert.equal(copied.rows, 42);
    assert.equal(Object.isFrozen(copied), true);
});

test('all individually audited Q12 requests retain their exact payload and rejection contract', () => {
    const plan = queryWitnessPlan(context);
    const decrqss = plan.filter(x => x.id.startsWith('q12.decrqss.') && x.id !== 'q12.decrqss.vertical-margins');
    assert.deepEqual(Object.fromEntries(decrqss.map(x => [x.id, x.request])), {
        'q12.decrqss.sgr': '\x1bP$qm\x1b\\',
        'q12.decrqss.horizontal-margins': '\x1bP$qs\x1b\\',
        'q12.decrqss.cursor-style': '\x1bP$q q\x1b\\',
        'q12.decrqss.conformance': '\x1bP$q"p\x1b\\',
        'q12.decrqss.protection': '\x1bP$q"q\x1b\\',
        'q12.decrqss.lines-per-page': '\x1bP$qt\x1b\\',
        'q12.decrqss.columns-per-page': '\x1bP$q$|\x1b\\',
        'q12.decrqss.number-lines': '\x1bP$q*|\x1b\\',
        'q12.decrqss.change-extent': '\x1bP$q*x\x1b\\',
        'q12.decrqss.unknown': '\x1bP$qunknown\x1b\\',
        'q12.decrqss.empty': '\x1bP$q\x1b\\',
    });
    for (const spec of decrqss) assert.equal(spec.expectedReply, '\x1bP0$r\x1b\\');
    const xtgettcap = plan.filter(x => x.id.startsWith('q12.xtgettcap.'));
    assert.deepEqual(Object.fromEntries(xtgettcap.map(x => [x.id, x.request])), {
        'q12.xtgettcap.terminal-name': '\x1bP+q544e\x1b\\',
        'q12.xtgettcap.color-count': '\x1bP+q436f\x1b\\',
        'q12.xtgettcap.colors': '\x1bP+q636f6c6f7273\x1b\\',
        'q12.xtgettcap.rgb': '\x1bP+q524742\x1b\\',
        'q12.xtgettcap.cursor-up-key': '\x1bP+q6b63757531\x1b\\',
        'q12.xtgettcap.list': '\x1bP+q544e;436f\x1b\\',
        'q12.xtgettcap.unknown': '\x1bP+q6e6f742d612d6361706162696c697479\x1b\\',
        'q12.xtgettcap.odd-hex': '\x1bP+q5\x1b\\',
        'q12.xtgettcap.invalid-hex': '\x1bP+qzz\x1b\\',
        'q12.xtgettcap.empty': '\x1bP+q\x1b\\',
    });
    for (const spec of xtgettcap) assert.equal(spec.expectedReply, '\x1bP0+r\x1b\\');
});

test('changing context getters are read once and only the validated snapshot is used', () => {
    const reads = { rows: 0, cols: 0, top: 0, bottom: 0 };
    const value = Object.fromEntries(Object.entries(context));
    for (const key of Object.keys(reads)) Object.defineProperty(value, key, { enumerable: true,
        get() { return ++reads[key] === 1 ? context[key] : 0; } });
    assert.equal(queryWitnessPlan(value).find(x => x.id === 'q07.device.xtwinops-18').expectedReply, '\x1b[8;42;90t');
    assert.deepEqual(reads, { rows: 1, cols: 1, top: 1, bottom: 1 });
    let attempts = 0;
    const invalid = { ...context, get rows() { return ++attempts === 1 ? 0 : 42; } };
    assert.throws(() => queryWitnessContext(invalid));
    assert.equal(attempts, 1);
});

test('a run that turns the off-by-default DECRQCRA report on owes the exact checksum of the bound N; every other witness is unchanged', () => {
    const plain = queryWitnessPlan(context), flagged = queryWitnessPlan({ ...context, checksumReport: true });
    const witness = plan => plan.find(item => item.id === 'q07.editing.decrqcra');
    assert.deepEqual({ ...witness(plain) }, { id: 'q07.editing.decrqcra', featureId: 'editing.decrqcra', request: '\x1b[1;1;1;1;1;1*y', expectedEvidence: 'silence', expectedReply: '' });
    assert.deepEqual({ ...witness(flagged) }, { ...witness(plain), expectedEvidence: 'positive-reply', expectedReply: '\x1bP1!~FFB2\x1b\\' });
    // N is 0x4E: the negated 16-bit sum is 0x10000 - 0x4E = 0xFFB2.
    assert.equal((0x10000 - 'N'.charCodeAt(0)).toString(16).toUpperCase(), 'FFB2');
    assert.deepEqual(flagged.filter(item => item.id !== 'q07.editing.decrqcra').map(item => ({ ...item })), plain.filter(item => item.id !== 'q07.editing.decrqcra').map(item => ({ ...item })));
    assert.equal(flagged.filter(item => item.expectedEvidence === 'silence').length, 15);
    assert.equal(queryWitnessContext({ ...context, checksumReport: true }).checksumReport, true);
    assert.equal(Object.hasOwn(queryWitnessContext(context), 'checksumReport'), false, 'an unflagged context stays exactly the four numbers');
    for (const bad of [{ ...context, checksumReport: false }, { ...context, checksumReport: 'yes' }, { ...context, checksumReport: true, extra: 1 }]) {
        assert.throws(() => queryWitnessContext(bad), /bounded observed geometry/, JSON.stringify(bad));
    }
});

test('a run of the v2 query policy owes the exact DECRQSS s reply; absent the marker the negative expectation is unchanged', () => {
    const plain = queryWitnessPlan(context), marked = queryWitnessPlan({ ...context, marginsReport: true });
    const witness = plan => plan.find(item => item.id === 'q12.decrqss.horizontal-margins');
    assert.deepEqual({ ...witness(plain) }, { id: 'q12.decrqss.horizontal-margins', featureId: 'device.decrqss', request: '\x1bP$qs\x1b\\',
        expectedEvidence: 'negative-reply', expectedReply: '\x1bP0$r\x1b\\' });
    // The query suite runs on a reset full-width screen: left 1, right cols (90 here), not the vertical margins.
    assert.deepEqual({ ...witness(marked) }, { ...witness(plain), expectedEvidence: 'positive-reply', expectedReply: '\x1bP1$r1;90s\x1b\\' });
    const others = plan => plan.filter(item => item.id !== 'q12.decrqss.horizontal-margins').map(item => ({ ...item }));
    assert.deepEqual(others(marked), others(plain), 'every other witness, including the other decrqss ones, is unchanged');
    assert.equal(marked.filter(item => item.expectedEvidence === 'negative-reply').length, 20);
    assert.equal(marked.filter(item => item.expectedEvidence === 'positive-reply').length, 4);
    const both = queryWitnessPlan({ ...context, checksumReport: true, marginsReport: true });
    assert.equal(both.find(item => item.id === 'q07.editing.decrqcra').expectedEvidence, 'positive-reply');
    assert.equal(both.find(item => item.id === 'q12.decrqss.horizontal-margins').expectedEvidence, 'positive-reply');
    assert.equal(queryWitnessContext({ ...context, marginsReport: true }).marginsReport, true);
    assert.equal(Object.hasOwn(queryWitnessContext(context), 'marginsReport'), false);
    for (const bad of [{ ...context, marginsReport: false }, { ...context, marginsReport: 1 }, { ...context, marginsReport: 'true' }, { ...context, marginsReport: undefined },
        { ...context, marginsReport: true, extra: 1 }]) {
        assert.throws(() => queryWitnessContext(bad), /bounded observed geometry/, JSON.stringify(bad));
    }
});
