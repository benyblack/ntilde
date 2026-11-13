import test from 'node:test';
import assert from 'node:assert/strict';
import { snapshotHash } from './agent-observer.mjs';
import { metadataProjection } from './metadata.mjs';
import { planReads } from './graded-plan.mjs';
import { syntheticOsc9Evidence } from './fixtures/osc9-evidence.mjs';
import { exposeOsc9Facts } from './osc9-collector.mjs';
import { osc9Experiments } from './osc9-contracts.mjs';
import { buildOsc9Report, matchesOsc9Baseline, osc9Counts, replayOsc9PinnedStatic } from './osc9-grading.mjs';

const verdict = (e, id) => buildOsc9Report(e.frames, e.run, e.runId, e.adapter).cases.find(item => item.caseId === id).effectContract;
const progress = 'extensions.osc9-progress', notifications = 'extensions.notifications';

/** The journal frame index and run read of one contracted observation. */
function locate(e, name, label) {
    let frame = 2;
    for (const [index, contract] of osc9Experiments(e.runId).entries()) {
        const reads = contract.plan.filter(step => step.read !== undefined);
        const at = reads.findIndex(step => step.read === label);
        if ((contract.caseId === name || contract.caseId.endsWith(`/${name}`)) && at >= 0) return { frame: frame + at, read: e.run.experiments[index].reads[at] };
        frame += planReads(contract);
    }
    throw new Error(`No ${name} ${label} read.`);
}
/** Change the presentation of one retained observation consistently (frame, hash, paired copy and journaled facts). */
function patch(e, name, label, change) {
    const { frame, read } = locate(e, name, label), f = e.frames[frame];
    change(f.before.metadata.presentation);
    f.after = structuredClone(f.before); f.sha256 = snapshotHash(metadataProjection(f.before)); f.afterSha256 = f.sha256;
    read.facts = exposeOsc9Facts(f.before);
}

test('a faithful journal grades both effect contracts supported while both pinned verdicts stay inconclusive', () => {
    const e = syntheticOsc9Evidence(), report = buildOsc9Report(e.frames, e.run, e.runId, e.adapter);
    assert.deepEqual(osc9Counts(report), { 'inconclusive/supported': 2 });
    for (const item of report.cases) {
        assert.equal(item.pinned.qualifiedOutcome, 'inconclusive');
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
    }
    assert.equal(report.cases[0].pinned.termlessFunction, false);
    assert.equal(report.cases[0].pinned.termless, null);
    assert.equal(report.cases[1].pinned.termless.observation.evidence, 'legacy');
    assert.deepEqual(report.cases[1].effectContract.preconditions.map(item => [item.control, item.delivery]).flat(), Array(8).fill(true));
});

test('pinned replay is the documented constant: no observation can upgrade it', () => {
    assert.equal(replayOsc9PinnedStatic(progress).termless, null);
    assert.equal(JSON.parse(replayOsc9PinnedStatic(notifications).termless.response).declared, true);
    assert.throws(() => replayOsc9PinnedStatic('extensions.other'), /Unknown/);
    const e = syntheticOsc9Evidence();
    e.run.pinned[1].termless.observation.outcome = 'supported';
    assert.throws(() => buildOsc9Report(e.frames, e.run, e.runId, e.adapter), /differs from its replay/);
});

test('a protocol mismatch with every control held is an unsupported verdict; a stale effect read is only inconclusive', () => {
    const wrong = syntheticOsc9Evidence();
    patch(wrong, 'paused', 'after', p => { p.progressKind = 'Normal'; p.tabText = p.tabText.replace(' ⏸ 61%', ' 61%'); });
    assert.equal(verdict(wrong, progress).outcome, 'unsupported');
    const stale = syntheticOsc9Evidence();
    patch(stale, 'replace-normal-with-error', 'after', p => { p.progressKind = 'Normal'; p.progressPercent = 17; p.tabText = p.tabText.replace(/ ✖ 80%/, ' 17%'); });
    const result = verdict(stale, progress);
    assert.equal(result.outcome, 'inconclusive'); assert.equal(result.stale, true); assert.equal(result.reason, 'stale-observation');
});

test('every validity control gates the progress verdict', () => {
    for (const [name, label, change] of [
        ['unmarked', 'after', p => { p.progressKind = 'Error'; p.tabText += ' ✖ 17%'; }],
        ['withdraw-when-none', 'after', p => { p.progressOwner = 'ffffffff-ffff-4fff-8fff-ffffffffffff'; p.progressKind = 'Normal'; p.progressPercent = 1; }],
        ['normal', 'before', p => { p.progressKind = 'Normal'; p.progressPercent = 3; }],
    ]) {
        const e = syntheticOsc9Evidence();
        patch(e, name, label, change);
        assert.equal(verdict(e, progress).outcome, 'inconclusive', name);
    }
});

test('policy observations are recorded but never decide a verdict', () => {
    const e = syntheticOsc9Evidence();
    patch(e, 'policy-unknown-state', 'after', p => { p.progressOwner = null; p.progressKind = null; p.progressPercent = null; p.tabText = p.tabText.replace(' 17%', ''); });
    const report = buildOsc9Report(e.frames, e.run, e.runId, e.adapter), item = report.cases[0];
    assert.equal(item.effectContract.outcome, 'supported');
    const record = item.policy.find(entry => entry.name === 'policy-unknown-state');
    assert.deepEqual([record.graded, record.matchesDocumentedPolicy], [false, false]);
});

