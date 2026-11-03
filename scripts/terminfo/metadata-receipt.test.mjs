import test from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, unlinkSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { ownedMetadataReceiptFixture as fixture, runId } from './fixtures/metadata-receipt.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { metadataSources } from './metadata-receipt.mjs';
test('v8 binds all thirteen suites, paired staged evidence, sources and verified cleanup', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion,8);assert.equal(receipt.fixtureRemoved,true);
    assert.deepEqual(receipt.metadataEvidence.counts,{passed:10,inconclusive:2,failed:1});assert.deepEqual(receipt.resizeEvidence.counts,{passed:3});
}));
for(const [name,mutate] of [
    ['missing metadata binding',f=>{delete f.evidence.metadataEvidence;}],
    ['missing prior saved binding',f=>{delete f.evidence.savedEvidence;}],
    ['missing prior resize binding',f=>{delete f.evidence.resizeEvidence;}],
    ['missing prior history binding',f=>{delete f.evidence.historyEvidence;}],
    ['missing prior color binding',f=>{delete f.evidence.colorEvidence;}],
    ['missing suite identity',f=>{delete f.context.metadataSuiteId;f.write('context.json',f.context);}],
    ['missing policy',f=>{delete f.context.ownedMetadataPolicy;f.write('context.json',f.context);}],
    ['missing parent marker',f=>{delete f.proof.proofs[0].metadata;f.write('ownership-proof.json',f.proof);}],
    ['wrong parent geometry',f=>{f.proof.proofs[0].metadata.geometry.cols++;f.write('ownership-proof.json',f.proof);}],
    ['error-bearing parent marker',f=>{f.proof.proofs[0].metadata.error='failed capture';f.write('ownership-proof.json',f.proof);}],
    ['error-bearing binding',f=>{f.evidence.metadataEvidence.error='failed capture';}],
    ['contradictory positive status',f=>{f.evidence.metadataEvidence.status='unavailable';}],
    ['changed report with recomputed digest',f=>{f.metadataReport.observations[0].result='failed';f.write('metadata-observations.json',f.metadataReport);f.evidence.metadataEvidence.sha256=snapshotHash(f.metadataReport);}],
    ['changed paired journal',f=>{writeFileSync(join(f.output,'metadata-snapshots.jsonl'),'[]');}],
    ['changed source',f=>{writeFileSync(join(f.output,'sources','metadata-cases.mjs'),'altered');}],
    ['missing source',f=>{unlinkSync(join(f.output,'sources','metadata-state.mjs'));}],
    ['forged counts',f=>{f.evidence.metadataEvidence.counts={passed:8};}],
])test('v8 refuses '+name,()=>fixture(f=>{mutate(f);assert.throws(f.finish);}));
function strip(f){
    delete f.evidence.metadataEvidence;delete f.context.metadataSuiteId;delete f.context.ownedMetadataPolicy;
    for(const key of Object.keys(metadataSources))delete f.context[key];f.write('context.json',f.context);
}
test('retained source files prevent downgrade after context, parent and journal stripping',()=>fixture(f=>{
    strip(f);delete f.proof.proofs[0].metadata;f.write('ownership-proof.json',f.proof);unlinkSync(join(f.output,'metadata-snapshots.jsonl'));
    assert.throws(f.finish);
}));
test('v8 still requires v6 identities after prior resize markers and binding are stripped',()=>fixture(f=>{
    delete f.evidence.resizeEvidence;delete f.context.resizeSuiteId;delete f.context.ownedResizePolicy;
    f.write('context.json',f.context);assert.throws(f.finish);
}));
function unavailable(f,reason){
    const report={schemaVersion:1,runId,suiteId:f.context.metadataSuiteId,status:'unavailable',reason,fixtureStarted:false,plannedExperiments:13};
    f.write('metadata-unavailable.json',report);writeFileSync(join(f.output,'metadata-snapshots.jsonl'),'');
    unlinkSync(join(f.output,'metadata-observations.json'));
    delete f.proof.proofs[0].metadata.paneId;delete f.proof.proofs[0].metadata.geometry;f.write('ownership-proof.json',f.proof);
    Object.assign(f.evidence.metadataEvidence,{file:'metadata-unavailable.json',sha256:snapshotHash(report),status:'unavailable',reason,counts:{}});return report;
}
for(const reason of ['screenDetailsUnavailable','paneMetadataUnavailable','metadataGeometryUnavailable'])test('v8 retains initial '+reason+' without writes or geometry claims',()=>fixture(f=>{
    unavailable(f,reason);assert.equal(f.finish().schemaVersion,8);
}));
for(const key of ['paneId','geometry'])test('unavailable v8 refuses contradictory parent '+key,()=>fixture(f=>{
    unavailable(f,'metadataGeometryUnavailable');f.proof.proofs[0].metadata[key]=key==='paneId'?f.frames[0].paneId:{rows:12,cols:60};f.write('ownership-proof.json',f.proof);assert.throws(f.finish);
}));
test('unavailable v8 refuses a started fixture despite a matching report digest',()=>fixture(f=>{
    const report=unavailable(f,'metadataGeometryUnavailable');report.fixtureStarted=true;f.write('metadata-unavailable.json',report);
    f.evidence.metadataEvidence.sha256=snapshotHash(report);assert.throws(f.finish);
}));

for (const file of ['metadata-observations.json', 'metadata-unavailable.json']) {
    test('retained ' + file + ' alone prevents v8 downgrade after all other metadata markers are stripped', () => fixture(f => {
        if (file === 'metadata-unavailable.json') {
            unavailable(f, 'metadataGeometryUnavailable');
        }
        strip(f);
        delete f.proof.proofs[0].metadata;
        f.write('ownership-proof.json', f.proof);
        unlinkSync(join(f.output, 'metadata-snapshots.jsonl'));
        for (const source of Object.values(metadataSources)) unlinkSync(join(f.output, 'sources', source));
        assert.throws(f.finish);
    }));
}

test('unavailable binding refuses a stale observations report', () => fixture(f => {
    unavailable(f, 'metadataGeometryUnavailable');
    f.write('metadata-observations.json', f.metadataReport);
    assert.throws(f.finish);
}));
test('available binding refuses a contradictory unavailable report', () => fixture(f => {
    f.write('metadata-unavailable.json', { status: 'unavailable' });
    assert.throws(f.finish);
}));

for (const prefix of ['sgr', 'rgb', 'rendition', 'cursor', 'pixel', 'erase', 'editing']) test('v8 requires prior ' + prefix + ' binding', () => fixture(f => {
    delete f.evidence[prefix + 'Evidence']; assert.throws(f.finish);
}));
test('v8 validates the retained actual private settings against their context digest', () => fixture(f => {
    f.write('initial-settings.json', { Osc9NotificationsEnabled: false }); assert.throws(f.finish);
}));
test('v8 rejects disabled settings even with a recomputed context digest', () => fixture(f => {
    const settings = { ...JSON.parse(readFileSync(join(f.output, 'initial-settings.json'))), Osc9NotificationsEnabled: false, Osc9ProgressReportingEnabled: true, AgentAccessObserveEnabled: true, TabStripOrientation: 'Horizontal' };
    f.write('initial-settings.json', settings); f.context.initialSettingsSha256 = snapshotHash(settings);
    f.write('context.json', f.context); assert.throws(f.finish);
}));

test('a rejected observed pair cannot accompany an otherwise complete success receipt', () => fixture(f => {
    f.write('metadata-failed-observation.json', { operation: 'bind', before: f.frames[1].before, after: f.frames[1].after });
    assert.throws(f.finish);
}));
