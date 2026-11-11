import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { expectedTitleStackScreen, makeTitleStackExperiments, replayTitleStackTerminal, replayTitleStackTermless, simulateTitleStack, titleStackCaseIds, titleStackDepth,
    titleStackFixture, titleStackNonceRow, titleStackPinned, titleStackRoles } from './title-stack-contracts.mjs';

const runId = 'a'.repeat(32), fixture = titleStackFixture(runId);
const osc = body => `\x1b]${body}\x07`, push = selector => `\x1b[22;${selector}t`, pop = selector => `\x1b[23;${selector}t`;
const run = (...feeds) => simulateTitleStack([fixture.stage, ...feeds], fixture);
const values = state => [state.title, state.icon];

test('the pinned fixture bytes are the terminfo.dev@4.0.0 bytes, for both cases', { skip: existsSync(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url)) ? false : 'collector cache not installed' }, () => {
    const bundle = readFileSync(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url), 'utf8');
    const section = id => { const start = bundle.indexOf(`...probe("${id}"`); return bundle.slice(start, bundle.indexOf('termlessObservationEvidence', start)); };
    const escaped = text => text.replaceAll('\x1b', '\\x1B').replaceAll('\x07', '\\x07');
    for (const id of titleStackCaseIds) {
        const text = section(id), pinned = titleStackPinned[id];
        const fed = [...text.matchAll(/ctx\.feed\("([^"]*)"\)/g)].map(match => match[1]), written = [...text.matchAll(/ctx\.write\("([^"]*)"\)/g)].map(match => match[1]);
        assert.deepEqual(fed, pinned.termlessFeeds.map(escaped), `${id} termless feeds`);
        assert.deepEqual(written, pinned.terminalWrites.map(escaped), `${id} terminal writes`);
    }
    assert.deepEqual(titleStackPinned['device.xtwinops-22'].termlessFeeds, titleStackPinned['device.xtwinops-23'].termlessFeeds, 'both cases share one termless body');
    assert.notDeepEqual(titleStackPinned['device.xtwinops-22'].terminalWrites, titleStackPinned['device.xtwinops-23'].terminalWrites, 'and differ on the terminal path');
});

test('combined push and pop: a push alone changes nothing, the pop restores both halves', () => {
    const base = run(osc('2;t0') + osc('1;i0'));
    assert.deepEqual(values(base), ['t0', 'i0']);
    assert.deepEqual(values(run(osc('2;t0') + osc('1;i0') + push(0))), ['t0', 'i0']);
    assert.deepEqual(values(run(osc('2;t0') + osc('1;i0'), push(0), osc('2;t1') + osc('1;i1'))), ['t1', 'i1']);
    assert.deepEqual(values(run(osc('2;t0') + osc('1;i0'), push(0), osc('2;t1') + osc('1;i1'), pop(0))), ['t0', 'i0']);
});

test('selectors: 0 and omitted-style combined, 1 icon only, 2 title only; the pop consumes the whole entry whatever it restores', () => {
    const setup = [osc('2;t0') + osc('1;i0'), push(0), osc('2;t1') + osc('1;i1')];
    assert.deepEqual(values(run(...setup, pop(1))), ['t1', 'i0'], 'icon-only pop');
    assert.deepEqual(values(run(...setup, pop(2))), ['t0', 'i1'], 'title-only pop');
    assert.deepEqual(values(run(...setup, pop(1), pop(2))), ['t1', 'i0'], 'the entry was consumed by the first pop');
    assert.deepEqual(values(run(...setup, pop(2), pop(1))), ['t0', 'i1'], 'the entry was consumed by the first pop (title first)');
    assert.deepEqual(values(run(osc('2;t0') + osc('1;i0'), push(2), osc('2;t1') + osc('1;i1'), pop(0))), ['t0', 'i1'], 'a title-only push has no icon half to restore');
    assert.deepEqual(values(run(osc('2;t0') + osc('1;i0'), push(1), osc('2;t1') + osc('1;i1'), pop(0))), ['t1', 'i0'], 'an icon-only push has no title half to restore');
});

test('one-sided pushes copy the other half from the entry below (the documented pop-time lookup)', () => {
    const state = run(osc('2;t0') + osc('1;i0'), push(2), osc('2;t1') + osc('1;i1'), push(1), osc('2;t2') + osc('1;i2'));
    assert.deepEqual(state.stack, [{ icon: null, title: 't0' }, { icon: 'i1', title: 't0' }]);
    assert.deepEqual(values(run(osc('2;t0') + osc('1;i0'), push(2), osc('2;t1') + osc('1;i1'), push(1), osc('2;t2') + osc('1;i2'), pop(0))), ['t0', 'i1']);
});

