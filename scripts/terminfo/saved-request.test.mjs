import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { requestOwner } from './ownership.mjs';
async function withServer(reply, action) {
    const server=createServer(socket=>{socket.on('error',()=>{});socket.once('data',()=>socket.end(reply));});
    server.listen(0,'127.0.0.1');await once(server,'listening');
    try {await action({socket:{host:'127.0.0.1',port:server.address().port},token:'test',runId:'a'.repeat(32)});}
    finally {server.close();await once(server,'close');}
}
for(const [kind,code,expected] of [
    ['saved-start','savedGeometryUnavailable','savedGeometryUnavailable'],
    ['saved-read','savedGeometryUnavailable',undefined],
    ['rgb-start','savedGeometryUnavailable',undefined],
    ['saved-start','untrustedCode',undefined],
    ['saved-start','screenDetailsUnavailable','screenDetailsUnavailable'],
])test('owner code '+code+' on '+kind+' retains only the permitted meaning',async()=>{
    await withServer(JSON.stringify({ok:false,error:'refused',code})+'\n',config=>assert.rejects(requestOwner(config,kind),error=>error.code===expected));
});
test('saved full-state replies use the existing bounded snapshot budget',async()=>{
    const reply=JSON.stringify({ok:true,ownerPid:100,detail:'x'.repeat(20000)})+'\n';
    await withServer(reply,async config=>{assert.equal((await requestOwner(config,'saved-read')).detail.length,20000);await assert.rejects(requestOwner(config,'prove'),/Oversized/);});
});
test('saved replies still reject evidence larger than the snapshot ceiling',async()=>{
    await withServer(JSON.stringify({ok:true,detail:'x'.repeat(512*1024)})+'\n',config=>assert.rejects(requestOwner(config,'saved-read'),/Oversized/));
});
