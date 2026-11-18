import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { syntheticAnimationJournal, animationEpoch } from './fixtures/kitty-animation-journal.mjs';
import { buildAnimationReport, animationPlan, animationActionCount, animationPlannedStages, animationReply, parseAnimationReplies, animationInputs, animationSuites, animationVersionOfPolicy, ownedAnimationPolicies } from './kitty-animation.mjs';
import { animationCases, animationCasesByVersion, animationCommand, animationSuiteIds } from './kitty-animation-cases.mjs';
import { inputRunId } from './fixtures/input-journal.mjs';

function withJournal(options, action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-kan-'));
    try { const journal = syntheticAnimationJournal(output, options); action({ output, ...journal, build: (frames = journal.frames, actions = journal.actions, seal = journal.seal) =>
        buildAnimationReport(frames, actions, seal, inputRunId, output, []) }); }
    finally { rmSync(output, { recursive: true, force: true }); }
}
const results = report => Object.fromEntries(report.observations.map(item => [item.featureId, item.result]));
const stageOf = (caseId, id) => animationCases.find(item => item.id === caseId).stages.find(stage => stage.id === id);

test('the plan covers every stage once, bounded by the capture budget', () => {
    assert.equal(animationPlan.length, 30); assert.equal(animationActionCount, 30); assert.equal(animationPlannedStages, 24);
    assert.equal(animationPlan.filter(entry => entry.kind === 'read').length, 24);
    assert.ok((animationActionCount + 1) * 2 <= 64);
});

test('suite v1 stays defined beside v2 so retained v1 evidence still rederives', () => {
    assert.deepEqual(Object.keys(animationSuiteIds), ['1', '2', '3']); assert.equal(animationVersionOfPolicy(ownedAnimationPolicies[1]), 1);
    assert.equal(animationVersionOfPolicy(ownedAnimationPolicies[2]), 2); assert.equal(animationVersionOfPolicy('owned-kitty-animation-paired-raw-render-v3'), null);
    assert.equal(animationVersionOfPolicy(undefined), null);
    // v1 is the three-case suite exactly as #988 retained it: 23 actions, 18 stages, three raw-frame inputs; v2 only adds the PNG and root-frame case and its two PNG inputs.
    assert.deepEqual(animationCasesByVersion[1].map(item => item.id), ['kitty.animation.stopped-hold', 'kitty.animation.playback', 'kitty.animation.stop-freeze']);
    assert.equal(animationSuites[1].plan.length, 23); assert.equal(animationSuites[2].plan.length, 30); assert.equal(animationInputs(1).length, 3); assert.equal(animationInputs(2).length, 5);
    assert.deepEqual(animationCasesByVersion[2].slice(0, 3), animationCasesByVersion[1]);
    assert.throws(() => buildAnimationReport([], [], {}, 'a'.repeat(32), '/nonexistent', [], 4));
});

test('a faithful playback run grades every stage passed, with timing facts and valid windows retained', () => {
    withJournal({}, ({ build, frames }) => {
        const report = build();
        assert.equal(report.suiteId, 'ntilde-owned-kitty-animation-v2'); assert.equal(report.observations.length, 24);
        assert.ok(report.observations.every(item => item.result === 'passed'), JSON.stringify(report.observations.filter(item => item.result !== 'passed').map(item => [item.featureId, item.inconclusiveReason])));
        assert.deepEqual(report.caseResults.map(item => item.result), ['passed', 'passed', 'passed', 'passed']);
        const second = report.observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.timing.valid, true); assert.equal(second.classification.frameId, 'second');
        assert.match(report.scope, /no display-refresh cadence, other platform, frame-rate/);
        assert.equal(report.inputs.length, 5); assert.equal(frames.length, 31);
    });
});

