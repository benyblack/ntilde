import test from 'node:test';
import assert from 'node:assert/strict';
import { planFeeds, planReads } from './graded-plan.mjs';
import { notifyBodyCap, notifyCaseIds, notifyExperiments, notifyFixture, notifyPinned, notifyTitleCap, notifyToastHideWaitMs, osc777CaseId, osc777PinnedWrites, osc99CaseId } from './notify-contracts.mjs';

const runId = 'b'.repeat(32);
const experiments = notifyExperiments(runId), byName = (owner, name) => experiments.find(item => item.owner === owner && item.name === name);

test('the plan fits the observer budget and keeps every experiment distinct and run-bound', () => {
    assert.ok(experiments.reduce((n, item) => n + planReads(item), 0) <= 58);
    assert.equal(new Set(experiments.map(item => item.caseId)).size, experiments.length);
    assert.deepEqual([...new Set(experiments.map(item => item.owner))], [osc777CaseId, osc99CaseId].sort((a, b) => notifyCaseIds.indexOf(a) - notifyCaseIds.indexOf(b)));
    for (const item of experiments) {
        assert.equal(item.plan[0].feed, notifyFixture(runId).stage, item.caseId);
        assert.ok(planReads(item) >= 2 && planFeeds(item).length >= 2);
    }
    assert.notDeepEqual(notifyExperiments('c'.repeat(32)).find(item => item.name === 'delivered').expected, byName(osc777CaseId, 'delivered').expected);
    assert.throws(() => notifyFixture('short'), /Invalid/);
});

test('the OSC 777 oracle is title then body, a lone title being the message under the pane title', () => {
    const title = notifyFixture(runId).title, m = runId.slice(0, 8);
    assert.deepEqual(byName(osc777CaseId, 'delivered').expected, { title: `NT-777-${m}`, message: `body-${m}` });
    assert.equal(byName(osc777CaseId, 'delivered').plan[3].feed, `\x1b]777;notify;NT-777-${m};body-${m}\x07`);
    assert.deepEqual(byName(osc777CaseId, 'title-only').expected, { title, message: `NT-777-only-${m}` });
    assert.deepEqual(byName(osc777CaseId, 'body-keeps-semicolons').expected.message, 'a;b;c');
    assert.deepEqual(byName(osc777CaseId, 'pinned-fixture').expected, { title: 'test', message: 'body' });
    assert.equal(byName(osc777CaseId, 'st-terminated').plan.find(step => step.feed?.includes('NT-777-st')).feed.slice(-2), '\x1b\\');
});

test('the pinned fixture feeds exactly the two pinned writes after parking the cursor at the origin', () => {
    const item = byName(osc777CaseId, 'pinned-fixture');
    assert.deepEqual(item.plan.filter(step => step.feed !== undefined).map(step => step.feed).slice(1), ['\x1b[1;1H', osc777PinnedWrites.join('')]);
    assert.deepEqual([...osc777PinnedWrites], ['\x1b[1;1H\x1b[2K', '\x1b]777;notify;test;body\x07']);
});

test('the OSC 99 oracle is the kitty protocol: title by default, hold until done, concatenation, base64 and ignored payload types', () => {
    const title = notifyFixture(runId).title, m = runId.slice(0, 8);
    assert.deepEqual(byName(osc99CaseId, 'delivered-title').expected, { title, message: `NT-99-${m}` });
    assert.equal(byName(osc99CaseId, 'delivered-title').plan[2].feed ?? byName(osc99CaseId, 'delivered-title').plan.find(step => step.feed?.includes('NT-99')).feed, `\x1b]99;;NT-99-${m}\x1b\\`);
    const held = byName(osc99CaseId, 'title-and-body');
    assert.deepEqual(held.plan.map(step => step.read ?? step.wait ?? step.action ?? 'feed'), ['feed', 'before', 'feed', 'held', 'feed', 'after', notifyToastHideWaitMs]);
    assert.deepEqual(held.expected, { title: `NT-99-t-${m}`, message: `body-${m}` });
    assert.deepEqual(byName(osc99CaseId, 'chunks-concatenate').expected.message, `NT-99-cat-${m}`);
    const b64 = byName(osc99CaseId, 'base64');
    assert.equal(b64.plan.find(step => step.feed?.includes('e=1')).feed, `\x1b]99;e=1;${Buffer.from(`NT-99-b64-${m}`).toString('base64')}\x1b\\`);
    assert.equal(byName(osc99CaseId, 'unsupported-payload-types').expected, null);
    assert.equal(byName(osc99CaseId, 'unsupported-payload-types').role, 'control');
});

