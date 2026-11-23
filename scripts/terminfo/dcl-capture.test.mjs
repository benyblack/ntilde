import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DclObserver, kernelGrid } from './dcl-capture.mjs';
import { sttyDescription } from './fixtures/dcl-evidence.mjs';

const output = description => ({ tool: '/bin/stty', outputProcessing: false, description });

test('the kernel grid is the rows and columns the system stty reports, with the output processing disabled', () => {
    assert.deepEqual(kernelGrid(output(sttyDescription(40, 140))), { tool: '/bin/stty', rows: 40, cols: 140, outputProcessing: false, description: sttyDescription(40, 140) });
    const linux = 'speed 38400 baud; rows 40; columns 140; line = 0;\n-opost';
    assert.throws(() => kernelGrid(output(linux)), /no grid/, 'a spelling the macOS stty never uses is not guessed');
});

test('a missing, foreign or contradictory kernel fact is refused, never turned into a size', () => {
    assert.throws(() => kernelGrid(undefined), /not read by the system stty/);
    assert.throws(() => kernelGrid({ ...output(sttyDescription(40, 140)), tool: '/usr/bin/true' }), /not read by the system stty/);
    assert.throws(() => kernelGrid({ ...output(sttyDescription(40, 140)), outputProcessing: true }), /not read by the system stty/);
    assert.throws(() => kernelGrid(output('speed 9600 baud; 40 rows; 140 columns;\noflags: opost')), /not disabled/);
    assert.throws(() => kernelGrid(output('speed 9600 baud;\n-opost')), /no grid/);
    assert.throws(() => kernelGrid(output('speed 9600 baud; 0 rows; 140 columns;\n-opost')), /positive size/);
    assert.throws(() => kernelGrid(output('x'.repeat(5000))), /Invalid private PTY output description/);
});

test('the observer needs a kernel reader from the parent', () => {
    assert.throws(() => new DclObserver({ nonce: 'NTILDE_DCL_' + 'a'.repeat(32) }), /needs a kernel PTY reader/);
});
