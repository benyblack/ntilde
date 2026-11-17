import test, { after, before } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { animationHoldMs } from './kitty-animation-cases.mjs';
import { persistentRestoreScope } from './collection-scope.mjs';
import { replayPinnedAnimationProbe } from './kitty-animation-qualification.mjs';
import { measuredAppRevision, measuredScriptsRevision, pinnedBundleArchive, restoreControls, restoreEffectContract, restoreQualification, restoreQualificationSuiteId,
    restoreReceiptSuiteId, verifyRestoreRun } from './kitty-animation-restore-qualification.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-kitty-animation-restore/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
const playing = 'kitty.animation.restore-playing', stopped = 'kitty.animation.restore-stopped', frames3 = 'kitty.animation.restore-frames';
let root;

before(() => {
    const manifest = JSON.parse(read('manifest.json'));
    assert.deepEqual(Object.keys(manifest.archives).sort(), ['owned-run.zip', pinnedBundleArchive]);
    assert.equal(hash(read('owned-run.zip')), manifest.archives['owned-run.zip']);
    root = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-restore-'));
    extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
});
after(() => { if (root) rmSync(root, { recursive: true, force: true }); });

const retained = () => JSON.parse(readFileSync(join(root, 'animation-observations.json')));
/** Extract a private mutable copy of the run for one refusal check. */
function withCopy(action) {
    const copy = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-restore-copy-'));
    try {
        cpSync(root, copy, { recursive: true });
        const json = name => JSON.parse(readFileSync(join(copy, name)));
        const write = (name, value) => writeFileSync(join(copy, name), typeof value === 'string' ? value : JSON.stringify(value, null, 2) + '\n');
        return action({ copy, json, write });
    } finally { rmSync(copy, { recursive: true, force: true }); }
}
/** Mutate a retained file, then re-bind the receipt to the new bytes so the refusal under test is the semantic one, not the hash. */
const rebind = ({ copy, json, write }, name, mutate) => {
    const value = json(name); mutate(value); write(name, value);
    const receipt = json('persistent-animation-receipt.json');
    receipt.ownershipProofSha256 = hash(readFileSync(join(copy, 'ownership-proof.json')));
    receipt.contextSha256 = hash(readFileSync(join(copy, 'context.json')));
    write('persistent-animation-receipt.json', receipt);
};

test('the owned run independently re-derives its receipt, relaunch chain, lineage, report and the publication projection', async () => {
    const verified = verifyRestoreRun(root);
    assert.equal(verified.receipt.suiteId, restoreReceiptSuiteId);
    assert.deepEqual(verified.receipt.collectionScope, persistentRestoreScope);
    assert.equal(verified.receipt.fixtureRemoved, true);
    assert.match(verified.context.sourceSnapshotPolicy, /^readonly-owned-source-graph-v\d+$/);
    const projected = await restoreQualification(root, { appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision }, read(pinnedBundleArchive));
    assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
    assert.deepEqual(projected.report, JSON.parse(read('report.json')));
    assert.equal(projected.report.suiteId, restoreQualificationSuiteId);
});

test('the pinned callbacks are replayed from the retained pinned bundle and are inconclusive on every declaration', async () => {
    const archive = read(pinnedBundleArchive);
    assert.equal(hash(archive), JSON.parse(read('manifest.json')).archives[pinnedBundleArchive]);
    const replay = (await replayPinnedAnimationProbe(archive))['extensions.kitty-graphics.animation'];
    assert.equal(replay.termless.observation.outcome, 'inconclusive');
    assert.equal(replay.terminal.observation.outcome, 'inconclusive');
});

test('the restore effect result and the pinned divergence stay separate, and no stock or Core claim is made', () => {
    const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json')), [item] = report.cases;
    assert.equal(item.effectContract.id, restoreEffectContract.id);
    assert.equal(item.contractResult.outcome, 'supported');
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { kind: 'pinned-inconclusive-by-design', pinnedOutcome: 'inconclusive', contractOutcome: 'supported' });
    assert.equal(item.stock, undefined); assert.equal(summary.stockCollector, undefined);
    assert.ok(item.effectContract.validity.length >= 17 && item.effectContract.validity.every(value => value.held));
    assert.deepEqual(summary.carrier.caseResults.map(value => [value.caseId, value.result]), [[playing, 'passed'], [stopped, 'passed'], [frames3, 'passed']]);
    assert.deepEqual(summary.carrier.counts, { passed: 22 });
});

test('the App was started four times on one session: the same worker and daemon throughout, each relaunch followed by a read of the reattached pane', () => {
    const proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json'))), summary = JSON.parse(read('summary.json')), chain = proof.proofs[0].relaunches;
    const daemon = proof.persistentDaemon, frames = retained().snapshots;
    assert.equal(chain.length, 3);
    assert.deepEqual(chain.map((item, index) => item.from === (index ? chain[index - 1].to : proof.proofs[0].app.pid)), [true, true, true]);
    assert.deepEqual([...new Set(frames.map(frame => frame.ownership.app.pid))], [proof.proofs[0].app.pid, ...chain.map(item => item.to)]);
    assert.ok(frames.every(frame => frame.ownership.worker.pid === proof.proofs[0].worker.pid && frame.ownership.daemon.pid === daemon.identity.pid));
    assert.deepEqual(frames.filter(frame => frame.stageId === 'reattach').map(frame => frame.caseId), [playing, stopped, frames3]);
    assert.equal(summary.pane.sameAcrossRelaunches, true, 'the persisted session names the pane, so the restored pane keeps its id');
    assert.ok(restoreControls({ proof, frames }).every(value => value.held));
});

