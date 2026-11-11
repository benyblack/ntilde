import test from 'node:test';
import { legacyPrefixSource } from './fixtures/private-clipboard-receipt.mjs';
import assert from 'node:assert/strict';
import { createCoreBackend, backendResetBytes, coreGapsIds, coreGapsPlan, coreGapsPinned, adaptCoreGapsCollector,
    coreGapContext, replayCoreGap, executeCorePlan } from './core-gaps.mjs';

test('plan execution waits for both the fenced write and read before starting the next API call', async () => {
    const events = [], records = [], roles = {};
    let finishWrite, finishRead;
    const plan = [{ feed: 'seed', role: 'before', api: null }, { feed: backendResetBytes, role: 'after', api: 'reset' }];
    const done = executeCorePlan(plan, {
        fence: { write: bytes => { events.push(bytes); return new Promise(resolve => { finishWrite = resolve; }); } },
        backend: { reset: async () => { events.push('reset'); return 'reset-barrier'; } },
        owner: (kind, barrier) => {
            events.push([kind, barrier]);
            return barrier === 'write-barrier' ? new Promise(resolve => { finishRead = resolve; }) : Promise.resolve('after-state');
        }, records, roles,
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(events, ['seed']);
    finishWrite('write-barrier');
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(events, ['seed', ['cg-read', 'write-barrier']]);
    finishRead('before-state');
    await done;
    assert.deepEqual(events, ['seed', ['cg-read', 'write-barrier'], 'reset', ['cg-read', 'reset-barrier']]);
    assert.deepEqual(roles, { before: 'before-state', after: 'after-state' });
    assert.deepEqual(records, plan.map((step, i) => ({ ...step, barrier: i ? 'reset-barrier' : 'write-barrier' })));
});

test('a refused observation prevents all subsequent writes and API calls', async () => {
    let resets = 0;
    await assert.rejects(executeCorePlan([{ feed: 'seed', role: 'before' }, { api: 'reset' }], {
        fence: { write: async () => 'barrier' }, backend: { reset: async () => { resets++; } },
        owner: async () => { throw new Error('read refused'); }, records: [], roles: {},
    }), /read refused/);
    assert.equal(resets, 0);
});

test('reset() awaits the real fenced API write and explicitly clears IRM after RIS', async () => {
    const writes = [], calls = [];
    let complete;
    const backend = createCoreBackend({ write: bytes => { writes.push(bytes); return new Promise(resolve => { complete = resolve; }); } }, calls);
    let done = false;
    const result = backend.reset().then(value => { done = true; return value; });
    assert.equal(done, false);
    assert.equal(writes[0], '\x1b[?1049l\x1bc\x1b[?6l\x1b[?7h\x1b[4l\x1b[?25h\x1b[r\x1b[0m\x1b[2J\x1b[3J\x1b[H');
    assert.deepEqual(calls, [{ method: 'reset', bytes: backendResetBytes }]);
    complete('barrier'); assert.equal(await result, 'barrier');
});

test('all four exact ids have separate plans and pinned code remains verbatim', () => {
    assert.deepEqual(coreGapsIds, ['text.reverse-index-scroll', 'reset.method', 'device.env-term', 'extensions.osc1-icon']);
    const plan = coreGapsPlan(40, 'TERM-run=xterm-256color');
    assert.equal(plan.filter(step => step.api === 'reset').length, 3);
    assert.equal(plan.filter(step => step.role === 'regionAfter').length, 1);
    assert.deepEqual(plan.slice(1, 7).map(s => s.feed), ['\x1b[1;5r', '\x1b[H', 'MARKER', '\x1b[H', '\x1bM', '\x1b[r']);
    const source = 'export const ntildeLrMarginsAdapter = Object.freeze({});\n' + Object.values(coreGapsPinned).join('\n');
    assert.ok(adaptCoreGapsCollector(source).startsWith(source));
    assert.throws(() => adaptCoreGapsCollector(source.replace('MARKER', 'CHANGED')), /pinned definition/);
    assert.throws(() => adaptCoreGapsCollector(source + coreGapsPinned['reset.method']), /pinned definition/);
});

test('the pinned OSC 1 callback stays diagnostic despite a title readback', () => {
    const roles = { iconBefore: { facts: { before: { oscTitle: 'window' } } }, iconPinned: { facts: { before: { oscTitle: 'window' } } } };
    const ctx = coreGapContext('extensions.osc1-icon', roles), result = replayCoreGap('extensions.osc1-icon', ctx);
    ctx.assertComplete(); assert.equal(result.pass, false);
    assert.deepEqual(result.notTested, { reason: 'no-semantic-observable', noObservable: 'icon-name state' });
    assert.equal(result.observation, undefined);
});

test('legacy synthetic prefix drops new suffix imports on LF and CRLF checkouts', () => {
    const source = "import { collectCoreGaps } from './core-gaps.mjs';\nimport { buildCoreGapsReport } from './terminfo/core-gaps.mjs';\nimport { includesCoreGapsEvidence } from './core-gaps-receipt.mjs';\nimport { retained } from './retained.mjs';\n";
    const expected = "import { retained } from './retained.mjs';\n";
    assert.equal(legacyPrefixSource(Buffer.from(source)).toString(), expected);
    assert.equal(legacyPrefixSource(Buffer.from(source.replaceAll('\n', '\r\n'))).toString(), expected);
});