test('flattening, caps, unknown keys, refused chunks and empty payloads are policy, never protocol', () => {
    for (const [owner, name] of [[osc777CaseId, 'policy-flatten-control'], [osc777CaseId, 'policy-caps'], [osc777CaseId, 'policy-empty'], [osc99CaseId, 'policy-unknown-keys'],
        [osc99CaseId, 'policy-bad-base64'], [osc99CaseId, 'policy-oversize-chunk'], [osc99CaseId, 'policy-flatten-control'], [osc99CaseId, 'policy-title-cap'], [osc777CaseId, 'focused-suppressed'], [osc99CaseId, 'focused-suppressed']]) {
        assert.equal(byName(owner, name).role, 'policy', `${owner}/${name}`);
    }
    const caps = byName(osc777CaseId, 'policy-caps').expected;
    assert.equal(caps.title.length, notifyTitleCap); assert.equal(caps.message.length, notifyBodyCap); assert.ok(caps.title.endsWith('…') && caps.message.endsWith('…'));
    assert.equal(byName(osc99CaseId, 'policy-oversize-chunk').plan.find(step => step.feed?.includes('xxxx')).feed.length, 4097 + '\x1b]99;;\x1b\\'.length);
});

test('the plan focuses the App for the suppression policy, defocuses before its first inactive experiment and refocuses at the end; toasts are waited out', () => {
    const actions = experiments.flatMap(item => item.plan.filter(step => step.action !== undefined).map(step => [item.caseId.split('/').at(-1), item.owner, step.action]));
    assert.deepEqual(actions, [['focused-suppressed', osc777CaseId, 'refocus'], ['delivered', osc777CaseId, 'defocus'], ['policy-title-cap', osc99CaseId, 'refocus']]);
    assert.deepEqual(experiments.slice(0, 2).map(item => item.focus), ['active', 'active']);
    const delivered = byName(osc777CaseId, 'delivered');
    assert.deepEqual(delivered.plan.map(step => step.read ?? step.wait ?? step.action ?? 'feed'), ['feed', 'defocus', 'before', 'feed', 'after', notifyToastHideWaitMs, 'hidden']);
    for (const item of experiments.filter(entry => entry.expected !== null && !['delivered', 'delivered-title'].includes(entry.name))) assert.ok(item.plan.some(step => step.wait === notifyToastHideWaitMs), item.caseId);
    for (const item of experiments.filter(entry => entry.expected === null)) assert.equal(item.plan.some(step => step.wait !== undefined), item.focus === 'active', item.caseId);
    assert.ok(notifyToastHideWaitMs > 6500);
});

test('the pinned descriptors say what each registration can send and return', () => {
    assert.equal(notifyPinned[osc777CaseId].termlessFunction, false);
    assert.equal(notifyPinned[osc777CaseId].reachableOutcomes.terminal, 'inconclusive');
    assert.deepEqual([...notifyPinned[osc777CaseId].terminalWrites], [...osc777PinnedWrites]);
    assert.equal(notifyPinned[osc99CaseId].replay, 'stock-collector-and-exact-query-witness');
    assert.deepEqual([...notifyPinned[osc99CaseId].terminalWrites], []);
});