test('an App that rejects animation frames fails every case through its negative replies without inventing playback failures', () => {
    // a=T succeeds and the root shows, but a=f and a=a are refused; nothing ever advances.
    const paint = (entry, appearance) => entry.kind === 'read' && ['second', 'third', 'show', 'edit', 'compose'].includes(entry.stageId) ? 'root' : appearance;
    const reply = (entry, raw) => {
        if (!['build', 'start', 'stop', 'show', 'edit', 'compose'].includes(entry.stageId)) return raw;
        const parts = raw.split('\x1b\\').filter(Boolean).map(part => part + '\x1b\\');
        return entry.stageId === 'build' ? parts[0] + parts.slice(1).map(part => part.replace(';OK', ';ENOTSUP:Graphics action not supported')).join('')
            : parts.map(part => part.replace(';OK', ';ENOTSUP:Graphics action not supported')).join('');
    };
    withJournal({ paint, reply }, ({ build }) => {
        const report = build(), value = results(report);
        assert.equal(value['kitty.animation.playback.build'], 'failed');
        assert.equal(value['kitty.animation.playback.start'], 'failed');
        assert.equal(value['kitty.animation.playback.second'], 'inconclusive', 'a pixel mismatch behind failed witnesses is not claimed');
        assert.match(report.observations.find(item => item.featureId === 'kitty.animation.playback.second').inconclusiveReason, /requires passed prior witness/);
        assert.deepEqual(report.caseResults.map(item => item.result), ['failed', 'failed', 'failed', 'failed']);
        assert.equal(report.observations.find(item => item.featureId === 'kitty.animation.playback.build').reply.statuses.join(), 'OK,ENOTSUP:Graphics action not supported,ENOTSUP:Graphics action not supported,ENOTSUP:Graphics action not supported');
    });
});

test('frames accepted but never advancing fails the later stages on their own timing evidence', () => {
    const paint = (entry, appearance) => entry.kind === 'read' && ['second', 'third', 'loop'].includes(entry.stageId) ? 'root' : appearance;
    withJournal({ paint }, ({ build }) => {
        const value = results(build());
        assert.equal(value['kitty.animation.playback.start'], 'passed');
        assert.equal(value['kitty.animation.playback.second'], 'failed');
        assert.equal(value['kitty.animation.stop-freeze.second'], 'failed');
        assert.equal(value['kitty.animation.stopped-hold.hold'], 'passed', 'stationary controls hold for a terminal that never animates');
    });
});

test('an App that ignores the root as a base or an edit target fails the PNG and root frames case on its own pixels', () => {
    const png = 'kitty.animation.png-and-root-frames';
    // The PNG frame is composed over a transparent canvas (c=1 ignored): the shown frame is the unblended green instead of the olive blend.
    withJournal({ paint: (entry, appearance) => entry.caseId === png && entry.stageId === 'show' ? 'second' : appearance }, ({ build }) => {
        const report = build(), value = results(report);
        assert.equal(value[png + '.show'], 'failed');
        assert.equal(value[png + '.edit'], 'inconclusive', 'later stages are not claimed behind a failed witness');
        assert.equal(report.caseResults.find(item => item.caseId === png).result, 'failed');
        assert.equal(value['kitty.animation.playback.second'], 'passed', 'the playback cases are graded on their own evidence');
    });
    // A root edit that does not take effect: the root still shows red after the edit, so the compose stage cannot stand for the edited base either.
    withJournal({ paint: (entry, appearance) => entry.caseId === png && entry.stageId === 'edit' ? 'root' : appearance }, ({ build }) => {
        const value = results(build());
        assert.equal(value[png + '.show'], 'passed'); assert.equal(value[png + '.edit'], 'failed'); assert.equal(value[png + '.compose'], 'inconclusive');
    });
    // A frame built on the original root instead of the edited one shows the red-over-red blend, which is not the purple the edited base gives.
    withJournal({ paint: (entry, appearance) => entry.caseId === png && entry.stageId === 'compose' ? 'root' : appearance }, ({ build }) => {
        assert.equal(results(build())[png + '.compose'], 'failed');
    });
});

test('a mismatching pixel in a valid window fails; the same mismatch outside its window is inconclusive, never failed', () => {
    withJournal({ paint: (entry, appearance) => entry.stageId === 'second' && entry.caseId === 'kitty.animation.playback' ? 'root' : appearance }, ({ build }) => {
        const second = build().observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.result, 'failed'); assert.equal(second.timing.valid, true);
    });
    // Aim the read 2.4 s late: its capture straddles the second/third boundary, so a root pixel proves nothing about playback.
    withJournal({ paint: (entry, appearance) => entry.stageId === 'second' && entry.caseId === 'kitty.animation.playback' ? 'root' : appearance,
        timing: (entry, plan) => entry.caseId === 'kitty.animation.playback' && entry.stageId === 'second' ? { actionAt: plan.actionAt + 2400, readAt: plan.readAt + 2400, span: plan.span.map(value => value + 2400) } : plan },
    ({ build }) => {
        const report = build(), second = report.observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.result, 'inconclusive'); assert.equal(second.timing.valid, false);
        assert.match(second.inconclusiveReason, /straddles|landed in the/);
        const third = report.observations.find(item => item.featureId === 'kitty.animation.playback.third');
        assert.equal(third.result, 'inconclusive'); assert.match(third.inconclusiveReason, /requires passed prior witness/);
        assert.equal(report.caseResults.find(item => item.caseId === 'kitty.animation.playback').result, 'inconclusive');
    });
});

