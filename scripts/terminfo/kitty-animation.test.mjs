import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { syntheticAnimationJournal, animationEpoch } from './fixtures/kitty-animation-journal.mjs';
import { buildAnimationReport, animationPlan, animationActionCount, animationPlannedStages, animationReply, parseAnimationReplies, animationInputs } from './kitty-animation.mjs';
import { animationCases } from './kitty-animation-cases.mjs';
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
    assert.equal(animationPlan.length, 23); assert.equal(animationActionCount, 23); assert.equal(animationPlannedStages, 18);
    assert.equal(animationPlan.filter(entry => entry.kind === 'read').length, 18);
    assert.ok((animationActionCount + 1) * 2 <= 64);
});

test('a faithful playback run grades every stage passed, with timing facts and valid windows retained', () => {
    withJournal({}, ({ build, frames }) => {
        const report = build();
        assert.equal(report.suiteId, 'ntilde-owned-kitty-animation-v1'); assert.equal(report.observations.length, 18);
        assert.ok(report.observations.every(item => item.result === 'passed'), JSON.stringify(report.observations.filter(item => item.result !== 'passed').map(item => [item.featureId, item.inconclusiveReason])));
        assert.deepEqual(report.caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
        const second = report.observations.find(item => item.featureId === 'kitty.animation.playback.second');
        assert.equal(second.timing.valid, true); assert.equal(second.classification.frameId, 'second');
        assert.match(report.scope, /no display-refresh cadence, other platform, frame-rate/);
        assert.equal(report.inputs.length, 3); assert.equal(frames.length, 24);
    });
});

test('an App that rejects animation frames fails every case through its negative replies without inventing playback failures', () => {
    // a=T succeeds and the root shows, but a=f and a=a are refused; nothing ever advances.
    const paint = (entry, appearance) => entry.kind === 'read' && ['second', 'third'].includes(entry.stageId) ? 'root' : appearance;
    const reply = (entry, raw) => {
        if (!['build', 'start', 'stop'].includes(entry.stageId)) return raw;
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
        assert.deepEqual(report.caseResults.map(item => item.result), ['failed', 'failed', 'failed']);
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
    assert.equal(animationInputs().length, 3); assert.ok(animationEpoch > 0);
});
