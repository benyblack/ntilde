import assert from 'node:assert/strict';
import test from 'node:test';
import { gradeViewport, gradeViewportPhase, viewportFixture } from './modern-viewport.mjs';

const runId = 'a'.repeat(32), rows = 24;
function experiment(phase = 'plain') {
    const { lines, marker } = viewportFixture(runId, phase, rows);
    const capture = (labels, offset) => {
        const extent = labels.length + 1 - rows;
        return { history: labels.slice(0, extent), live: [...labels.slice(extent), ''],
            offset, extent, rowsEvicted: 0, generation: 1 };
    };
    return { phase, runId, bottom: capture(lines, 0), anchor: capture(lines, rows + 1),
        output: capture([...lines, marker], rows + 2), restored: capture([...lines, marker], 0) };
}

test('both phases retain row identities when the distance from the bottom grows', () => {
    const phases = ['plain', 'synchronized'].map(phase => gradeViewportPhase(experiment(phase)));
    assert.equal(gradeViewport(phases), 'pass');
    for (const phase of phases) {
        assert.deepEqual(phase.output.visibleRows, phase.anchor.visibleRows);
        assert.equal(phase.output.offset, phase.anchor.offset + 1);
        assert.equal(phase.restored.visibleRows.at(-2), phase.marker);
    }
    assert.notEqual(phases[0].marker, phases[1].marker);
});

test('exact synchronized bytes enclose one independent marker line', () => {
    const plain = viewportFixture(runId, 'plain', rows), sync = viewportFixture(runId, 'synchronized', rows);
    assert.equal(plain.markerBytes, plain.marker + '\r\n');
    assert.equal(sync.markerBytes, '\x1b[?2026h' + sync.marker + '\r\n\x1b[?2026l');
    assert.ok(plain.lines.every(line => !sync.lines.includes(line)));
});

test('a snap requires matching the separately verified new bottom', () => {
    const run = experiment();
    run.output.offset = 0;
    assert.equal(gradeViewportPhase(run).conclusion, 'fail');
    run.restored.offset = 1;
    assert.equal(gradeViewportPhase(run).conclusion, 'inconclusive');
});

test('stable distance, missing output, failed movement and eviction stay inconclusive', () => {
    for (const mutate of [
        run => { run.output.offset = run.anchor.offset; },
        run => { run.output.live[rows - 2] = ''; },
        run => { run.anchor.offset = 0; },
        run => { run.output.rowsEvicted++; },
    ]) {
        const run = experiment();
        mutate(run);
        assert.equal(gradeViewportPhase(run).conclusion, 'inconclusive');
    }
});