test('a hold that does not outlast the frame gap cannot prove stationarity', () => {
    withJournal({ timing: (entry, plan) => entry.stageId === 'hold' && entry.caseId === 'kitty.animation.stopped-hold'
        ? { actionAt: plan.actionAt - 3000, readAt: plan.readAt - 3000, span: plan.span.map(value => value - 3000) } : plan }, ({ build }) => {
        // The shortened hold would also run into the previous read, which the builder refuses outright.
        assert.throws(() => build());
    });
    withJournal({ timing: (entry, plan) => entry.stageId === 'hold' && entry.caseId === 'kitty.animation.stopped-hold'
        ? { actionAt: plan.actionAt, readAt: plan.readAt, span: plan.span } : plan }, ({ build }) => {
        const hold = build().observations.find(item => item.featureId === 'kitty.animation.stopped-hold.hold');
        assert.equal(hold.result, 'passed'); assert.equal(hold.timing.clock, 'frozen');
    });
});

test('bleed outside the target envelope is a failure whatever the timing', () => {
    withJournal({ bleed: entry => entry.kind === 'read' && entry.caseId === 'kitty.animation.playback' && entry.stageId === 'third',
        timing: (entry, plan) => plan }, ({ build }) => {
        const third = build().observations.find(item => item.featureId === 'kitty.animation.playback.third');
        assert.equal(third.result, 'failed'); assert.equal(third.assertions.at(-1).passed, false);
    });
    // An invalid-timing read with bleed still fails: the bleed control does not depend on when the frame was captured.
    withJournal({ bleed: entry => entry.kind === 'read' && entry.caseId === 'kitty.animation.playback' && entry.stageId === 'second',
        timing: (entry, plan) => entry.caseId === 'kitty.animation.playback' && entry.stageId === 'second' ? { actionAt: plan.actionAt + 2400, readAt: plan.readAt + 2400, span: plan.span.map(value => value + 2400) } : plan }, ({ build }) => {
        const second = build().observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.result, 'failed'); assert.equal(second.timing.valid, false);
    });
});

test('a differing capture pair is inconclusive where a boundary could fall inside the span and a failure where only one frame can be on screen', () => {
    const differ = entry => entry.kind === 'read' && entry.caseId === 'kitty.animation.playback' && entry.stageId === 'second' ? 'third' : undefined;
    // Valid timing (one frame only): the pair cannot legitimately differ, so the inconsistency fails the stage.
    withJournal({ pairDiffer: differ }, ({ build }) => {
        const second = build().observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.timing.valid, true); assert.equal(second.result, 'failed');
        assert.match(second.assertions.map(item => item.description).join('|'), /paired captures show one frame/);
        const third = build().observations.find(item => item.featureId === 'kitty.animation.playback.third');
        assert.equal(third.result, 'inconclusive'); assert.match(third.inconclusiveReason, /requires passed prior witness/);
    });
    // Timing that straddles the second/third boundary: a differing pair is exactly what a correct animation shows there.
    withJournal({ pairDiffer: differ, timing: (entry, plan) => entry.caseId === 'kitty.animation.playback' && entry.stageId === 'second'
        ? { actionAt: plan.actionAt + 2400, readAt: plan.readAt + 2400, span: plan.span.map(value => value + 2400) } : plan }, ({ build }) => {
        const second = build().observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.result, 'inconclusive'); assert.equal(second.timing.valid, false); assert.match(second.inconclusiveReason, /straddles/);
    });
    withJournal({ pairDiffer: entry => entry.kind === 'read' && entry.stageId === 'build' && entry.caseId === 'kitty.animation.playback' ? 'second' : undefined }, ({ build }) => {
        assert.throws(() => build());
    });
});

