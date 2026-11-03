import test from 'node:test';
import assert from 'node:assert/strict';
import { makeMetadataCases, metadataFixture } from './metadata-cases.mjs';
const runId = 'a'.repeat(32), cases = makeMetadataCases(40, 140, runId);
test('exact OSC P probes retain their distinct scope and cannot receive a passing semantic grade', () => {
    const probes = cases.filter(value => value.id.startsWith('metadata.shell.P.'));
    assert.deepEqual(probes.map(value => value.stages[1].sequence), ['\x1b]133;P;Cwd=/tmp\x07', '\x1b]133;P;k=r\x07']);
    assert.ok(probes.every(value => value.qualification === 'inconclusive'));
});
test('source-known lifecycle encodes independent A B C D markers and raw anchor positions', () => {
    const value = cases.find(value => value.id === 'metadata.shell.lifecycle');
    assert.deepEqual(value.stages.map(value => value.id), ['control', 'A', 'B', 'C', 'D', 'restored']);
    assert.equal(value.stages[2].sequence, '\x1b[3;5H\x1b]133;B\x07echo owned');
    assert.equal(value.stages[3].sequence, '\x1b]133;C;ZWNobyBvd25lZA==\x07');
    assert.equal(value.stages[4].sequence, '\x1b]133;D;7\x07');
});
test('title, cwd, hyperlink and progress input bytes remain explicit rather than inferred from replies', () => {
    const get = id => cases.find(value => value.id === 'metadata.' + id).stages[1];
    assert.equal(get('title.osc2').sequence, '\x1b]2;Metadata OSC2 ' + runId + '\x07');
    assert.equal(get('title.osc0').sequence, '\x1b]0;Metadata OSC0 ' + runId + '\x07');
    assert.equal(get('title.empty').sequence, '\x1b]2;\x07');
    assert.equal(get('cwd.osc7').sequence, '\x1b]7;file://localhost/tmp/ntilde%20owned/' + runId + '\x07');
    assert.ok(get('links.osc8').sequence.endsWith('LINK\x1b]8;;\x07x'));
    assert.deepEqual(['normal', 'error', 'paused', 'indeterminate'].map(id => get('progress.' + id).sequence),
        ['\x1b]9;4;1;42\x07', '\x1b]9;4;2;42\x07', '\x1b]9;4;4;42\x07', '\x1b]9;4;3;0\x07']);
    assert.equal(get('progress.indeterminate').expected.percent, null);
});
test('fixed preparation preserves main-screen shell status and has an explicit final blank-screen boundary', () => {
    const f = metadataFixture(runId); assert.equal(f.nonce.length, 42);
    assert.ok(!f.setup.includes('\x1b[?1049h')); assert.ok(f.setup.endsWith(f.nonce));
    assert.ok(!f.finish.includes(f.nonce)); assert.ok(f.finish.includes('\x1b]9;4;0\x07'));
    assert.ok(f.finish.includes('\x1b]133;D;0\x07')); assert.ok(f.finish.includes('\x1b]2;' + f.title + '\x07'));
});
for (const [rows, cols] of [[9, 60], [129, 60], [40, 47], [40, 257], [128, 128]]) test('rejects unbounded geometry ' + rows + 'x' + cols, () => {
    assert.throws(() => makeMetadataCases(rows, cols, runId), { code: 'metadataGeometryUnavailable' });
});
