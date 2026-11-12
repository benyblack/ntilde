import test from 'node:test';
import assert from 'node:assert/strict';
import { snapshotHash } from './agent-observer.mjs';
import { metadataProjection } from './metadata.mjs';
import { buildOscTitleReport, replayOscTitlePinned } from './osc-title-grading.mjs';
import { syntheticOscTitleEvidence as synthetic } from './fixtures/osc-title-evidence.mjs';

const build = e => buildOscTitleReport(e.frames, e.run, e.runId, e.adapter);
function mutateRead(e, experiment, which, action) {
    const frame = e.frames[2 + 2 * experiment + which], entry = e.run.experiments[experiment].reads[which];
    action(frame.before);
    frame.after = structuredClone(frame.before);
    frame.sha256 = snapshotHash(metadataProjection(frame.before)); frame.afterSha256 = frame.sha256;
    entry.facts = { title: frame.before.metadata.oscTitle, icon: frame.before.metadata.buffer.requestedIconName };
}

test('complete paired evidence yields distinct effect results and refuses tampering', () => {
    const e = synthetic();
    assert.deepEqual(build(e).cases.map(item => item.effectContract.outcome), ['supported', 'supported', 'supported']);
    const changedFeed = synthetic(); changedFeed.run.experiments[0].feeds[2] += 'x';
    assert.throws(() => build(changedFeed), /feeds/);
    const crossCase = synthetic(); crossCase.run.experiments[0].caseId = crossCase.run.experiments[8].caseId;
    assert.throws(() => build(crossCase), /identity/);
    const crossRead = synthetic(), other = crossRead.run.experiments[8].reads[0].facts;
    mutateRead(crossRead, 1, 0, value => { value.metadata.oscTitle = other.title; value.metadata.buffer.requestedIconName = other.icon; });
    assert.throws(() => build(crossRead), /Cross-case/);
    const incomplete = synthetic(); incomplete.frames.pop();
    assert.throws(() => build(incomplete), /Incomplete/);
    const tampered = synthetic(); tampered.frames[2].before.metadata.oscTitle = 'stale';
    assert.throws(() => build(tampered));
    const identity = synthetic(); identity.adapter.bundleSha256 = 'bad';
    assert.throws(() => build(identity), /identity/);
    const restoration = synthetic(), finish = restoration.frames.at(-1);
    finish.before.metadata.oscTitle = 'not-restored'; finish.after = structuredClone(finish.before);
    finish.sha256 = snapshotHash(metadataProjection(finish.before)); finish.afterSha256 = finish.sha256;
    assert.throws(() => build(restoration), /restoration/);
    for (const field of ['displayTitle', 'tabText']) {
        const changed = synthetic(), last = changed.frames.at(-1);
        if (field === 'displayTitle') last.before.metadata.displayTitle = 'stale display';
        else last.before.metadata.presentation.tabText = 'stale tab';
        last.after = structuredClone(last.before); last.sha256 = snapshotHash(metadataProjection(last.before)); last.afterSha256 = last.sha256;
        assert.throws(() => build(changed), /restoration/, field);
    }
});

test('a wrong effect needs valid controls; unavailable or unrelated drift cannot become a verdict', () => {
    const wrong = synthetic();
    mutateRead(wrong, 0, 1, value => { value.metadata.oscTitle = 'wrong'; });
    Object.assign(wrong.run.pinned[0], replayOscTitlePinned(wrong.run.pinned[0].caseId, wrong.run.experiments[0].reads[0].facts.title, 'wrong'));
    assert.equal(build(wrong).cases[0].effectContract.outcome, 'unsupported');
    const badControl = synthetic();
    mutateRead(badControl, 1, 0, value => { value.metadata.oscTitle = 'stale'; });
    assert.equal(build(badControl).cases[0].effectContract.outcome, 'inconclusive');
    const stale = synthetic(), original = stale.run.experiments[1].reads[0].facts;
    mutateRead(stale, 1, 1, value => { value.metadata.oscTitle = original.title; value.metadata.buffer.requestedIconName = original.icon; });
    assert.equal(build(stale).cases[0].effectContract.outcome, 'inconclusive');
    const unrelated = synthetic();
    mutateRead(unrelated, 1, 1, value => { value.metadata.workingDirectory = '/elsewhere'; });
    assert.throws(() => build(unrelated), /drifted/);
    const missing = synthetic();
    mutateRead(missing, 1, 1, value => { delete value.metadata.buffer.requestedIconName; });
    assert.throws(() => build(missing), /facts|evidence/);
});

test('a truncated fallback tab must retain a long prefix of the actual display title', () => {
    const e = synthetic();
    mutateRead(e, 1, 1, value => { value.metadata.presentation.tabText = value.metadata.displayTitle.slice(0, 41) + '… ⌨'; });
    assert.equal(build(e).cases[0].effectContract.outcome, 'supported');
    mutateRead(e, 1, 1, value => { value.metadata.presentation.tabText = 'unrelated stale title …'; });
    assert.equal(build(e).cases[0].effectContract.outcome, 'inconclusive');
});