test('what the restored panes show: the running playhead continues from the original start, the stopped one stays, the edits survive', () => {
    const report = retained(), stage = id => report.observations.find(value => value.featureId === id);
    const reattach = stage(playing + '.reattach'), third = stage(playing + '.third'), loop = stage(playing + '.loop');
    const seconds = (value, field) => (value.timing.facts.capture[field] - value.timing.facts.start.latest) / 1000;
    for (const [value, frame, from, to] of [[third, 'third', 10, 15], [loop, 'root', 15, 20]]) {
        assert.equal(value.result, 'passed'); assert.equal(value.classification.frameId, frame); assert.equal(value.timing.valid, true);
        assert.ok(seconds(value, 'start') > from + 1 && seconds(value, 'end') < to - 1, `${value.featureId} sits well inside its ${from}-${to} s window`);
    }
    assert.equal(reattach.result, 'passed'); assert.equal(reattach.timing.clock, 'running');
    // The App came back before the second frame's window ended, so a playhead restarted at that moment could not show the third frame at about 12 s.
    assert.ok(seconds(reattach, 'end') < seconds(third, 'start') - 5, 'the relaunch finished well before the third-frame read');
    const hold = stage(stopped + '.hold'), reattachedStop = stage(stopped + '.reattach'), stop = hold.timing.facts.stop;
    assert.equal(reattachedStop.classification.frameId, 'second'); assert.equal(hold.classification.frameId, 'second');
    assert.ok(hold.timing.facts.capture.start - reattachedStop.timing.facts.capture.end >= animationHoldMs, 'the hold outlasted a full frame gap after the reattach read');
    assert.ok(reattachedStop.timing.facts.capture.start > stop.latest, 'the reattach read followed the stop');
    for (const [id, frame] of [['reattach', 'third'], ['compose', 'overEdit']]) {
        const value = stage(frames3 + '.' + id);
        assert.equal(value.result, 'passed'); assert.equal(value.classification.frameId, frame); assert.equal(value.timing.clock, 'static');
    }
    assert.ok(report.observations.every(value => value.reply.passed === true));
});

test('only the one requested case is qualified; the claim stays bounded to a killed-and-restarted App', () => {
    const report = JSON.parse(read('report.json'));
    assert.equal(report.cases.length, 1);
    for (const limit of ['killed and started again', 'No stock collector ran', 'a graceful quit', 'a second client', 'remote persistent sessions', 'a non-PNG root'])
        assert.ok(report.scope.includes(limit), limit);
});

test('a downgraded, stripped, unchained or foreign restore run is refused', () => {
    const refuses = (mutation, message) => withCopy(context => {
        mutation(context);
        assert.throws(() => verifyRestoreRun(context.copy), message);
    });
    refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].relaunches.pop(); }), /relaunches/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].relaunches[1].from += 1; }), /ordered chain/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].relaunches[2].to = proof.proofs[0].relaunches[0].to; }), /ordered chain|did not observe|App process/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { delete proof.proofs[0].relaunches; }), /relaunches/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { delete proof.persistentDaemon; }), /no multiplexer daemon/);
    refuses(context => rebind(context, 'context.json', value => { value.collectionScope = { ...value.collectionScope, suiteVersion: 2 }; }), /declare the persistent restore scope/);
    refuses(context => rebind(context, 'initial-settings.json', settings => { settings.SessionPersistence = 'Off'; }), /persistent tab|receipt/);
    refuses(({ copy }) => rmSync(join(copy, 'source-snapshot.json')), /source-snapshot/);
    refuses(({ copy }) => writeFileSync(join(copy, 'sources', 'worker.mjs'), '// changed\n'), /retained source|differs/);
    refuses(({ json, write }) => { const report = json('animation-observations.json'); report.observations[0].result = 'failed'; write('animation-observations.json', report); }, /receipt does not bind/);
});

test('a restore claim without a real relaunch is refused by the carrier report itself', () => {
    withCopy(({ copy, json, write }) => {
        const frames = readFileSync(join(copy, 'animation-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const first = frames[0].ownership.app;
        write('animation-snapshots.jsonl', frames.map(frame => JSON.stringify({ ...frame, ownership: { ...frame.ownership, app: first } })).join('\n') + '\n');
        assert.throws(() => verifyRestoreRun(copy));
    });
});

test('every development attempt of the collection is retained with its real outcome, and none of them is the published run', () => {
    const attempts = JSON.parse(read('superseded-attempts/attempts.json')), published = JSON.parse(read('summary.json')).ownedRunId;
    assert.equal(attempts.schemaVersion, 1);
    assert.ok(attempts.attempts.length >= 2);
    for (const item of attempts.attempts) {
        assert.notEqual(item.runId, published);
        assert.ok(item.reason.length > 20 && item.scripts.length > 0 && item.files.length >= 2, item.attempt);
        for (const file of item.files) {
            // Retained text is LF in git; a Windows checkout may add CR, so hash and size the LF form (JSON is pinned LF by .gitattributes already).
            const bytes = Buffer.from(read(join('superseded-attempts', file.file)).toString('latin1').replaceAll('\r\n', '\n'), 'latin1');
            assert.equal(hash(bytes), file.sha256, file.file);
            assert.equal(bytes.length, file.bytes, file.file);
        }
    }
});
