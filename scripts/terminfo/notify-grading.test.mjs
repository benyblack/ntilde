import test from 'node:test';
import assert from 'node:assert/strict';
import { snapshotHash } from './agent-observer.mjs';
import { metadataProjection } from './metadata.mjs';
import { planReads } from './graded-plan.mjs';
import { syntheticNotifyEvidence } from './fixtures/notify-evidence.mjs';
import { exposeNotifyFacts } from './notify-collector.mjs';
import { notifyExperiments, osc777CaseId, osc99CaseId } from './notify-contracts.mjs';
import { buildNotifyReport, matchesNotifyBaseline, notifyCounts, replayOsc777PinnedStatic } from './notify-grading.mjs';

const report = e => buildNotifyReport(e.frames, e.run, e.runId, e.adapter);
const verdict = (e, id) => report(e).cases.find(item => item.caseId === id).effectContract;

/** The journal frame index and run read of one contracted observation. */
function locate(e, owner, name, label) {
    let frame = 2;
    for (const [index, contract] of notifyExperiments(e.runId).entries()) {
        const reads = contract.plan.filter(step => step.read !== undefined), at = reads.findIndex(step => step.read === label);
        if (contract.owner === owner && contract.name === name && at >= 0) return { frame: frame + at, read: e.run.experiments[index].reads[at] };
        frame += planReads(contract);
    }
    throw new Error(`No ${owner}/${name} ${label} read.`);
}
/** Change the presentation of one retained observation consistently (frame, hash, paired copy and journaled facts). */
function patch(e, owner, name, label, change) {
    const { frame, read } = locate(e, owner, name, label), f = e.frames[frame];
    change(f.before.metadata.presentation);
    f.after = structuredClone(f.before); f.sha256 = snapshotHash(metadataProjection(f.before)); f.afterSha256 = f.sha256;
    read.facts = exposeNotifyFacts(f.before);
}

test('a faithful journal grades both effect contracts supported; OSC 777 stays inconclusive and OSC 99 leaves its pinned verdict to the stock run', () => {
    const e = syntheticNotifyEvidence(), result = report(e);
    assert.deepEqual(notifyCounts(result), { 'inconclusive/supported': 1, 'stock-and-query-witness/supported': 1 });
    const [s, k] = result.cases;
    assert.equal(s.pinned.qualifiedOutcome, 'inconclusive'); assert.equal(s.pinned.terminal.observation.outcome, 'inconclusive');
    assert.equal(s.pinned.termlessFunction, false); assert.equal(s.pinned.termless, null);
    assert.equal(k.pinned.qualifiedOutcome, null); assert.equal(k.pinned.replay, 'stock-collector-and-exact-query-witness');
    for (const item of result.cases) {
        assert.equal(item.effectContract.preconditionsMet, true);
        assert.ok(item.effectContract.preconditions.every(entry => entry.control && entry.delivery));
    }
    assert.deepEqual(s.divergences.find(entry => entry.path === 'effect'), { path: 'effect', pinnedOutcome: 'inconclusive', effectOutcome: 'supported' });
    assert.equal(k.divergences.some(entry => entry.path === 'effect'), false);
});

test('the pinned OSC 777 replay is the documented constant over the journaled cursor: no observation can upgrade it', () => {
    assert.equal(replayOsc777PinnedStatic([0, 0]).terminal.response, '1;1');
    assert.equal(replayOsc777PinnedStatic([4, 7]).terminal.response, '5;8');
    const e = syntheticNotifyEvidence();
    e.run.pinned[0].terminal.observation.outcome = 'supported';
    assert.throws(() => report(e), /differs from its replay/);
    const other = syntheticNotifyEvidence();
    other.run.pinned[1].replay = 'something else';
    assert.throws(() => report(other), /identification changed/);
});