test('bleed in either member of a differing pair fails the stage', () => {
    for (const side of [0, 1]) withJournal({ bleed: (entry, at) => entry.kind === 'read' && entry.caseId === 'kitty.animation.playback' && entry.stageId === 'second' && at === side,
        pairDiffer: entry => entry.kind === 'read' && entry.caseId === 'kitty.animation.playback' && entry.stageId === 'second' ? 'third' : undefined }, ({ build }) => {
        const second = build().observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.result, 'failed', `bleed in capture ${side}`); assert.equal(second.assertions.at(-1).passed, false);
    });
});

test('tampering with the journal, actions, timing, replies or seal is refused', () => {
    withJournal({}, ({ build, frames, actions, seal, output }) => {
        const clone = value => structuredClone(value);
        const badAction = clone(actions); badAction[3].commandHex = '00' + badAction[3].commandHex.slice(2);
        assert.throws(() => build(frames, badAction, seal));
        const badSpan = clone(frames); badSpan[5].pairSpan = { startMs: badSpan[5].pairSpan.endMs + 1, endMs: badSpan[5].pairSpan.endMs };
        assert.throws(() => build(badSpan, actions, seal));
        const earlyRead = clone(frames); earlyRead[5].pairSpan.startMs = Date.parse(earlyRead[5].ownership.at) - 5;
        assert.throws(() => build(earlyRead, actions, seal));
        const badId = clone(actions); badId[2].imageId = badId[2].imageId + 1;
        assert.throws(() => build(frames, badId, seal));
        const badSeal = clone(seal); badSeal.attempts -= 1;
        assert.throws(() => build(frames, actions, badSeal));
        const wrongKind = clone(frames); wrongKind[4].kind = 'images-read';
        assert.throws(() => build(wrongKind, actions, seal));
        assert.throws(() => build(frames.slice(1), actions, seal));
        const file = join(output, frames[3].pixels[0].file); const bytes = readFileSync(file); bytes[bytes.length - 20] ^= 1; writeFileSync(file, bytes);
        assert.throws(() => build());
    });
});

test('replies: exact correlated framing with the a=T placement echo; missing or error replies are negatives; extras and foreign ids are invalid', () => {
    const ok = id => `\x1b_Gi=${id};OK\x1b\\`, placed = id => `\x1b_Gi=${id},p=1;OK\x1b\\`, error = (id, extra = '') => `\x1b_Gi=${id}${extra};ENOTSUP:Graphics action not supported\x1b\\`;
    const build = stageOf('kitty.animation.playback', 'build'), start = stageOf('kitty.animation.playback', 'start');
    const faithful = placed(7) + ok(7).repeat(3);
    assert.deepEqual(animationReply(faithful, build, 7), { passed: true, statuses: Array(4).fill('OK') });
    assert.equal(animationReply('', build, 7).passed, false);
    assert.equal(animationReply(placed(7) + error(7).repeat(3), build, 7).passed, false);
    assert.equal(animationReply(error(7, ',p=1') + error(7).repeat(3), build, 7).passed, false, 'an error may echo the placement the request named');
    assert.equal(animationReply('\x1b_Gi=7;ENOENT:Image not found\x1b\\', start, 7).passed, false);
    assert.throws(() => animationReply(ok(7) + ok(7).repeat(3), build, 7), 'a=T must echo its placement id');
    assert.throws(() => animationReply(placed(7) + placed(7) + ok(7).repeat(2), build, 7), 'only a=T carries a placement');
    assert.throws(() => animationReply(faithful + ok(7), build, 7)); assert.throws(() => animationReply(ok(8), start, 7));
    assert.throws(() => animationReply('OK', start, 7)); assert.throws(() => animationReply(ok(7) + 'x', start, 7));
    assert.throws(() => animationReply('\x1b_Gi=7,p=0;OK\x1b\\', start, 7)); assert.throws(() => animationReply('\x1b_Gi=7,q=1;OK\x1b\\', start, 7));
    assert.deepEqual(animationReply('', stageOf('kitty.animation.playback', 'second'), 7), { passed: true, statuses: [] });
    assert.throws(() => animationReply(ok(7), stageOf('kitty.animation.playback', 'second'), 7));
    assert.deepEqual(parseAnimationReplies(placed(7) + ok(7), 7), [{ status: 'OK', placement: 1 }, { status: 'OK', placement: null }]);
    assert.equal(animationInputs().length, 5); assert.ok(animationEpoch > 0);
    assert.deepEqual(animationInputs().map(item => item.file.split('.').pop()), ['rgba', 'rgba', 'rgba', 'png', 'png']);
    const png = stageOf('kitty.animation.png-and-root-frames', 'build'), edit = stageOf('kitty.animation.png-and-root-frames', 'edit');
    assert.deepEqual(animationReply(placed(7) + ok(7), png, 7), { passed: true, statuses: ['OK', 'OK'] });
    assert.equal(animationReply(placed(7) + error(7), png, 7).passed, false);
    assert.deepEqual(animationReply(ok(7).repeat(2), edit, 7), { passed: true, statuses: ['OK', 'OK'] });
    assert.throws(() => animationReply(ok(7).repeat(3), edit, 7));
});