test('nested pushes restore last-in-first-out and an empty stack is silent', () => {
    const feeds = [osc('2;t0') + osc('1;i0'), push(0), osc('2;t1') + osc('1;i1'), push(0), osc('2;t2') + osc('1;i2')];
    assert.deepEqual(values(run(...feeds, pop(0))), ['t1', 'i1']);
    assert.deepEqual(values(run(...feeds, pop(0), pop(0))), ['t0', 'i0']);
    assert.deepEqual(values(run(...feeds, pop(0), pop(0), pop(0), pop(1), pop(2))), ['t0', 'i0'], 'extra pops change nothing');
    assert.deepEqual(values(run(osc('2;t7') + osc('1;i7'), pop(0) + pop(1) + pop(2))), ['t7', 'i7'], 'an empty pop leaves measured values alone');
});

test('the stack holds ten entries: an eleventh push discards the oldest, and pops below the retained depth are silent', () => {
    assert.equal(titleStackDepth, 10);
    const pushes = Array.from({ length: 12 }, (_, index) => osc(`2;t${index}`) + osc(`1;i${index}`) + push(0));
    const full = run(...pushes);
    assert.equal(full.stack.length, 10);
    assert.deepEqual(full.stack.map(entry => entry.title), ['t2', 't3', 't4', 't5', 't6', 't7', 't8', 't9', 't10', 't11']);
    assert.deepEqual(values(run(...pushes, osc('2;t12') + osc('1;i12'), pop(0))), ['t11', 'i11']);
    assert.deepEqual(values(run(...pushes, osc('2;t12') + osc('1;i12'), pop(0).repeat(10))), ['t2', 'i2']);
    assert.deepEqual(values(run(...pushes, osc('2;t12') + osc('1;i12'), pop(0).repeat(11))), ['t2', 'i2'], 'the eleventh pop finds nothing');
});

test('OSC 0 sets both, OSC 1 the icon and OSC 2 the title; alternate screens and DECSTR keep the stack, RIS clears it and does not retitle', () => {
    assert.deepEqual(values(run(osc('0;both'))), ['both', 'both']);
    assert.deepEqual(values(run(osc('2;only-title'))), ['only-title', fixture.title]);
    assert.deepEqual(values(run(osc('1;only-icon'))), [fixture.title, 'only-icon']);
    const saved = [osc('2;t0') + osc('1;i0'), push(0), osc('2;t1') + osc('1;i1')];
    assert.deepEqual(values(run(...saved, '\x1b[?1049h', '\x1b[?1049l', pop(0))), ['t0', 'i0']);
    assert.deepEqual(values(run(...saved, '\x1b[!p', pop(0))), ['t0', 'i0']);
    const ris = run(...saved, '\x1bc');
    assert.deepEqual([values(ris), ris.stack, ris.rows, ris.cursor, ris.cleared], [['t1', 'i1'], [], {}, [0, 0], true]);
    assert.deepEqual(values(run(...saved, '\x1bc', pop(0))), ['t1', 'i1'], 'nothing left to pop');
});

test('the stage is a fresh measured baseline: prepared title and icon, an empty stack and the nonce row', () => {
    const state = simulateTitleStack([fixture.stage], fixture);
    assert.deepEqual([state.title, state.icon, state.stack, state.rows[titleStackNonceRow], state.cursor], [fixture.title, fixture.title, [], fixture.nonce, [titleStackNonceRow, fixture.nonce.length]]);
    const dirty = simulateTitleStack([fixture.stage, push(0), osc('2;x'), fixture.stage], fixture);
    assert.deepEqual([dirty.title, dirty.stack], [fixture.title, []], 'each experiment starts from the stage');
    assert.ok(fixture.stage.includes(pop(0).repeat(12)), 'the stage drains the stack with its own pops');
    assert.ok(fixture.finish.endsWith(pop(0)), 'the finish write ends with a pop that must leave the prepared values alone');
    assert.ok(fixture.bind.includes('\x1b]4;7;rgb:12/34/56\x07') && fixture.finish.includes('\x1b]104;7\x07'));
});

test('the oracle refuses every byte it was not written for', () => {
    for (const feed of ['x', '\x1b[22;3t', '\x1b[22;0;1t', '\x1b[22:0t', '\x1b[?22;0t', '\x1b]52;c;x\x07', '\x1b]2;no-terminator', '\x1b[1;1H', '\x1b7', '\x1b[22t', '\x1b[?1048h'])
        assert.throws(() => simulateTitleStack([fixture.stage, feed], fixture), /does not understand a contracted feed/, JSON.stringify(feed));
});