test('the notification needs an inactive-window delivery control: a focused window is never graded', () => {
    const e = syntheticOsc9Evidence();
    patch(e, 'delivered', 'after', p => { p.windowActive = true; p.toastVisible = false; p.toastTitle = ''; p.toastMessage = ''; });
    const result = verdict(e, notifications);
    assert.equal(result.outcome, 'inconclusive'); assert.equal(result.reason, 'inactive-window-precondition-unmet'); assert.equal(result.preconditionsMet, false);
});

test('a missing delivery with held preconditions and controls is a measured unsupported result', () => {
    const e = syntheticOsc9Evidence();
    patch(e, 'delivered', 'after', p => { p.toastVisible = false; });
    const result = verdict(e, notifications);
    assert.equal(result.outcome, 'unsupported'); assert.equal(result.preconditionsMet, true); assert.equal(result.controlsHeld, true);
});

test('notification validity controls: unmarked leak, toast that never auto-hides, a visible toast at setup, wrong title or message', () => {
    for (const [name, label, change] of [
        [`${notifications}/unmarked`, 'after', p => { p.toastVisible = true; p.toastMessage = 'leak'; }],
        ['delivered', 'hidden', p => { p.toastVisible = true; }],
        [`${notifications}/st-terminated`, 'before', p => { p.toastVisible = true; }],
    ]) {
        const e = syntheticOsc9Evidence();
        patch(e, name, label, change);
        assert.equal(verdict(e, notifications).outcome, 'inconclusive', `${name}/${label}`);
    }
    for (const change of [p => { p.toastTitle = 'someone else'; }, p => { p.toastMessage = 'changed'; }]) {
        const e = syntheticOsc9Evidence();
        patch(e, 'delivered', 'after', change);
        assert.equal(verdict(e, notifications).outcome, 'unsupported');
    }
});

test('an OSC 9;4 payload is not text: progress raising a toast is a protocol mismatch', () => {
    const e = syntheticOsc9Evidence();
    patch(e, 'progress-is-not-text', 'after', p => { p.toastVisible = true; p.toastTitle = 'NT'; p.toastMessage = '4;1;50'; });
    assert.equal(verdict(e, notifications).outcome, 'unsupported');
});

test('the focused-suppression policy is only applicable while the window really was active', () => {
    const e = syntheticOsc9Evidence();
    patch(e, 'focused-suppressed', 'before', p => { p.windowActive = false; });
    const record = buildOsc9Report(e.frames, e.run, e.runId, e.adapter).cases[1].policy.find(entry => entry.name === 'focused-suppressed');
    assert.equal(record.applicable, false);
});

test('unrelated drift, changed feeds, identity changes and broken restoration are refused', () => {
    const drift = syntheticOsc9Evidence();
    patch(drift, 'normal', 'after', p => { p.focusMode = true; });
    assert.throws(() => buildOsc9Report(drift.frames, drift.run, drift.runId, drift.adapter), /drift/);
    const feeds = syntheticOsc9Evidence();
    feeds.run.experiments[0].feeds[1] = feeds.run.experiments[0].feeds[1].replace('42', '43');
    assert.throws(() => buildOsc9Report(feeds.frames, feeds.run, feeds.runId, feeds.adapter), /feeds/);
    const facts = syntheticOsc9Evidence();
    facts.run.experiments[0].reads[1].facts.progress.percent = 99;
    assert.throws(() => buildOsc9Report(facts.frames, facts.run, facts.runId, facts.adapter), /read identity/);
    const adapter = syntheticOsc9Evidence();
    assert.throws(() => buildOsc9Report(adapter.frames, adapter.run, adapter.runId, { ...adapter.adapter, derivedFromAdapterId: 'other' }), /run identity/);
    const toast = syntheticOsc9Evidence(), last = toast.frames.length - 1;
    toast.frames[last].before.metadata.presentation.toastVisible = true; toast.frames[last].after = structuredClone(toast.frames[last].before);
    toast.frames[last].sha256 = toast.frames[last].afterSha256 = snapshotHash(metadataProjection(toast.frames[last].before));
    assert.throws(() => buildOsc9Report(toast.frames, toast.run, toast.runId, toast.adapter), /restoration/);
    const actions = syntheticOsc9Evidence();
    delete actions.run.experiments.find(item => item.actions).actions;
    assert.throws(() => buildOsc9Report(actions.frames, actions.run, actions.runId, actions.adapter), /action record/);
});

test('the baseline predicate requires withdrawn progress, a hidden toast and the opt-ins, but not window focus or hidden toast text', () => {
    const e = syntheticOsc9Evidence(), first = e.frames[0].before;
    const bound = structuredClone(e.frames[1].before);
    assert.equal(matchesOsc9Baseline(bound, first, e.runId, false), true);
    const focused = structuredClone(bound); focused.metadata.presentation.windowActive = true; focused.metadata.presentation.toastMessage = 'old';
    assert.equal(matchesOsc9Baseline(focused, first, e.runId, false), true);
    for (const change of [p => { p.progressKind = 'Normal'; }, p => { p.toastVisible = true; }, p => { p.notificationsEnabled = false; }, p => { p.tabText += ' 5%'; }]) {
        const broken = structuredClone(bound); change(broken.metadata.presentation);
        assert.equal(matchesOsc9Baseline(broken, first, e.runId, false), false);
    }
});