// The restore suite (#1030): the same animation state read again after the App was quit and started again on the same persistent session.
const restoreOf = (caseId, id) => animationCasesByVersion[3].find(item => item.id === caseId).stages.find(stage => stage.id === id);
const playing = 'kitty.animation.restore-playing', stopped = 'kitty.animation.restore-stopped', frames3 = 'kitty.animation.restore-frames';
function withRestore(options, action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-kan-restore-'));
    try {
        const journal = syntheticAnimationJournal(output, { version: 3, ...options });
        action({ output, ...journal, build: (frames = journal.frames, actions = journal.actions, seal = journal.seal) => buildAnimationReport(frames, actions, seal, inputRunId, output, [], 3) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

test('the restore suite plans one relaunch per case, each read as an empty command, and its plan fits the capture budget', () => {
    assert.deepEqual(animationCasesByVersion[3].map(item => item.id), [playing, stopped, frames3]);
    assert.equal(animationSuites[3].plan.length, 28); assert.ok((animationSuites[3].plan.length + 1) * 2 <= 64);
    assert.equal(animationInputs(3).length, 5);
    for (const caseId of [playing, stopped, frames3]) {
        assert.equal(animationSuites[3].relaunches({ kind: 'read', caseId, stageId: 'reattach' }), true);
        assert.equal(animationSuites[3].relaunches({ kind: 'read', caseId, stageId: 'blank' }), false);
        assert.equal(animationSuites[3].relaunches({ kind: 'restore', caseId }), false);
        assert.equal(animationCommand(caseId, 'reattach', { imageId: 4000 }, 3), '');
    }
    assert.equal(animationSuites[2].relaunches({ kind: 'read', caseId: 'kitty.animation.playback', stageId: 'second' }), false, 'a direct-tab suite has nothing to relaunch');
    assert.equal(restoreOf(playing, 'reattach').appearance, 'timeline');
});

test('a faithful restore run grades every stage passed across three App processes', () => {
    withRestore({}, ({ build, frames }) => {
        const report = build();
        assert.equal(report.suiteId, 'ntilde-owned-kitty-animation-restore-v1'); assert.equal(report.observations.length, 23);
        assert.ok(report.observations.every(item => item.result === 'passed'), JSON.stringify(report.observations.filter(item => item.result !== 'passed').map(item => [item.featureId, item.inconclusiveReason])));
        assert.deepEqual(report.caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
        assert.equal(new Set(frames.map(frame => frame.ownership.app.pid)).size, 4, 'the initial App and one new App per relaunch');
        assert.deepEqual([...new Set(frames.map(frame => frame.ownership.daemon.ppid))], [frames[0].ownership.app.pid, 1], 'the daemon is its first App\'s child until that App is gone, then init\'s');
        assert.match(report.scope, /terminated with SIGTERM \(not a graceful quit\) and started again on the same persistent session/);
    });
});

test('a relaunch stage shows whichever frame the timeline gives for when the App came back', () => {
    // Back after 6.4 s: the timeline says second, and a pane that shows the second frame passes.
    const around = (from, to) => (entry, plan, { startedAt }) => entry.caseId === playing && entry.stageId === 'reattach'
        ? { actionAt: startedAt + from - 1000, readAt: startedAt + from - 200, span: [startedAt + from, startedAt + to] } : plan;
    withRestore({ timing: around(6400, 7600) }, ({ build }) => {
        const reattach = build().observations.find(item => item.featureId === playing + '.reattach');
        assert.equal(reattach.result, 'passed'); assert.equal(reattach.classification.frameId, 'second');
    });
    // The same late return with a pane that restarted at the root (the playhead was not restored): that is the failure the stage exists to catch.
    withRestore({ timing: around(6400, 7600), paint: (entry, appearance) => entry.caseId === playing && entry.stageId === 'reattach' ? 'root' : appearance }, ({ build }) => {
        const reattach = build().observations.find(item => item.featureId === playing + '.reattach');
        assert.equal(reattach.result, 'failed'); assert.equal(reattach.timing.valid, true);
    });
    // A capture span that straddles a frame boundary proves nothing either way.
    withRestore({ timing: around(4700, 5300), paint: (entry, appearance) => entry.caseId === playing && entry.stageId === 'reattach' ? 'second' : appearance }, ({ build }) => {
        const reattach = build().observations.find(item => item.featureId === playing + '.reattach');
        assert.equal(reattach.result, 'inconclusive'); assert.equal(reattach.timing.valid, false);
    });
});

test('a restored playhead that restarts, a stopped animation that resumes and lost edits each fail on their own evidence', () => {
    withRestore({ paint: (entry, appearance) => entry.caseId === playing && entry.stageId === 'third' ? 'second' : appearance }, ({ build }) => {
        const value = results(build());
        assert.equal(value[playing + '.third'], 'failed'); assert.equal(value[stopped + '.hold'], 'passed');
    });
    withRestore({ paint: (entry, appearance) => entry.caseId === stopped && entry.stageId === 'hold' ? 'third' : appearance }, ({ build }) => {
        const value = results(build());
        assert.equal(value[stopped + '.hold'], 'failed'); assert.equal(value[stopped + '.reattach'], 'passed');
    });
    // The frame built over the edited root after the restore shows the original-root blend: the edited pixels were not restored.
    withRestore({ paint: (entry, appearance) => entry.caseId === frames3 && entry.stageId === 'compose' ? 'overRoot' : appearance }, ({ build }) => {
        const value = results(build());
        assert.equal(value[frames3 + '.compose'], 'failed'); assert.equal(value[frames3 + '.reattach'], 'passed');
    });
});

test('the PNG-derived frame is selected again after the relaunch without being uploaded: its pixels must come back from the snapshot', () => {
    assert.deepEqual(animationCasesByVersion[3].find(item => item.id === frames3).stages.map(stage => stage.id), ['blank', 'build', 'show', 'edit', 'reattach', 'again', 'compose', 'clear']);
    assert.equal(animationCommand(frames3, 'again', { imageId: 9 }, 3), '\x1b_Ga=a,i=9,c=2\x1b\\');
    assert.equal(restoreOf(frames3, 'again').appearance, 'overRoot');
    // Frame 2 comes back as the wrong pixels (its slot survived, its content did not): the later checks alone would not notice.
    withRestore({ paint: (entry, appearance) => entry.caseId === frames3 && entry.stageId === 'again' ? 'root' : appearance }, ({ build }) => {
        const value = results(build());
        assert.equal(value[frames3 + '.again'], 'failed'); assert.equal(value[frames3 + '.reattach'], 'passed'); assert.equal(value[frames3 + '.compose'], 'inconclusive');
    });
});

test('the relaunch record the timing uses is the validated frame\'s, and the admitted action must carry the very same one', () => {
    const refuses = mutate => withRestore({}, ({ build, frames, actions }) => { mutate(frames, actions); assert.throws(() => build(frames, actions)); });
    const reattach = items => items.find(value => value.stageId === 'reattach' && value.caseId === stopped);
    refuses((frames, actions) => { const action = reattach(actions); action.ownership.relaunch = { ...action.ownership.relaunch, startedAt: action.ownership.relaunch.at, at: action.ownership.relaunch.startedAt }; });
    refuses((frames, actions) => { delete reattach(actions).ownership.relaunch; });
    // A record that begins after it ended is no window at all.
    refuses(frames => { const frame = reattach(frames); frame.ownership.relaunch = { ...frame.ownership.relaunch, startedAt: new Date(Date.parse(frame.ownership.relaunch.at) + 500).toISOString() }; });
});

test('only a declared relaunch stage may change the App process, and the worker, daemon and PTY must stay exactly the same', () => {
    const refuses = (mutate, options = {}) => withRestore(options, ({ build, frames, actions }) => { mutate(frames, actions); assert.throws(() => build(frames, actions)); });
    // Another App process at a stage that is not a relaunch.
    refuses(frames => { for (const frame of frames.filter(value => value.index > 8)) frame.ownership.app = { ...frame.ownership.app, pid: 99999 }; });
    // The relaunched App sees another daemon, another worker parent or another PTY.
    const afterRelaunch = frames => frames.filter(value => value.stageId === 'reattach' && value.caseId === playing);
    refuses(frames => { const [frame] = afterRelaunch(frames); frame.ownership.daemon = { ...frame.ownership.daemon, pid: 701 }; frame.ownership.worker = { ...frame.ownership.worker, ppid: 701 }; });
    // The worker changes in the frame and in its admitted action alike, so only the epoch check (not the action-versus-frame comparison) can refuse it.
    refuses((frames, actions) => {
        const [frame] = afterRelaunch(frames), action = actions.find(value => value.caseId === playing && value.stageId === 'reattach');
        frame.ownership.worker = { ...frame.ownership.worker, pid: 4242 }; action.ownership.worker = { ...action.ownership.worker, pid: 4242 };
    });
    refuses(frames => { const [frame] = afterRelaunch(frames); frame.ownership.app = { ...frame.ownership.app, uid: 9999 }; });
    refuses(frames => { const [frame] = afterRelaunch(frames); delete frame.ownership.daemon; });
    // The parent's relaunch record must link the previous App process to this one, in order.
    refuses(frames => { const [frame] = afterRelaunch(frames); frame.ownership.relaunch = { ...frame.ownership.relaunch, from: 1 }; });
    refuses(frames => { const [frame] = afterRelaunch(frames); delete frame.ownership.relaunch; });
    refuses(frames => { const [frame] = afterRelaunch(frames); frame.ownership.relaunch = { ...frame.ownership.relaunch, startedAt: frame.ownership.at, at: new Date(Date.parse(frame.ownership.at) - 1000).toISOString() }; });
    // A declared relaunch that never changed the App process is not restore evidence: uninterrupted playback must not qualify.
    refuses(() => {}, { keepApp: true });
    // A direct-tab (version 2) journal never admits a changed App process.
    withJournal({}, ({ build, frames }) => { for (const frame of frames.filter(value => value.index > 4)) frame.ownership.app = { ...frame.ownership.app, pid: 99999 }; assert.throws(() => build(frames)); });
});

test('a read that a restarted playhead would also pass cannot stand for a restore: an early relaunch makes it inconclusive', () => {
    // The App is back after about 2.6 s: a playhead restarted then predicts the same frames at 12.5 s and 17.5 s as the restored one, so those reads prove nothing about restoration.
    const early = (entry, plan) => entry.caseId === playing && entry.stageId === 'reattach' ? { ...plan, actionAt: plan.actionAt - 3900, readAt: plan.readAt - 3900, span: plan.span.map(value => value - 3900) } : plan;
    withRestore({ timing: early }, ({ build }) => {
        const report = build(), third = report.observations.find(item => item.featureId === playing + '.third');
        assert.equal(third.result, 'inconclusive'); assert.equal(third.timing.valid, false); assert.match(third.timing.reason, /restarted by the relaunch could show the third frame/);
        // The reattach read itself cannot tell the two apart either: the original timeline and a playhead restarted at 2.6 s show the same frame there.
        const reattach = report.observations.find(item => item.featureId === playing + '.reattach');
        assert.equal(reattach.result, 'inconclusive'); assert.match(reattach.inconclusiveReason, /restarted by the relaunch could show the \w+ frame here as well/);
        assert.equal(report.caseResults.find(item => item.caseId === playing).result, 'inconclusive');
    });
    // A relaunch that takes about three seconds separates them: a restart then shows the second frame at 12.5 s, the restored playhead the third.
    withRestore({ paint: (entry, appearance) => entry.caseId === playing && entry.stageId === 'third' ? 'second' : appearance }, ({ build }) => {
        const third = build().observations.find(item => item.featureId === playing + '.third');
        assert.equal(third.result, 'failed'); assert.equal(third.timing.valid, true);
    });
});
