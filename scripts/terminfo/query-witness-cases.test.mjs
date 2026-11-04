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
    assert.deepEqual(decrqss.map(x => x.request).sort(), ['m', 's', ' q', '"p', '"q', 't', '$|', '*|', '*x', 'unknown', '']
        .map(s => '\x1bP$q' + s + '\x1b\\').sort());
    for (const spec of decrqss) assert.equal(spec.expectedReply, '\x1bP0$r\x1b\\');
    const xtgettcap = plan.filter(x => x.id.startsWith('q12.xtgettcap.'));
    assert.deepEqual(xtgettcap.map(x => x.request).sort(), ['544e', '436f', '636f6c6f7273', '524742', '6b63757531',
        '544e;436f', '6e6f742d612d6361706162696c697479', '5', 'zz', ''].map(s => '\x1bP+q' + s + '\x1b\\').sort());
    for (const spec of xtgettcap) assert.equal(spec.expectedReply, '\x1bP0+r\x1b\\');
});
