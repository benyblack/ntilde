import assert from 'node:assert/strict';
import { boundaryPlan, caseIds } from './unicode-boundary-contracts.mjs';
import { charsetsCellAt, charsetsFlagBits } from './charsets-contracts.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { readRun, withMeasurement } from './unicode-boundary-publication.mjs';
import { testWithPinnedCollector } from './real-bundle-test.mjs';
import { pinnedUnicodeProbes } from './unicode-pinned-test.mjs';

testWithPinnedCollector('the original 4.0.0 callbacks agree for all three boundary cases', async bundle => {
    const probes = await pinnedUnicodeProbes(bundle);
    const { frames: records, report } = withMeasurement(readRun);
    const frames = Object.fromEntries(records.map(frame => [frame.stage, frame]));
    const { rows, cols } = records[0].screen;
    const plan = Object.fromEntries(boundaryPlan(cols).map(step => [step.stage, step]));
    const parserPlans = [
        ['ambiguous-seed', 'ambiguous-origin', 'ambiguous-target'],
        ['grapheme-seed', 'grapheme-origin', 'grapheme-target'],
        ['parser-wrap-clear', 'parser-wrap-seed', 'parser-wrap-origin', 'parser-wrap-target'],
    ];
    for (const [index, id] of caseIds.entries()) {
        const probe = probes.find(item => item.id === id), planned = parserPlans[index];
        assert.ok(probe, id);
        let count = 0, screen;
        const termless = probe.termless({
            cols, getScrollback: () => ({ screenLines: rows }),
            feed(sequence) {
                const stage = planned[count++];
                assert.equal(sequence, plan[stage].bytes, stage);
                screen = frames[stage].screen;
            },
            getCursor: () => ({ x: screen.cursorCol, y: screen.cursorRow, visible: screen.cursorVisible }),
            getCell(row, col) {
                if (id === caseIds[1] && count === 3 && row === 0 && col === 0) {
                    const cells = expandCellDetails(screen.cellDetails, rows * cols);
                    const { wide, continuation, extended } = charsetsFlagBits;
                    assert.equal(cells[0].flags & (wide | extended), wide | extended);
                    assert.equal(cells[1].flags & continuation, continuation);
                    assert.ok(cells.slice(1).every(cell => (cell.flags & extended) === 0));
                    assert.equal(screen.lines[0], plan['grapheme-target'].bytes);
                    return { char: screen.lines[0], wide: true };
                }
                return charsetsCellAt(screen, row, col);
            },
        });
        assert.equal(count, planned.length, id);
        assert.equal(termless.observation.outcome, 'supported', id);
        let exchanges = 0;
        const writes = [], row = report.rows[index];
        const terminal = await probe.term({
            cols, rows, write: value => writes.push(value),
            queryCursorPosition: async () => {
                const { row, col } = frames['terminal-wrap-target'].reply;
                return { row, col };
            },
            measureRenderedWidth: async sample => {
                assert.equal(sample, plan[index === 0 ? 'ambiguous-width' : 'grapheme-width'].sample);
                exchanges++;
                return row.ptyWidth;
            },
        }); // NOSONAR: replay each callback with its own fixture.
        if (index === 2) {
            assert.deepEqual(writes, ['terminal-wrap-clear', 'terminal-wrap-seed', 'terminal-wrap-target'].map(stage => plan[stage].bytes));
            assert.equal(exchanges, 0);
        } else {
            assert.equal(exchanges, 1);
        }
        assert.equal(terminal.observation.outcome, row.terminal, id);
    }
});
