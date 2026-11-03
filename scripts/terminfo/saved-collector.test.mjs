import test from 'node:test';
import assert from 'node:assert/strict';
import { collectSaved, makeSavedCases, savedReset } from './saved-cases.mjs';
import { initialEditingScreen } from './fixtures/editing-journal.mjs';
const runId='a'.repeat(32),barrier='\x1b[?1;2c';
function fixture(fail) {
    const calls=[],writes=[];let failed=false;
    const owner=async(kind,reply,extra)=>{calls.push({kind,reply,extra});if(fail)await fail(kind,calls);
        return kind==='saved-start'?{screen:initialEditingScreen(12,60)}:{};};
    const fence={get failed(){return failed;},write:async value=>{writes.push(value);return barrier;},writeCleanup:async value=>{writes.push(value);},poison(){failed=true;}};
    return {calls,writes,fence,run:()=>collectSaved({config:{runId},fence,owner})};
}
test('the worker runs all source-defined stages serially and restores before finishing',async()=>{
    const f=fixture();await f.run();const reads=f.calls.filter(c=>c.kind==='saved-read');
    assert.deepEqual(reads.map(c=>c.extra),makeSavedCases(12,60).flatMap(item=>item.stages.map(stage=>({caseId:item.id,stageId:stage.id}))));
    assert.equal(reads.length,54);assert.equal(f.calls.at(-1).kind,'saved-finish');assert.equal(f.writes.at(-1),savedReset);
    assert.ok(f.writes[0].includes('NTILDE_SV_'+runId));assert.ok(reads.every(c=>c.reply===barrier));
});
for(const code of ['screenDetailsUnavailable','savedGeometryUnavailable'])test('only initial '+code+' is unavailable without writes',async()=>{
    const f=fixture(kind=>{if(kind==='saved-start')throw Object.assign(new Error(code),{code});});
    assert.deepEqual(await f.run(),{status:'unavailable',reason:code});assert.equal(f.writes.length,0);
});
test('a later typed failure stays a failure and attempts bounded reset',async()=>{
    const error=Object.assign(new Error('later refusal'),{code:'screenDetailsUnavailable'});
    const f=fixture(kind=>{if(kind==='saved-read')throw error;});await assert.rejects(f.run(),e=>e===error);
    assert.equal(f.writes.at(-1),savedReset);assert.ok(!f.calls.some(c=>c.kind==='saved-finish'));
});
test('a poisoned fence uses cleanup and retains both original and cleanup errors',async()=>{
    const original=new Error('input corruption'),cleanup=new Error('cleanup refused');
    const f=fixture(kind=>{if(kind==='saved-read'){f.fence.poison();throw original;}});
    f.fence.writeCleanup=async()=>{throw cleanup;};await assert.rejects(f.run(),error=>error instanceof AggregateError&&error.errors[0]===original&&error.errors[1]===cleanup);
});
