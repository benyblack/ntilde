import test, { after, before } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { animationHoldMs } from './kitty-animation-cases.mjs';
import { persistentAnimationScope } from './collection-scope.mjs';
import { kittyAnimationCarrierPolicy, kittyAnimationCarrierSuiteId, replayPinnedAnimationProbe } from './kitty-animation-qualification.mjs';
import { measuredAppRevision, measuredScriptsRevision, persistentAnimationQualification, persistentAnimationQualificationSuiteId, persistentControls, persistentEffectContract,
    persistentAnimationReceiptSuiteId, pinnedBundleArchive, verifyPersistentAnimationRun } from './kitty-animation-persistent-qualification.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-kitty-animation-persistent/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
let root;

before(() => {
    const manifest = JSON.parse(read('manifest.json'));
    assert.deepEqual(Object.keys(manifest.archives).sort(), ['owned-run.zip', pinnedBundleArchive]);
    assert.equal(hash(read('owned-run.zip')), manifest.archives['owned-run.zip']);
    root = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-persistent-'));
    extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
});
after(() => { if (root) rmSync(root, { recursive: true, force: true }); });

const retained = () => JSON.parse(readFileSync(join(root, 'animation-observations.json')));
/** Extract a private mutable copy of the run for one refusal check. */
function withCopy(action) {
    const copy = mkdtempSync(join(tmpdir(), 'nt-kitty-animation-persistent-copy-'));
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

test('the owned run independently re-derives its receipt, lineage, report and the publication projection', async () => {
    const verified = verifyPersistentAnimationRun(root);
    assert.equal(verified.receipt.schemaVersion, 1);
    assert.equal(verified.receipt.suiteId, persistentAnimationReceiptSuiteId);
    assert.deepEqual(verified.receipt.collectionScope, persistentAnimationScope);
    assert.equal(verified.receipt.fixtureRemoved, true);
    assert.equal(verified.context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v67');
    assert.equal(verified.context.ownedAnimationPolicy, kittyAnimationCarrierPolicy);
    const projected = await persistentAnimationQualification(root, { appRevision: measuredAppRevision, scriptsRevision: measuredScriptsRevision }, read(pinnedBundleArchive));
    assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
    assert.deepEqual(projected.report, JSON.parse(read('report.json')));
    assert.equal(projected.report.suiteId, persistentAnimationQualificationSuiteId);
    assert.equal(projected.summary.carrier.suiteId, kittyAnimationCarrierSuiteId);
});

test('the pinned callbacks are replayed from the retained pinned bundle and are inconclusive on every declaration', async () => {
    const archive = read(pinnedBundleArchive);
    assert.equal(hash(archive), JSON.parse(read('manifest.json')).archives[pinnedBundleArchive]);
    const replay = (await replayPinnedAnimationProbe(archive))['extensions.kitty-graphics.animation'];
    assert.equal(replay.termless.observation.outcome, 'inconclusive');
    assert.equal(replay.terminal.observation.outcome, 'inconclusive');
    const tampered = Buffer.from(archive);
    tampered[tampered.length - 12] ^= 1;
    await assert.rejects(() => replayPinnedAnimationProbe(tampered));
});

test('the persistent effect result and the pinned divergence stay separate, and no stock or Core claim is made', () => {
    const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json')), [item] = report.cases;
    assert.equal(item.caseId, 'extensions.kitty-graphics.animation');
    assert.equal(item.effectContract.id, persistentEffectContract.id);
    assert.equal(item.contractResult.outcome, 'supported');
    assert.equal(item.pinned.observation.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { kind: 'pinned-inconclusive-by-design', pinnedOutcome: 'inconclusive', contractOutcome: 'supported' });
    assert.equal(item.stock, undefined, 'no stock collector ran, so there is no stock outcome to report');
    assert.equal(summary.stockCollector, undefined);
    assert.ok(item.effectContract.validity.length >= 13 && item.effectContract.validity.every(value => value.held));
    assert.deepEqual(summary.carrier.caseResults.map(value => [value.caseId, value.result]),
        [['kitty.animation.stopped-hold', 'passed'], ['kitty.animation.playback', 'passed'], ['kitty.animation.stop-freeze', 'passed'], ['kitty.animation.png-and-root-frames', 'passed']]);
    assert.deepEqual(summary.carrier.counts, { passed: 24 });
});

test('the worker was the multiplexer daemon\'s child and never the App\'s, and the tab was persistent by configuration', () => {
    const proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json'))), settings = JSON.parse(readFileSync(join(root, 'initial-settings.json')));
    const { worker, app } = proof.proofs[0], daemon = proof.persistentDaemon;
    assert.equal(settings.SessionPersistence, 'KeepOnClose');
    assert.equal(worker.ppid, daemon.identity.pid);
    assert.notEqual(worker.ppid, app.pid);
    assert.equal(daemon.identity.executable, app.executable);
    assert.equal(daemon.command, `${app.executable} mux serve`);
    assert.equal(daemon.descriptor.pid, daemon.identity.pid);
    assert.equal(daemon.descriptor.endpoint, `${daemon.appData}/mux/mux.sock`);
    assert.ok(Date.parse(daemon.birth + ' UTC') >= Date.parse(daemon.appBirth + ' UTC'));
    for (const frame of retained().snapshots) assert.equal(frame.ownership.daemon.pid, daemon.identity.pid);
    assert.ok(proof.completed && proof.cleanedUpAt && !proof.cleanupError);
});

test('the retained wall-clock evidence puts every timed read inside its planned frame window', () => {
    const report = retained(), stage = id => report.observations.find(value => value.featureId === id);
    const seconds = (facts, field) => (facts.capture[field] - facts.start.latest) / 1000;
    for (const [id, frame, from, to] of [['playback.second', 'second', 5, 10], ['playback.third', 'third', 10, 15], ['playback.loop', 'root', 15, 20]]) {
        const value = stage('kitty.animation.' + id), { facts } = value.timing;
        assert.equal(value.result, 'passed'); assert.equal(value.timing.valid, true); assert.equal(value.classification.frameId, frame);
        assert.ok(seconds(facts, 'start') > from + 1 && seconds(facts, 'end') < to - 1, `${id} sits well inside its ${from}-${to} s window`);
    }
    const hold = stage('kitty.animation.stop-freeze.hold'), frozen = hold.timing.facts;
    assert.equal(hold.classification.frameId, 'second');
    assert.ok(frozen.capture.start - frozen.stop.latest >= animationHoldMs, 'the hold outlasted a full frame gap after the stop');
    assert.ok((frozen.capture.start - frozen.start.latest) / 1000 > 10, 'a running animation would have reached the third frame by then');
    assert.ok(report.observations.every(value => value.reply.passed === true));
});

test('only the one requested case is qualified; the claim stays bounded to a live persistent tab', () => {
    const report = JSON.parse(read('report.json'));
    assert.equal(report.cases.length, 1);
    for (const limit of ['persistent (KeepOnClose) tab', 'No stock collector ran', 'a reattach or restart', 'a second client', 'remote persistent sessions', 'a non-PNG root'])
        assert.ok(report.scope.includes(limit), limit);
});

test('a downgraded, stripped or foreign persistent run is refused', () => {
    const refuses = (mutation, message) => withCopy(context => {
        mutation(context);
        assert.throws(() => verifyPersistentAnimationRun(context.copy), message);
    });
    refuses(context => rebind(context, 'initial-settings.json', settings => { settings.SessionPersistence = 'Off'; }), /persistent tab|receipt/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { delete proof.persistentDaemon; }), /no multiplexer daemon/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.persistentDaemon.descriptor.pid += 1; }), /descriptor/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.persistentDaemon.command = proof.persistentDaemon.command.replace('mux serve', 'mux ls'); }), /not the launched executable/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.proofs[0].worker.ppid = proof.proofs[0].app.pid; }), /not a child of the recorded daemon|ownership|observed under/);
    refuses(context => rebind(context, 'context.json', value => { value.collectionScope = { ...value.collectionScope, persistence: 'Off' }; }), /declare the persistent animation scope/);
    refuses(context => rebind(context, 'ownership-proof.json', proof => { proof.cleanupError = 'daemon survived'; }), /no completed cleanup/);
    refuses(({ copy }) => rmSync(join(copy, 'source-snapshot.json')), /source-snapshot/);
    refuses(({ copy, write }) => write('source-snapshot.json', '{}\n'), /source manifest|receipt/);
    refuses(({ copy }) => writeFileSync(join(copy, 'sources', 'worker.mjs'), '// changed\n'), /retained source|differs/);
    refuses(({ json, write }) => { const report = json('animation-observations.json'); report.observations[0].result = 'failed'; write('animation-observations.json', report); }, /receipt does not bind/);
});