test('the expected screen keeps the bound screen and changes only the oracle rows and cursor', () => {
    const bound = { rows: 12, cols: 48, lines: Array(12).fill(''), cursorRow: 0, cursorCol: 0, colorBinding: { paletteOverrideIndices: [7] }, marker: 'kept' };
    const screen = expectedTitleStackScreen(bound, run());
    assert.equal(screen.lines[titleStackNonceRow], fixture.nonce);
    assert.deepEqual([screen.cursorRow, screen.cursorCol, screen.marker, screen.colorBinding], [titleStackNonceRow, fixture.nonce.length, 'kept', bound.colorBinding]);
    assert.deepEqual(expectedTitleStackScreen(bound, run('\x1bc')).lines, Array(12).fill(''));
});

test('every experiment starts from the stage and a distinct, run-bound prepared baseline whose last byte is an empty pop, and reads it back before acting', () => {
    for (const item of makeTitleStackExperiments(runId)) {
        assert.equal(item.plan[0].feed, fixture.stage, item.caseId);
        assert.ok(item.plan[1].feed.endsWith(pop(0)), item.caseId);
        assert.ok(item.plan[1].feed.includes(runId.slice(0, 8) + '-' + item.tag + '-t0'), `${item.caseId} baseline is run-bound and experiment-bound`);
        if (item.name !== 'terminal-path') assert.equal(item.plan[2].read, 'base', item.caseId);
        const feeds = item.plan.filter(step => step.feed !== undefined).map(step => step.feed);
        const state = simulateTitleStack(feeds, fixture);
        assert.equal(state.stack.length, 0, `${item.caseId} ends with an empty stack (oracle)`);
        assert.ok(item.plan.filter(step => step.read !== undefined).every(step => ['setup', 'effect'].includes(step.kind) && ['strict', 'preserved', 'cleared'].includes(step.scope)));
    }
});

test('the two cases own distinct experiments, effect contracts and criteria', () => {
    const experiments = makeTitleStackExperiments(runId), [push22, pop23] = titleStackCaseIds;
    assert.deepEqual([...new Set(experiments.map(item => item.owner))], [push22, pop23]);
    assert.notEqual(titleStackRoles[push22].contractId, titleStackRoles[pop23].contractId);
    assert.notEqual(titleStackRoles[push22].criterion, titleStackRoles[pop23].criterion);
    assert.deepEqual(experiments.filter(item => item.role === 'limit').map(item => item.caseId), [`${push22}/overflow`]);
    assert.deepEqual(experiments.filter(item => item.role === 'boundary').map(item => item.caseId), [`${push22}/alternate-screen`, `${push22}/decstr`, `${pop23}/ris`]);
    assert.equal(experiments.filter(item => item.role === 'stock').length, 2);
});

test('the pinned termless replay mirrors parserStateResult: supported only when all three titles match, otherwise inconclusive with the pinned notes, never unsupported', () => {
    const [push22, pop23] = titleStackCaseIds;
    const good = replayTitleStackTermless(push22, { 'pinned-original': 'pushed-title', 'pinned-changed': 'new-title', 'pinned-restored': 'pushed-title' });
    assert.deepEqual(good, { pass: true, response: '{"original":"pushed-title","changed":"new-title","restored":"pushed-title"}',
        observation: { outcome: 'supported', evidence: 'parser-state' },
        assertions: [{ kind: 'positive', expected: titleStackPinned[push22].expected, observed: '{"original":"pushed-title","changed":"new-title","restored":"pushed-title"}' }] });
    assert.equal(replayTitleStackTermless(pop23, { 'pinned-original': 'pushed-title', 'pinned-changed': 'new-title', 'pinned-restored': 'pushed-title' }).assertions[0].expected, titleStackPinned[pop23].expected);
    const notRestored = replayTitleStackTermless(push22, { 'pinned-original': 'pushed-title', 'pinned-changed': 'new-title', 'pinned-restored': 'new-title' });
    assert.deepEqual([notRestored.pass, notRestored.observation.outcome, notRestored.note], [false, 'inconclusive', 'Title push/pop did not restore the old title; the failing operation is unknown']);
    assert.equal(notRestored.assertions, undefined);
    for (const [original, changed] of [['x', 'new-title'], ['pushed-title', 'y']]) {
        const unobserved = replayTitleStackTermless(push22, { 'pinned-original': original, 'pinned-changed': changed, 'pinned-restored': 'pushed-title' });
        assert.deepEqual([unobserved.observation.outcome, unobserved.note], ['inconclusive', 'Title setup was not observable']);
    }
});

test('the pinned terminal path is inconclusive by design with the pinned note of each case', () => {
    assert.deepEqual(replayTitleStackTerminal('device.xtwinops-22', true), { pass: false, note: 'Cursor answered after title push; title stack was not measured',
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'consumed' } });
    assert.equal(replayTitleStackTerminal('device.xtwinops-23', true).note, 'Cursor answered after title pop; restored title was not measured');
    assert.equal(replayTitleStackTerminal('device.xtwinops-23', false).note, 'No DSR response after title pop');
    assert.equal(replayTitleStackTerminal('device.xtwinops-22', false).note, 'No DSR response after title push');
});
