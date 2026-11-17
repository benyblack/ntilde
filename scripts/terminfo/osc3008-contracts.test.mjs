import assert from 'node:assert/strict';
import test from 'node:test';
import { makeOsc3008Experiments, osc3008CaseId, osc3008Limits, osc3008PinnedWrites, replayOsc3008PinnedStatic, simulateContexts, specExample, withoutContextSequences } from './osc3008-contracts.mjs';

const OSC = '\x1b]3008;', ST = '\x1b\\';
const start = (id, fields = '') => `${OSC}start=${id}${fields}${ST}`;
const end = (id, fields = '') => `${OSC}end=${id}${fields}${ST}`;
const ids = feeds => simulateContexts(feeds).stack.map(item => item.id);
const runId = '0123456789abcdef0123456789abcdef';

// The oracle is checked against the specification's own text (UAPI.15 v1.0), never against the App.
test('the specification example opens one context and its end closes it', () => {
    const feeds = [start(specExample.id, specExample.fields.map(field => `;${field}`).join(''))];
    const { stack } = simulateContexts(feeds);
    assert.deepEqual(stack, [{ id: 'bed86fab93af4328bbed0a1224af6d40', type: 'container', user: 'lennart', hostname: 'zeta', machineId: '3deb5353d3ba43d08201c136a47ead7b',
        bootId: 'd4a3d0fdf2e24fdea6d971ce73f4fbf2', pid: '1062862', pidFdId: '1063162', comm: 'systemd-nspawn', container: 'foobar' }]);
    assert.deepEqual(simulateContexts([...feeds, end(specExample.id)]).stack, []);
});

test('contexts nest, an update flushes metadata and ends sub-contexts, and an unknown end is ignored', () => {
    assert.deepEqual(ids([start('a'), start('b'), start('c')]), ['a', 'b', 'c']);
    const updated = simulateContexts([start('a', ';user=old'), start('b'), start('a', ';comm=new')]);
    assert.deepEqual(updated.stack, [{ id: 'a', comm: 'new' }]);
    assert.deepEqual(ids([start('a'), end('zzz')]), ['a']);
    assert.deepEqual(ids([start('a'), start('b'), end('a')]), []);
});

test('context ids are one to sixty-four printable ASCII characters', () => {
    assert.deepEqual(ids([start('k'.repeat(64)), start('k'.repeat(65)), start(''), start('café'), start('tab\there'), start('del\x7f')]), ['k'.repeat(64)]);
    assert.deepEqual(ids([start('with space'), start('semi\\x3bcolon'), start('bad\\id')]), ['with space', 'semi;colon']);
    assert.deepEqual(ids([start('x'.repeat(61) + '\\x3b\\x5c\\x3b\\x5c')]), [], 'the bound counts decoded characters');
});

test('invalid and unknown fields are ignored and the rest is kept', () => {
    const { stack } = simulateContexts([start('a', ';nonsense=1;pid=abc;type=hacker;user=;hostname=ok;pidfdid=18446744073709551616;machineid=short;bootid=' + 'a'.repeat(37) + ';cwd=' + 'd'.repeat(256) + ';cmdline=')]);
    assert.deepEqual(stack, [{ id: 'a', hostname: 'ok', cmdline: '' }]);
    assert.deepEqual(simulateContexts([start('a', ';pid=18446744073709551615;machineid=3deb5353-d3ba-43d0-8201-c136a47ead7b;cwd=' + 'd'.repeat(255))]).stack[0].pid, '18446744073709551615');
});

test('escapes decode and a repeated field keeps its last valid value', () => {
    assert.deepEqual(simulateContexts([start('a', ';cwd=/a\\x3bb\\x5cc;user=first;user=;user=last;comm=bad\\q')]).stack, [{ id: 'a', cwd: '/a;b\\c', user: 'last' }]);
});

test('end metadata keeps only valid exit, status and signal, and records the context type', () => {
    const { ended } = simulateContexts([start('a', ';type=command'), end('a', ';exit=crash;status=139;signal=SIGSEGV;user=x'), start('b'), end('b', ';exit=boom;status=256;signal=segv')]);
    assert.deepEqual(ended, [{ id: 'a', type: 'command', exit: 'crash', status: '139', signal: 'SIGSEGV' }, { id: 'b' }]);
});

test('the depth and ended bounds keep the earlier contexts and the newest records', () => {
    const feeds = Array.from({ length: osc3008Limits.depth + 4 }, (_, index) => start(`c${index}`));
    assert.deepEqual(ids(feeds), Array.from({ length: osc3008Limits.depth }, (_, index) => `c${index}`));
    const many = Array.from({ length: 11 }, (_, index) => [start(`e${index}`), end(`e${index}`)]).flat();
    assert.deepEqual(simulateContexts(many).ended.map(item => item.id), Array.from({ length: osc3008Limits.ended }, (_, index) => `e${index + 3}`));
});

test('terminal reset sequences and foreign spellings do not change the stack', () => {
    const feeds = [start('a'), '\x1b[!p', '\x1bc', `\x1b]3008;type=test\x07`, `${OSC}start${ST}`, `${OSC}begin=x${ST}`, `\x1b]3007;start=x${ST}`, `\x1b]133;start=x${ST}`];
    assert.deepEqual(ids(feeds), ['a']);
});

test('the pinned fixture is not a start or an end, and its replay depends on the cursor alone', () => {
    assert.deepEqual(ids(osc3008PinnedWrites), []);
    const replay = replayOsc3008PinnedStatic([0, 0]);
    assert.deepEqual(replay.writes, [...osc3008PinnedWrites]);
    assert.deepEqual(replay.terminal, { pass: false, response: '1;1', note: 'Cursor response does not measure OSC 3008 systemd context',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' } });
});

test('the plan is bounded, run-bound, closes what it opens and starts with the pinned stock read', () => {
    const experiments = makeOsc3008Experiments(runId), reads = experiments.reduce((sum, item) => sum + item.plan.filter(step => step.read).length, 0);
    assert.ok(reads + 3 <= 64, 'start, bind and finish are part of the observation budget');
    assert.equal(experiments[0].caseId, `${osc3008CaseId}/stock`);
    assert.deepEqual(experiments[0].plan.filter(step => step.feed).slice(1).map(step => step.feed), [...osc3008PinnedWrites]);
    const names = experiments.map(item => item.name);
    assert.equal(new Set(names).size, names.length);
    assert.ok(experiments.every(item => ['stock', 'required', 'control', 'policy'].includes(item.role)));
    const history = experiments.flatMap(item => item.plan.filter(step => step.feed).map(step => step.feed));
    assert.deepEqual(simulateContexts(history).stack, [], 'every experiment closes its contexts');
    for (const feed of history) for (const id of [...feed.matchAll(/start=([^;\x1b\x07]*)/g)].map(match => match[1])) assert.ok(id.length <= 65, 'ids stay short (the 65-character one is deliberate)');
});

test('stripping removes only inert sequences', () => {
    assert.equal(withoutContextSequences(`a${start('x')}b\x1b[!pc${`\x1b]133;start=x${ST}`}`), 'abc');
    assert.equal(withoutContextSequences('\x1b]133;A\x07'), '\x1b]133;A\x07');
    assert.throws(() => withoutContextSequences('\x1b]3008;start=x'), /Unterminated/);
});
