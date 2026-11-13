import test from 'node:test';
import assert from 'node:assert/strict';
import { planFeeds, planReads } from './graded-plan.mjs';
import { osc9CaseIds, osc9Experiments, osc9Fixture, osc9NotificationCap, osc9Pinned, osc9ToastHideWaitMs, progressSuffix } from './osc9-contracts.mjs';

const runId = 'b'.repeat(32);

test('the plan fits the observer budget and keeps every experiment distinct and run-bound', () => {
    const contracts = osc9Experiments(runId);
    assert.ok(contracts.reduce((n, item) => n + planReads(item), 0) <= 58);
    assert.equal(new Set(contracts.map(item => item.caseId)).size, contracts.length);
    assert.deepEqual([...new Set(contracts.map(item => item.owner))], osc9CaseIds);
    for (const item of contracts) {
        assert.equal(item.plan[0].feed, osc9Fixture(runId).stage, item.caseId);
        assert.ok(planReads(item) >= 2 && planFeeds(item).length >= 2);
    }
    assert.notDeepEqual(osc9Experiments('c'.repeat(32)).find(item => item.name === 'delivered').expected, osc9Experiments(runId).find(item => item.name === 'delivered').expected);
    assert.throws(() => osc9Fixture('short'), /Invalid/);
});

test('the oracle is the OSC 9;4 protocol: states 1 to 4, withdrawal, clamping and the documented tab suffix', () => {
    const byName = Object.fromEntries(osc9Experiments(runId).filter(item => item.kind === 'progress').map(item => [item.name, item]));
    assert.deepEqual(byName.normal.expected, { kind: 'Normal', percent: 42 });
    assert.deepEqual(byName.error.expected, { kind: 'Error', percent: 37 });
    assert.deepEqual(byName.indeterminate.expected, { kind: 'Indeterminate', percent: null });
    assert.deepEqual(byName.paused.expected, { kind: 'Paused', percent: 61 });
    assert.equal(byName.withdraw.expected, null);
    assert.equal(byName['clamp-high'].expected.percent, 100);
    assert.equal(byName['indeterminate-ignores-percent'].expected.percent, null);
    assert.deepEqual(byName['st-terminated'].plan.at(-2).feed.slice(-2), '\x1b\\');
    assert.deepEqual([byName.normal, byName.error, byName.paused, byName.indeterminate].map(item => progressSuffix(item.expected)), [' 42%', ' ✖ 37%', ' ⏸ 61%', ' ⋯']);
    assert.equal(progressSuffix(null), '');
    assert.equal(progressSuffix({ kind: 'Error', percent: null }), ' ✖');
});

test('absent percentages, unknown and malformed states and bare 4 are policy, never protocol', () => {
    const experiments = osc9Experiments(runId);
    for (const name of ['policy-absent-percent-normal', 'policy-absent-percent-error', 'policy-absent-percent-paused', 'policy-unknown-state', 'policy-bare-4', 'policy-missing-state']) {
        assert.equal(experiments.find(item => item.name === name && item.kind === 'progress').role, 'policy', name);
    }
    for (const name of ['policy-flatten-control', 'policy-cap', 'policy-whitespace-only', 'policy-bare-4', 'focused-suppressed']) {
        assert.equal(experiments.find(item => item.name === name && item.kind === 'notification').role, 'policy', name);
    }
    const cap = experiments.find(item => item.name === 'policy-cap').expected;
    assert.equal(cap.length, osc9NotificationCap); assert.ok(cap.endsWith('…'));
});

test('the notification plan focuses the App for the suppression policy, defocuses before its first inactive control and refocuses at the end; toasts are waited out', () => {
    const notifications = osc9Experiments(runId).filter(item => item.kind === 'notification');
    const actions = notifications.flatMap(item => item.plan.filter(step => step.action !== undefined).map(step => [item.name, step.action]));
    assert.deepEqual(actions, [['focused-suppressed', 'refocus'], ['delivered', 'defocus'], ['policy-bare-4', 'refocus']]);
    const delivered = notifications.find(item => item.name === 'delivered');
    assert.deepEqual(delivered.plan.map(step => step.read ?? step.wait ?? step.action ?? 'feed'), ['feed', 'defocus', 'before', 'feed', 'after', osc9ToastHideWaitMs, 'hidden']);
    for (const item of notifications.filter(entry => entry.expected !== null && entry.name !== 'delivered')) assert.ok(item.plan.some(step => step.wait === osc9ToastHideWaitMs), item.name);
    for (const item of notifications.filter(entry => entry.expected === null)) assert.equal(item.plan.some(step => step.wait !== undefined), item.focus === 'active', item.name);
    assert.ok(osc9ToastHideWaitMs > 6500);
    assert.ok(osc9Experiments(runId).find(item => item.name === 'policy-bare-4' && item.kind === 'progress').plan.some(step => step.wait === osc9ToastHideWaitMs));
});

test('the pinned descriptors say what the registrations can send and return', () => {
    assert.equal(osc9Pinned['extensions.osc9-progress'].termlessFunction, false);
    assert.equal(osc9Pinned['extensions.notifications'].termlessFunction, true);
    for (const item of Object.values(osc9Pinned)) { assert.deepEqual(item.termlessFeeds, []); assert.deepEqual(item.terminalWrites, []); assert.equal(item.fixtureBytes, false); }
});
