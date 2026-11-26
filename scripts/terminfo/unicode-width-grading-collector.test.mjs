import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { cases, clearRow, deriveWidth, describeFrame, gradeCase, widthWrites } from './unicode-width-contracts.mjs';
import { withMeasurement } from './unicode-width-publication.mjs';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { pinnedUnicodeProbes } from './unicode-pinned-test.mjs';

const raw = (row, col) => ({ raw: `\x1b[${row};${col}R`, row, col });
const barrier = '\x1b[?62;4;22c';
const exchanges = (sample, width) => widthWrites(sample).map((bytes, index) => ({
    bytes, exchange: { barrier, reply: [raw(2, 1), raw(1, 2), raw(1, 1 + width), raw(1, 2 + width)][index] },
}));

test('CPR derives only a calibrated width with a coherent ASCII-following step', () => {
    const item = cases[0], valid = exchanges(item.sample, 1);
    assert.equal(deriveWidth(item.sample, valid), 1);
    const absent = structuredClone(valid);
    absent[2].exchange.reply = null;
    assert.equal(deriveWidth(item.sample, absent), null);
    const wrongFollow = structuredClone(valid);
    wrongFollow[3].exchange.reply = raw(1, 2);
    assert.equal(deriveWidth(item.sample, wrongFollow), null);
    const wrongBytes = structuredClone(valid);
    wrongBytes[2].bytes = 'A';
    assert.throws(() => deriveWidth(item.sample, wrongBytes), /fixture bytes differ/);
    const wrongProjection = structuredClone(valid);
    wrongProjection[2].exchange.reply.col = 9;
    assert.throws(() => deriveWidth(item.sample, wrongProjection), /disagrees with its raw bytes/);
});

const journal = path => readFileSync(path + '/unicode-width-frames.jsonl', 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));

test('same-width wrong clusters or cells fail the owned effect while bad controls make it inconclusive', () => {
    withMeasurement(path => {
        const item = cases[0], frames = journal(path).filter(frame => frame.id === item.id);
        const [seed, blank, target] = frames.slice(0, 3).map(frame => describeFrame(frame.screen));
        const width = { ascii: frames[3].measurements.ascii.frames, sample: frames[3].measurements.sample.frames };
        assert.equal(gradeCase(item, seed, blank, target, width).effect, 'supported');
        const dropped = { ...target, lines: ['eX', ...target.lines.slice(1)] };
        const delta = gradeCase(item, seed, blank, dropped, width);
        assert.equal(delta.terminal, 'supported');
        assert.equal(delta.effect, 'unsupported');
        const stale = { ...blank, lines: ['A', ...blank.lines.slice(1)] };
        assert.equal(gradeCase(item, seed, stale, target, width).effect, 'inconclusive');
    });
});

function parserProjection(item, seed, target) {
    const row = target.lines[0], hasSuffix = item.suffix.length > 0;
    const text = hasSuffix ? row.slice(0, -1) : row;
    const anchor = target.flags[0].flags;
    const wide = (anchor & 0x800) !== 0;
    assert.equal(anchor & 0x2000, 0x2000);
    assert.equal(wide, item.width === 2);
    assert.equal(target.flags[1].flags & 0x1000, wide ? 0x1000 : 0);
    const parserCells = [{ char: text, wide }];
    if (wide) parserCells.push({ char: '', wide: false });
    if (hasSuffix) parserCells.push({ char: row.at(-1), wide: false });
    const ascii = [...seed.lines[0]].map(char => ({ char, wide: false }));
    const feeds = [item.seed, clearRow, item.sample + item.suffix];
    let seen = 0;
    return {
        context: {
            feed(bytes) { assert.equal(bytes, feeds[seen++]); },
            getCell(rowIndex, col) { assert.equal(rowIndex, 0); return (seen === 1 ? ascii : parserCells)[col]; },
        },
        complete: () => assert.equal(seen, feeds.length),
    };
}

testWithPinnedCollector('Unicode replay matches both pinned callbacks on the retained App run', async bundleUrl => {
    const probes = await pinnedUnicodeProbes(bundleUrl);
    const frames = withMeasurement(path => journal(path));
    for (const item of cases) {
        const [seedFrame, blankFrame, targetFrame, width] = frames.filter(frame => frame.id === item.id);
        const seed = describeFrame(seedFrame.screen), target = describeFrame(targetFrame.screen);
        const report = gradeCase(item, seed, describeFrame(blankFrame.screen), target,
            { ascii: width.measurements.ascii.frames, sample: width.measurements.sample.frames });
        const probe = probes.find(candidate => candidate.id === item.id);
        assert.ok(probe, item.id);
        const parser = parserProjection(item, seed, target);
        const termless = probe.termless(parser.context);
        parser.complete();
        assert.equal(termless.observation.outcome, 'supported', item.id);
        const reads = [item.width === 1 ? 'A' : 'AA', item.sample];
        const widths = [report.asciiWidth, report.sampleWidth];
        let seen = 0;
        const terminal = await probe.term({
            cols: targetFrame.screen.cols, rows: targetFrame.screen.rows,
            async measureRenderedWidth(sample) {
                assert.equal(sample, reads[seen]);
                return widths[seen++];
            },
        }); // NOSONAR: trace the original callback in case order.
        assert.equal(seen, reads.length);
        assert.equal(terminal.observation.outcome, report.terminal, item.id);
        assert.deepEqual(JSON.parse(terminal.response), { ascii: report.asciiWidth, width: report.sampleWidth });
    }
});
