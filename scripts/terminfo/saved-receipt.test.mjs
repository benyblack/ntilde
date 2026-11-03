import test from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';
import { ownedSavedReceiptFixture as fixture, runId } from './fixtures/saved-receipt.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { savedSources } from './saved-receipt.mjs';
test('v7 binds all twelve suites, paired staged evidence, sources and verified cleanup', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion,7);assert.equal(receipt.fixtureRemoved,true);
    assert.deepEqual(receipt.savedEvidence.counts,{passed:9});assert.deepEqual(receipt.resizeEvidence.counts,{passed:3});
}));
for(const [name,mutate] of [
    ['missing saved binding',f=>{delete f.evidence.savedEvidence;}],
    ['missing prior resize binding',f=>{delete f.evidence.resizeEvidence;}],
    ['missing prior history binding',f=>{delete f.evidence.historyEvidence;}],
    ['missing prior color binding',f=>{delete f.evidence.colorEvidence;}],
    ['missing suite identity',f=>{delete f.context.savedSuiteId;f.write('context.json',f.context);}],
    ['missing policy',f=>{delete f.context.ownedSavedPolicy;f.write('context.json',f.context);}],
    ['missing parent marker',f=>{delete f.proof.proofs[0].saved;f.write('ownership-proof.json',f.proof);}],
    ['wrong parent geometry',f=>{f.proof.proofs[0].saved.geometry.cols++;f.write('ownership-proof.json',f.proof);}],
    ['error-bearing parent marker',f=>{f.proof.proofs[0].saved.error='failed capture';f.write('ownership-proof.json',f.proof);}],
    ['error-bearing binding',f=>{f.evidence.savedEvidence.error='failed capture';}],
    ['contradictory positive status',f=>{f.evidence.savedEvidence.status='unavailable';}],
    ['changed report with recomputed digest',f=>{f.savedReport.observations[0].result='failed';f.write('saved-observations.json',f.savedReport);f.evidence.savedEvidence.sha256=snapshotHash(f.savedReport);}],
    ['changed paired journal',f=>{writeFileSync(join(f.output,'saved-snapshots.jsonl'),'[]');}],
    ['changed source',f=>{writeFileSync(join(f.output,'sources','saved-cases.mjs'),'altered');}],
    ['missing source',f=>{unlinkSync(join(f.output,'sources','saved-expectations.mjs'));}],
    ['forged counts',f=>{f.evidence.savedEvidence.counts={passed:8};}],
])test('v7 refuses '+name,()=>fixture(f=>{mutate(f);assert.throws(f.finish);}));
function strip(f){
    delete f.evidence.savedEvidence;delete f.context.savedSuiteId;delete f.context.ownedSavedPolicy;
    for(const key of Object.keys(savedSources))delete f.context[key];f.write('context.json',f.context);
}
test('retained source files prevent downgrade after context, parent and journal stripping',()=>fixture(f=>{
    strip(f);delete f.proof.proofs[0].saved;f.write('ownership-proof.json',f.proof);unlinkSync(join(f.output,'saved-snapshots.jsonl'));
    assert.throws(f.finish);
}));
test('v7 still requires v6 identities after prior resize markers and binding are stripped',()=>fixture(f=>{
    delete f.evidence.resizeEvidence;delete f.context.resizeSuiteId;delete f.context.ownedResizePolicy;
    f.write('context.json',f.context);assert.throws(f.finish);
}));
function unavailable(f,reason){
    const report={schemaVersion:1,runId,suiteId:f.context.savedSuiteId,status:'unavailable',reason,fixtureStarted:false,plannedExperiments:9};
    f.write('saved-unavailable.json',report);writeFileSync(join(f.output,'saved-snapshots.jsonl'),'');
    delete f.proof.proofs[0].saved.paneId;delete f.proof.proofs[0].saved.geometry;f.write('ownership-proof.json',f.proof);
    Object.assign(f.evidence.savedEvidence,{file:'saved-unavailable.json',sha256:snapshotHash(report),status:'unavailable',reason,counts:{}});return report;
}
for(const reason of ['screenDetailsUnavailable','savedGeometryUnavailable'])test('v7 retains initial '+reason+' without writes or geometry claims',()=>fixture(f=>{
    unavailable(f,reason);assert.equal(f.finish().schemaVersion,7);
}));
for(const key of ['paneId','geometry'])test('unavailable v7 refuses contradictory parent '+key,()=>fixture(f=>{
    unavailable(f,'savedGeometryUnavailable');f.proof.proofs[0].saved[key]=key==='paneId'?f.frames[0].paneId:{rows:12,cols:60};f.write('ownership-proof.json',f.proof);assert.throws(f.finish);
}));
test('unavailable v7 refuses a started fixture despite a matching report digest',()=>fixture(f=>{
    const report=unavailable(f,'savedGeometryUnavailable');report.fixtureStarted=true;f.write('saved-unavailable.json',report);
    f.evidence.savedEvidence.sha256=snapshotHash(report);assert.throws(f.finish);
}));