test('a protocol mismatch with every control held is an unsupported verdict', () => {
    for (const [owner, name, change] of [
        [osc777CaseId, 'delivered', p => { p.toastMessage = 'changed'; }],
        [osc777CaseId, 'pinned-fixture', p => { p.toastTitle = 'someone else'; }],
        [osc99CaseId, 'base64', p => { p.toastVisible = false; }],
        [osc99CaseId, 'title-and-body', p => { p.toastMessage = ''; }],
    ]) {
        const e = syntheticNotifyEvidence();
        patch(e, owner, name, 'after', change);
        assert.equal(verdict(e, owner).outcome, 'unsupported', `${owner}/${name}`);
        assert.equal(verdict(e, owner === osc777CaseId ? osc99CaseId : osc777CaseId).outcome, 'supported');
    }
});

test('a notification held by d=0 that already shows before its last chunk fails the validity control', () => {
    const e = syntheticNotifyEvidence();
    patch(e, osc99CaseId, 'title-and-body', 'held', p => { p.toastVisible = true; p.toastTitle = 'early'; p.toastMessage = 'x'; });
    const result = verdict(e, osc99CaseId);
    assert.equal(result.outcome, 'inconclusive'); assert.equal(result.reason, 'validity-control-failed');
});

test('every validity control gates its case: unmarked leak, ignored payload types, a toast that never auto-hides, a visible toast at setup', () => {
    for (const [owner, name, label, change] of [
        [osc777CaseId, 'unmarked', 'after', p => { p.toastVisible = true; p.toastMessage = 'leak'; }],
        [osc777CaseId, 'other-subcommands', 'after', p => { p.toastVisible = true; p.toastMessage = 'leak'; }],
        [osc99CaseId, 'unmarked', 'after', p => { p.toastVisible = true; p.toastMessage = 'leak'; }],
        [osc99CaseId, 'unsupported-payload-types', 'after', p => { p.toastVisible = true; p.toastMessage = 'leak'; }],
        [osc777CaseId, 'delivered', 'hidden', p => { p.toastVisible = true; }],
        [osc99CaseId, 'delivered-title', 'hidden', p => { p.toastVisible = true; }],
        [osc99CaseId, 'base64', 'before', p => { p.toastVisible = true; }],
    ]) {
        const e = syntheticNotifyEvidence();
        patch(e, owner, name, label, change);
        assert.equal(verdict(e, owner).outcome, 'inconclusive', `${owner}/${name}/${label}`);
    }
});

test('the case needs an inactive-window delivery control: a focused window is never graded', () => {
    for (const [owner, name] of [[osc777CaseId, 'delivered'], [osc99CaseId, 'delivered-title']]) {
        const e = syntheticNotifyEvidence();
        patch(e, owner, name, 'after', p => { p.windowActive = true; p.toastVisible = false; p.toastTitle = ''; p.toastMessage = ''; });
        const result = verdict(e, owner);
        assert.equal(result.outcome, 'inconclusive'); assert.equal(result.reason, 'inactive-window-precondition-unmet'); assert.equal(result.preconditionsMet, false);
    }
});

test('policy observations are recorded but never decide a verdict', () => {
    const e = syntheticNotifyEvidence();
    patch(e, osc99CaseId, 'policy-oversize-chunk', 'after', p => { p.toastVisible = true; p.toastTitle = 'x'; p.toastMessage = 'x'; });
    patch(e, osc777CaseId, 'policy-caps', 'after', p => { p.toastMessage = 'not capped'; });
    const result = report(e);
    assert.deepEqual(result.cases.map(item => item.effectContract.outcome), ['supported', 'supported']);
    const record = result.cases[1].policy.find(entry => entry.name === 'policy-oversize-chunk');
    assert.deepEqual([record.graded, record.matchesDocumentedPolicy], [false, false]);
    assert.equal(result.cases[0].policy.find(entry => entry.name === 'policy-caps').matchesDocumentedPolicy, false);
});

test('the focused-suppression policy is only applicable while the window really was active', () => {
    const e = syntheticNotifyEvidence();
    patch(e, osc777CaseId, 'focused-suppressed', 'before', p => { p.windowActive = false; });
    const record = report(e).cases[0].policy.find(entry => entry.name === 'focused-suppressed');
    assert.equal(record.applicable, false);
    assert.equal(report(syntheticNotifyEvidence()).cases[1].policy.find(entry => entry.name === 'focused-suppressed').applicable, true);
});