test('every persistent control is checked, and a violated one is refused by the projection', async () => {
    const verified = verifyPersistentAnimationRun(root), controls = persistentControls(verified);
    assert.deepEqual(controls.map(value => value.name), ['persistent-tab-configured', 'worker-hosted-by-daemon', 'daemon-is-the-launched-executable', 'daemon-stopped']);
    assert.ok(controls.every(value => value.held));
    const moved = structuredClone(verified);
    moved.frames[0].ownership.worker.ppid = moved.frames[0].ownership.app.pid;
    assert.equal(persistentControls(moved).find(value => value.name === 'worker-hosted-by-daemon').held, false);
});

test('every development and measurement attempt is retained with its real outcome, and none of them is the published run', () => {
    const attempts = JSON.parse(read('superseded-attempts/attempts.json')), published = JSON.parse(read('summary.json')).ownedRunId;
    assert.equal(attempts.schemaVersion, 1);
    assert.equal(attempts.attempts.length, 9);
    assert.deepEqual(attempts.attempts.map(item => item.outcome), ['no evidence', 'no evidence', 'no evidence', 'no evidence', 'no evidence', 'no evidence', '24/24 passed', '24/24 passed', '24/24 passed']);
    for (const item of attempts.attempts) {
        assert.notEqual(item.runId, published);
        assert.ok(item.reason.length > 20 && item.scripts.length > 0 && item.files.length >= 2, item.attempt);
        for (const file of item.files) {
            // Retained text is LF in git; a Windows checkout may add CR, so hash and size the LF form (JSON is pinned LF by .gitattributes already).
            const bytes = Buffer.from(read(join('superseded-attempts', file.file)).toString('latin1').replaceAll('\r\n', '\n'), 'latin1');
            assert.equal(hash(bytes), file.sha256, file.file);
            assert.equal(bytes.length, file.bytes, file.file);
        }
        const names = item.files.map(file => file.file.split('/')[1]);
        assert.ok(item.outcome === 'no evidence' ? names.includes('failure.json') : names.includes('animation-observations.json'), item.attempt);
    }
    const complete = attempts.attempts.filter(item => item.outcome !== 'no evidence');
    assert.ok(complete.every(item => JSON.parse(read(join('superseded-attempts', item.files.find(file => file.file.endsWith('animation-observations.json')).file))).observations.length === 24));
});
