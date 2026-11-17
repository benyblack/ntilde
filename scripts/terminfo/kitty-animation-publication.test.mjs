import test, { after, before } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { checkAnimationEvidence } from './kitty-animation-receipt.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { animationHoldMs } from './kitty-animation-cases.mjs';
import { animationAppearanceColor, animationPngPins } from './kitty-animation-inputs.mjs';
import { caseOutcome, gradeEffect, kittyAnimationCarrierPolicy, kittyAnimationCarrierSuiteId, kittyAnimationQualification, kittyAnimationQualificationIds,
    kittyAnimationQualificationSuiteId, measuredAppRevision, measuredScriptsRevision, pinnedBundleArchive, pinnedSpanDigests, replayPinnedAnimationProbe, validityControls } from './kitty-animation-qualification.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-kitty-animation/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
const clone = value => JSON.parse(JSON.stringify(value));
let root, original, evidenceOf;

before(() => {
    const manifest = JSON.parse(read('manifest.json'));
    assert.deepEqual(Object.keys(manifest.archives).sort(), ['owned-run.zip', pinnedBundleArchive]);
    assert.equal(hash(read('owned-run.zip')), manifest.archives['owned-run.zip']);
    root = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-'));
    extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
    original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
    evidenceOf = receipt => { const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = receipt; return evidence; };
});
after(() => { if (root) rmSync(root, { recursive: true, force: true }); });

const retained = () => JSON.parse(readFileSync(join(root, 'animation-observations.json')));
/** Extract a private mutable copy of the run for one refusal check. */
function withCopy(action) {
    const copy = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-copy-'));
    try {
        cpSync(root, copy, { recursive: true });
        const json = name => JSON.parse(readFileSync(join(copy, name)));
        return action({ copy, json, context: json('context.json'), proof: json('ownership-proof.json'), binding: original.animationEvidence });
    } finally { rmSync(copy, { recursive: true, force: true }); }
}

