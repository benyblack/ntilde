import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { inputPlanForSuiteId } from './input-plan.mjs';
import { mexPublication, measuredAppRevision, measuredScriptsRevision } from './mex-publication.mjs';
import { buildMexGradingReport } from './mex-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-15-mouse-extended/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));
const jsonLines = (root, name) => readFileSync(join(root, name), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
const hex = text => Buffer.from(text, 'latin1').toString('hex');

/** The retained final run, extracted once into a fresh directory and re-issued without the collector. */
function withRetainedRun(body) {
    const manifest = JSON.parse(read('manifest.json'));
    const root = mkdtempSync(join(tmpdir(), 'nt-mex-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        return body(root, manifest);
    } finally { rmSync(root, { recursive: true, force: true }); }
}

test('the manifest binds the final run and the one retained superseded attempt', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), 'd07b49523ec79d081785aab21eb4ff930219ebdec37e8a1105b927bd2cb5601e');
    const manifest = JSON.parse(manifestBytes);
    assert.deepEqual(Object.keys(manifest.archives), ['owned-run.zip', 'superseded/attempt-1.zip']);
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    assert.equal(manifest.archives['owned-run.zip'], '1371720651955a76d984352f4cc3ac944bf51b5bba8f4e19f596b0b8ab585bcc');
    assert.equal(manifest.archives['superseded/attempt-1.zip'], 'f4a29e534973762e2709fd8cc2d4102ff9a10012c18e616476ff5d2a66d6c0f4');
    // The retained failed attempt keeps its own failure record: the App refused a generated pointer event while no tracking selector was active.
    const python = process.env.PYTHON ?? (process.platform === 'win32' ? 'python' : 'python3');
    const script = 'import zipfile,sys\nz=zipfile.ZipFile(sys.argv[1])\nsys.stdout.write(z.read("failure.json").decode())';
    const result = spawnSync(python, ['-I', '-c', script, join(dir, 'superseded/attempt-1.zip')], { encoding: 'utf8', windowsHide: true });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(JSON.parse(result.stdout).error, 'Owned generated-input action failed.');
});