test('unrelated drift, changed feeds, identity changes and broken restoration are refused', () => {
    const drift = syntheticNotifyEvidence();
    patch(drift, osc777CaseId, 'delivered', 'after', p => { p.focusMode = true; });
    assert.throws(() => report(drift), /drift/);
    const feeds = syntheticNotifyEvidence();
    feeds.run.experiments[3].feeds[1] = feeds.run.experiments[3].feeds[1].replace('NT-777', 'NT-778');
    assert.throws(() => report(feeds), /feeds/);
    const facts = syntheticNotifyEvidence();
    facts.run.experiments[3].reads[1].facts.toast.message = 'other';
    assert.throws(() => report(facts), /read identity/);
    const adapter = syntheticNotifyEvidence();
    assert.throws(() => buildNotifyReport(adapter.frames, adapter.run, adapter.runId, { ...adapter.adapter, derivedFromAdapterId: 'other' }), /run identity/);
    const toast = syntheticNotifyEvidence(), last = toast.frames.length - 1;
    toast.frames[last].before.metadata.presentation.toastVisible = true; toast.frames[last].after = structuredClone(toast.frames[last].before);
    toast.frames[last].sha256 = toast.frames[last].afterSha256 = snapshotHash(metadataProjection(toast.frames[last].before));
    assert.throws(() => report(toast), /restoration/);
    const actions = syntheticNotifyEvidence();
    delete actions.run.experiments.find(item => item.actions).actions;
    assert.throws(() => report(actions), /action record/);
});

test('the pinned-fixture experiment may park the cursor at the origin; any other experiment moving the cursor is drift', () => {
    assert.doesNotThrow(() => report(syntheticNotifyEvidence()));
    const e = syntheticNotifyEvidence();
    const { frame, read } = locate(e, osc777CaseId, 'delivered', 'after'), f = e.frames[frame];
    f.before.screen.cursorRow = 0; f.before.screen.cursorCol = 0; f.before.metadata.buffer.cursorRow = 0; f.before.metadata.buffer.cursorCol = 0;
    f.after = structuredClone(f.before); f.sha256 = f.afterSha256 = snapshotHash(metadataProjection(f.before));
    read.cursor = [0, 0]; read.facts = exposeNotifyFacts(f.before);
    assert.throws(() => report(e), /drift/);
});

test('the baseline predicate requires withdrawn progress, a hidden toast and the opt-ins, but not window focus or hidden toast text', () => {
    const e = syntheticNotifyEvidence(), first = e.frames[0].before, bound = structuredClone(e.frames[1].before);
    assert.equal(matchesNotifyBaseline(bound, first, e.runId, false), true);
    const focused = structuredClone(bound); focused.metadata.presentation.windowActive = true; focused.metadata.presentation.toastMessage = 'old';
    assert.equal(matchesNotifyBaseline(focused, first, e.runId, false), true);
    for (const change of [p => { p.progressKind = 'Normal'; }, p => { p.toastVisible = true; }, p => { p.notificationsEnabled = false; }, p => { p.tabText += ' 5%'; }]) {
        const broken = structuredClone(bound); change(broken.metadata.presentation);
        assert.equal(matchesNotifyBaseline(broken, first, e.runId, false), false);
    }
});

test('attention markers that follow the live focus state never count as drift or as unrestored state', () => {
    const e = syntheticNotifyEvidence(), last = e.frames.length - 1;
    for (const index of e.frames.keys()) {
        if (index < 2 || index >= last) continue;
        const f = e.frames[index];
        f.before.metadata.presentation.tabText = f.before.metadata.presentation.tabText.replace(/^(\S+)/, '$1 ⌨');
        f.after = structuredClone(f.before); f.sha256 = f.afterSha256 = snapshotHash(metadataProjection(f.before));
    }
    for (const item of e.run.experiments) for (const read of item.reads) read.facts.tabText = read.facts.tabText.replace(/^(\S+)/, '$1 ⌨');
    assert.doesNotThrow(() => report(e));
});