test('the owned run independently rederives the complete receipt and the publication projection', async () => {
    const copy = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-rederive-'));
    try {
        cpSync(root, copy, { recursive: true });
        rmSync(join(copy, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(copy, join(copy, 'removed-fixture'), evidenceOf(original));
        assert.deepEqual({ ...derived, completedAt: original.completedAt }, original);
        assert.equal(original.schemaVersion, 71);
        assert.equal(original.fixtureRemoved, true);
        assert.equal(original.collectionScope.id, 'owned-osc633-without-notification-suites-v4');
        assert.deepEqual(original.collectionScope.omittedSuites, ['osc9', 'notify']);
        assert.equal(original.collectionScope.predecessor.runId, 'b45702ad11ea1f0ea46eeb4908206155');
        const archive = read(pinnedBundleArchive);
        const projected = await kittyAnimationQualification(root, { appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision }, archive);
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(projected.report.suiteId, kittyAnimationQualificationSuiteId);
        assert.deepEqual(projected.report.cases.map(item => item.caseId), [...kittyAnimationQualificationIds]);
        assert.equal(projected.summary.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v64');
        assert.equal(projected.summary.context.ownedAnimationPolicy, kittyAnimationCarrierPolicy);
        assert.equal(projected.summary.context.animationSuiteId, kittyAnimationCarrierSuiteId);
    } finally { rmSync(copy, { recursive: true, force: true }); }
});

test('the pinned callbacks are replayed from the retained pinned bundle and are inconclusive on every declaration', async () => {
    const archive = read(pinnedBundleArchive);
    assert.equal(hash(archive), JSON.parse(read('manifest.json')).archives[pinnedBundleArchive]);
    const replay = (await replayPinnedAnimationProbe(archive))[kittyAnimationQualificationIds[0]];
    assert.equal(replay.termless.observation.outcome, 'inconclusive');
    assert.equal(replay.termless.observation.evidence, 'legacy');
    assert.equal(replay.termless.pass, false);
    assert.equal(replay.terminal.observation.outcome, 'inconclusive');
    assert.equal(replay.terminal.observation.evidence, 'none');
    assert.notEqual(replay.termless.response, replay.termless.declaredFalseResponse);
    assert.equal(Object.keys(pinnedSpanDigests).length, 2);
    const tampered = Buffer.from(archive);
    tampered[tampered.length - 12] ^= 1;
    await assert.rejects(() => replayPinnedAnimationProbe(tampered));
});

test('the effect result, the pinned divergence and the unchanged stock grade stay separate', () => {
    const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json')), [item] = report.cases;
    assert.equal(item.caseId, 'extensions.kitty-graphics.animation');
    assert.equal(item.contractResult.outcome, 'supported');
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.equal(item.stock.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { kind: 'pinned-inconclusive-by-design', pinnedOutcome: 'inconclusive', contractOutcome: 'supported' });
    assert.equal(item.conclusion.outcome, 'supported');
    assert.ok(item.effectContract.validity.length >= 9 && item.effectContract.validity.every(value => value.held));
    assert.deepEqual(summary.carrier.caseResults.map(value => [value.caseId, value.result]),
        [['kitty.animation.stopped-hold', 'passed'], ['kitty.animation.playback', 'passed'], ['kitty.animation.stop-freeze', 'passed'], ['kitty.animation.png-and-root-frames', 'passed']]);
    assert.deepEqual(summary.carrier.counts, { passed: 24 });
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 161, supported: 93, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
});

test('the retained wall-clock evidence puts every timed read inside its planned frame window', () => {
    const report = retained(), stage = id => report.observations.find(value => value.featureId === id);
    const seconds = (facts, field) => (facts.capture[field] - facts.start.latest) / 1000;
    for (const [id, frame, from, to] of [['playback.second', 'second', 5, 10], ['playback.third', 'third', 10, 15], ['playback.loop', 'root', 15, 20]]) {
        const value = stage('kitty.animation.' + id), { facts } = value.timing;
        assert.equal(value.result, 'passed'); assert.equal(value.timing.valid, true); assert.equal(value.classification.frameId, frame);
        assert.ok(facts.start.latest - facts.start.earliest < 1000, 'the start is bracketed tightly');
        assert.ok(seconds(facts, 'start') > from + 1 && seconds(facts, 'end') < to - 1, `${id} sits well inside its ${from}-${to} s window`);
    }
    const hold = stage('kitty.animation.stop-freeze.hold'), frozen = hold.timing.facts;
    assert.equal(hold.classification.frameId, 'second');
    assert.ok(frozen.capture.start - frozen.stop.latest >= animationHoldMs, 'the hold outlasted a full frame gap after the stop');
    assert.ok((frozen.capture.start - frozen.start.latest) / 1000 > 10, 'a running animation would have reached the third frame by then');
    const stopped = stage('kitty.animation.stopped-hold.hold').timing.facts;
    assert.ok(stopped.capture.start - stopped.build.latest >= animationHoldMs, 'the stationary control outlasted the root gap');
    assert.ok(report.observations.every(value => value.reply.passed === true));
});

test('PNG frames and the root frame as a base and edit target are shown as the blends the protocol gives, chosen without elapsed time', () => {
    const report = retained(), stage = id => report.observations.find(value => value.featureId === 'kitty.animation.png-and-root-frames.' + id);
    for (const [id, frame] of [['build', 'root'], ['show', 'overRoot'], ['edit', 'third'], ['compose', 'overEdit']]) {
        const value = stage(id);
        assert.equal(value.result, 'passed'); assert.equal(value.classification.frameId, frame); assert.equal(value.timing.clock, 'static'); assert.ok(value.reply.passed);
    }
    // The composed colors are the protocol's straight-alpha blends of the independently encoded PNG inputs, not whatever the App drew.
    assert.deepEqual(animationAppearanceColor('overRoot'), { color: [127, 128, 0, 255], tolerance: 2 });
    assert.deepEqual(animationAppearanceColor('overEdit'), { color: [128, 0, 127, 255], tolerance: 2 });
    assert.equal(report.inputs.filter(item => item.file.endsWith('.png')).length, 2);
    assert.deepEqual(report.inputs.filter(item => item.file.endsWith('.png')).map(item => item.sha256), [animationPngPins.overRoot, animationPngPins.overEdit]);
});

test('only the one requested case is qualified; the claim stays bounded', () => {
    const report = JSON.parse(read('report.json'));
    assert.equal(report.cases.length, 1);
    for (const limit of ['other container formats or PNG encodings', 'a=c', 'persistent-session', 'frame-rate or display-refresh cadence', 'common-harness or stock-score claim'])
        assert.ok(report.scope.includes(limit), limit);
    assert.match(report.cases[0].effectContract.statement, /past a hold that would have advanced it/);
});

test('a promoted or stripped carrier result is refused', () => {
    const report = retained();
    const promoted = clone(report);
    promoted.observations.find(item => item.featureId === 'kitty.animation.playback.second').result = 'failed';
    assert.throws(() => gradeEffect(promoted), /carrier result passed differs/);
    const stale = clone(report);
    stale.caseResults.find(item => item.caseId === 'kitty.animation.playback').result = 'failed';
    assert.throws(() => gradeEffect(stale), /carrier result failed differs/);
    const missing = clone(report);
    missing.observations = missing.observations.filter(item => item.featureId !== 'kitty.animation.playback.third');
    assert.throws(() => gradeEffect(missing), /stage list differs/);
    assert.equal(caseOutcome(['passed', 'failed', 'inconclusive']), 'failed');
    assert.equal(caseOutcome(['passed', 'inconclusive']), 'inconclusive');
    assert.equal(caseOutcome(['passed', 'passed']), 'passed');
});

test('a changed or invalid validity control stops the projection', () => withCopy(({ copy, json }) => {
    const receipt = json('cell-receipt.json'), context = json('context.json'), bytes = readFileSync(join(copy, 'animation-observations.json'));
    const report = JSON.parse(bytes);
    assert.ok(validityControls(report, receipt, context, bytes).every(item => item.held));
    const late = clone(report); late.observations.find(item => item.featureId === 'kitty.animation.playback.second').timing.valid = false;
    assert.equal(validityControls(late, receipt, context, bytes).find(item => item.name === 'wall-clock-windows-valid').held, false);
    const reused = clone(report); reused.snapshots[3].capture.nonces = [...reused.snapshots[2].capture.nonces];
    assert.equal(validityControls(reused, receipt, context, bytes).find(item => item.name === 'fresh-paired-fences').held, false);
    // Two captures may share one challenge number: the contract is unique pairs (the collector's rule), not unique numbers.
    const sharedNumber = clone(report); sharedNumber.snapshots[3].capture.nonces = [sharedNumber.snapshots[2].capture.nonces[0], sharedNumber.snapshots[3].capture.nonces[1] === sharedNumber.snapshots[2].capture.nonces[0] ? 40000 : sharedNumber.snapshots[3].capture.nonces[1]];
    assert.equal(validityControls(sharedNumber, receipt, context, bytes).find(item => item.name === 'fresh-paired-fences').held, true);
    const unacknowledged = clone(report); unacknowledged.observations[1].reply.passed = false;
    assert.equal(validityControls(unacknowledged, receipt, context, bytes).find(item => item.name === 'acknowledgement-separate-from-pixels').held, false);
    assert.equal(validityControls(report, receipt, context, Buffer.from(bytes.toString() + ' ')).find(item => item.name === 'receipt-binds-animation-report').held, false);
}));

test('the animation receipt check accepts the retained run and refuses every altered binding, journal, pixel or marker', () => withCopy(({ copy, json, context, proof, binding }) => {
    const runId = original.animationEvidence.runId, check = (b = binding, c = context, p = proof) => checkAnimationEvidence(copy, b, runId, c, p);
    assert.doesNotThrow(() => check());
    for (const alter of [
        () => check({ ...binding, runId: 'f'.repeat(32) }), () => check({ ...binding, suiteId: 'other' }), () => check({ ...binding, sha256: '0'.repeat(64) }),
        () => check({ ...binding, counts: { passed: 17 } }), () => check({ ...binding, extra: 1 }), () => check({ ...binding, status: 'unavailable' }),
        () => check(binding, { ...context, ownedAnimationPolicy: 'other' }), () => check(binding, { ...context, animationSuiteId: 'other' }),
        () => check(binding, { ...context, sourceSnapshotPolicy: 'readonly-owned-source-graph-v62' }),
        () => check(binding, context, { ...proof, proofs: [{ ...proof.proofs[0], kittyAnimation: undefined }] }),
        () => check(binding, context, { ...proof, proofs: [{ ...proof.proofs[0], kittyAnimation: { ...proof.proofs[0].kittyAnimation, geometry: { rows: 1, cols: 1 } } }] }),
    ]) assert.throws(alter);
    // A coherent rewrite of the report is refused by the independent rederivation.
    const report = json('animation-observations.json');
    report.observations.find(item => item.featureId === 'kitty.animation.playback.second').result = 'failed';
    writeFileSync(join(copy, 'animation-observations.json'), JSON.stringify(report));
    assert.throws(() => check());
}));

test('a retained pixel, input, journal line or directory member that changed is refused', () => {
    for (const alter of [
        copy => writeFileSync(join(copy, 'animation-pixels', 'frame-005-0.png'), Buffer.from('not a png')),
        copy => rmSync(join(copy, 'animation-pixels', 'frame-005-1.png')),
        copy => writeFileSync(join(copy, 'animation-pixels', 'frame-099-0.png'), Buffer.from('extra')),
        copy => writeFileSync(join(copy, 'animation-inputs', 'second.rgba'), Buffer.alloc(1024)),
        copy => writeFileSync(join(copy, 'animation-snapshots.jsonl'), readFileSync(join(copy, 'animation-snapshots.jsonl'), 'utf8').split('\n').slice(1).join('\n')),
        copy => writeFileSync(join(copy, 'animation-actions.jsonl'), readFileSync(join(copy, 'animation-actions.jsonl'), 'utf8').replace('"imageId"', '"imageld"')),
        copy => writeFileSync(join(copy, 'animation-raw-failure.json'), '{}'),
        copy => writeFileSync(join(copy, 'animation-unavailable.json'), '{}'),
    ]) withCopy(({ copy, context, proof, binding }) => {
        alter(copy);
        assert.throws(() => checkAnimationEvidence(copy, binding, original.animationEvidence.runId, context, proof));
    });
});

test('the superseded collection attempts are retained with their actual failures and none carries a grade', () => {
    const { attempts } = JSON.parse(readFileSync(join(dir, 'superseded-attempts', 'attempts.json')));
    assert.equal(attempts.length, 13);
    assert.deepEqual(attempts.map(item => item.runId.length), [32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32, 32]);
    assert.ok(attempts.every(item => item.runId !== original.animationEvidence.runId && item.reason.length > 40));
    for (const item of attempts) {
        assert.deepEqual(readdirSync(join(dir, 'superseded-attempts', item.runId)).sort(), Object.keys(item.retainedFiles).sort());
        for (const [file, digest] of Object.entries(item.retainedFiles)) assert.equal(hash(readFileSync(join(dir, 'superseded-attempts', item.runId, file))), digest, file);
    }
    assert.match(attempts[0].reason, /RenderTimer error -6661/);
    assert.match(attempts[2].reason, /i=<id>,p=1;OK/);
    // The earlier complete run is retained as superseded evidence, not as the published verdict: its App was replaced after review.
    assert.match(attempts[3].reason, /Superseded, not failed/);
    assert.equal(attempts[3].archiveSha256.length, 64);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[3].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.match(attempts[4].reason, /RenderTimer error -6661/);
    assert.match(attempts[5].reason, /Superseded, not failed/);
    assert.match(attempts[6].reason, /Superseded, not failed/);
    assert.match(attempts[7].reason, /Superseded, not failed/);
    assert.match(attempts[8].reason, /Superseded, not failed/);
    assert.match(attempts[9].reason, /Superseded, not failed/);
    assert.match(attempts[10].reason, /Superseded, not failed/);
    assert.match(attempts[11].reason, /Superseded, not failed/);
    assert.match(attempts[12].reason, /Superseded, not failed/);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[12].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed', 'passed']);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[11].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[10].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[9].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[8].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[7].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[6].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
    assert.deepEqual(JSON.parse(readFileSync(join(dir, 'superseded-attempts', attempts[5].runId, 'animation-observations.json'))).caseResults.map(item => item.result), ['passed', 'passed', 'passed']);
});