test('retained run independently rederives the complete receipt and published projection', () => withRetainedRun(root => {
    const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
    const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
    rmSync(join(root, 'cell-receipt.json'));
    const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
    assert.deepEqual({ ...derived, completedAt }, original);
    // The retained report is rebuilt from the retained input journal, action log, ownership seal and worker run log, byte for byte.
    const context = JSON.parse(readFileSync(join(root, 'context.json'))), proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json')));
    const retained = readFileSync(join(root, 'mex-observations.json')), published = JSON.parse(retained);
    const input = { frames: jsonLines(root, 'input-snapshots.jsonl'), actions: jsonLines(root, 'input-actions.jsonl'), seal: proof.proofs[0].input.seal };
    const rebuilt = buildMexGradingReport(input, JSON.parse(readFileSync(join(root, 'mex-run.json'))), published.runId, published.adapter, {
        contractsVersion: published.contractsVersion, inputPlan: inputPlanForSuiteId(context.inputSuiteId), inputReportSha256: original.inputEvidence.sha256 });
    assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
    assert.equal(original.mexEvidence.sha256, hash(retained));
    const projected = mexPublication(root);
    // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
    projected.summary.receipt = original;
    assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
    assert.deepEqual(projected.report, JSON.parse(read('report.json')));
    assert.equal(schemaVersion, 63); assert.equal(fixtureRemoved, true);
    assert.equal(context.ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v4');
    assert.equal(context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v57');
    assert.equal(projected.summary.appRevision, measuredAppRevision);
    assert.equal(projected.summary.scriptsRevision, measuredScriptsRevision);
    assert.equal(projected.summary.ownedRunId, 'a25f8db7d77fc3d00295ab5daa52ba2d');
    assert.equal(projected.report.inputSuiteId, 'ntilde-owned-routed-input-v4');
    assert.equal(projected.report.restoredSnapshot, input.frames.length - 1);
}));

test('both pinned verdicts are inconclusive, both effect contracts are supported, and each divergence is recorded', () => {
    const report = JSON.parse(read('report.json')), summary = JSON.parse(read('summary.json'));
    const [utf8, urxvt] = report.cases;
    assert.deepEqual(report.cases.map(item => item.caseId), ['modes.utf8-mouse-1005', 'input.urxvt-mouse']);
    // modes.utf8-mouse-1005: the unchanged pinned termless code feeds ?1005h, captures the child's DECRPM reply, feeds ?1005l and returns notTestedResult.
    assert.deepEqual(utf8.pinnedFeeds, ['\x1b[?1005h', '\x1b[?1005l']);
    assert.equal(utf8.served.response, '\x1b[?1005;1$y');
    assert.equal(utf8.pinned.observation, null);
    assert.deepEqual(utf8.pinned.notTested, { reason: 'no-semantic-observable', noObservable: 'encoded UTF-8 mouse events' });
    // input.urxvt-mouse: the pinned probe only reads the restored tracking state and is always inconclusive.
    assert.deepEqual(urxvt.pinnedFeeds, []);
    assert.deepEqual(urxvt.pinned.observation, { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'legacy', note: 'No generated input event and encoded report were validated' });
    for (const item of report.cases) {
        assert.equal(item.pinned.pass, false);
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
        assert.equal(item.effectContract.outcome, 'supported');
        assert.equal(item.contractResult.outcome, 'supported');
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    }
    assert.equal(utf8.effectContract.id, 'ntilde-utf8-mouse-1005-effect-v1');
    assert.equal(urxvt.effectContract.id, 'ntilde-urxvt-mouse-effect-v1');
    // The stock collector grades 1005 supported through its DECRPM path (a stock effect of the production change); urxvt stays inconclusive.
    assert.deepEqual(report.cases.map(item => [item.stock.outcome, item.stock.evidence ?? null]), [['supported', 'query'], ['inconclusive', 'none']]);
    assert.deepEqual(summary.stockCollector.counts, { inconclusive: 162, supported: 92, unsupported: 1 });
    assert.equal(summary.stockCollector.suiteComplete, false);
    assert.deepEqual(summary.receipt.mexEvidence.counts, { 'inconclusive/supported': 2 });
    assert.equal(summary.receipt.inputEvidence.counts.passed, 164);
    assert.match(summary.stockCollector.note, /neither an encoding measurement nor an upgrade/);
});

test('every experiment matched its literal expectation and the independent oracle, with the decisive bytes the child received', () => {
    const report = JSON.parse(read('report.json'));
    const [utf8, urxvt] = report.cases;
    const roles = item => item.experiments.reduce((accumulator, experiment) => { accumulator[experiment.role] = (accumulator[experiment.role] ?? 0) + 1; return accumulator; }, {});
    assert.deepEqual(roles(utf8), { control: 3, required: 20, label: 1 });
    assert.deepEqual(roles(urxvt), { control: 3, required: 21, label: 1 });
    for (const item of report.cases) {
        for (const experiment of item.experiments) {
            assert.equal(experiment.matched, true, experiment.id);
            assert.equal(experiment.actualHex, experiment.expectedHex, experiment.id);
            assert.equal(experiment.oracleHex, experiment.expectedHex, experiment.id);
            assert.equal(experiment.appFacts.boundary, 'avalonia-routed');
            assert.equal(experiment.appFacts.eventDispatched, true);
        }
    }
    const bytes = (item, id) => item.experiments.find(experiment => experiment.id === id).actualHex;
    // UTF-8 (1005): column 95 is still one octet, column 96 is the two-octet C2 80 where legacy X10 sends the single octet 80, the rightmost column (a label) is C2 AC.
    assert.equal(bytes(utf8, 'input.mex.control.legacy.press'), hex('\x1b[M \x80#'));
    assert.equal(bytes(utf8, 'input.mex.u.press.col95'), hex('\x1b[M \x7f#'));
    assert.equal(bytes(utf8, 'input.mex.u.press.left'), hex('\x1b[M \xc2\x80#'));
    assert.equal(bytes(utf8, 'input.mex.u.press.col97'), hex('\x1b[M \xc2\x81#'));
    assert.equal(bytes(utf8, 'input.mex.u.press.col1row1'), hex('\x1b[M !!'));
    assert.equal(bytes(utf8, 'input.mex.u.release.left'), hex('\x1b[M#\xc2\x80#'));
    assert.equal(bytes(utf8, 'input.mex.u.x10.press'), hex('\x1b[M \xc2\x80#'), 'X10 press-only tracking carries no modifier bits');
    assert.equal(bytes(utf8, 'input.mex.u.press.rightmost'), hex('\x1b[M \xc2\xac#'));
    assert.equal(utf8.experiments.find(experiment => experiment.id === 'input.mex.u.press.rightmost').role, 'label');
    // urxvt (1015): decimal Cb;Cx;Cy with the +32 offset, a release is code 35 with the final M, wheel codes 96/97, the rightmost column (a label) is 140.
    assert.equal(bytes(urxvt, 'input.mex.r.press.left'), hex('\x1b[32;96;3M'));
    assert.equal(bytes(urxvt, 'input.mex.r.press.mods7'), hex('\x1b[60;96;3M'));
    assert.equal(bytes(urxvt, 'input.mex.r.release.left'), hex('\x1b[35;96;3M'));
    assert.equal(bytes(urxvt, 'input.mex.r.wheel.up'), hex('\x1b[96;96;3M'));
    assert.equal(bytes(urxvt, 'input.mex.r.wheel.down'), hex('\x1b[97;96;3M'));
    assert.equal(bytes(urxvt, 'input.mex.r.x10.press.mods7'), hex('\x1b[32;96;3M'), 'X10 press-only tracking carries no modifier bits');
    assert.equal(bytes(urxvt, 'input.mex.r.press.rightmost'), hex('\x1b[32;140;3M'));
    assert.equal(urxvt.experiments.find(experiment => experiment.id === 'input.mex.r.press.rightmost').role, 'label');
    // Single-slot selection (xterm extend_coords): a later DECSET replaces the earlier encoding; a DECRST clears only the active one.
    assert.equal(bytes(utf8, 'input.mex.u.slot.sgr.replaces'), hex('\x1b[<0;96;3M'));
    assert.equal(bytes(utf8, 'input.mex.u.slot.utf8.replaces'), hex('\x1b[M \xc2\x80#'));
    assert.equal(bytes(utf8, 'input.mex.u.slot.urxvt.replaced'), hex('\x1b[M \xc2\x80#'));
    assert.equal(bytes(utf8, 'input.mex.u.slot.reset.inactive'), hex('\x1b[<0;96;3M'));
    assert.equal(bytes(utf8, 'input.mex.u.slot.reset.active'), hex('\x1b[M \x80#'), 'the reset of the active UTF-8 encoding falls back to legacy bytes');
    assert.equal(bytes(urxvt, 'input.mex.r.slot.sgr.replaces'), hex('\x1b[<0;96;3M'));
    assert.equal(bytes(urxvt, 'input.mex.r.slot.utf8.replaced'), hex('\x1b[32;96;3M'));
    assert.equal(bytes(urxvt, 'input.mex.r.slot.reset.active'), hex('\x1b[M \x80#'));
});
