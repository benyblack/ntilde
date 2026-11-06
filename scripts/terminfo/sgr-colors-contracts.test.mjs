import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrColorsContracts, sgrColorsCaseIds, sgrColorsContractsVersion, sgrColorsSuiteId,
    sgrColorsFeeds, sgrColorsReads, validateSgrColorsFeeds, gradeSgrColorsCase,
    sgrColorsCellAt, defaultColors, fgDefaultMarker, bgDefaultMarker } from './sgr-colors-contracts.mjs';
import { expandScreenFlags } from './ascii-sgr-contracts.mjs';
import { syntheticSgrColorsScreen, syntheticSgrColorsCells, syntheticSgrColorsGrade } from './fixtures/sgr-colors-evidence.mjs';

test('the batch covers exactly the five contracted terminfo.dev cases in a fixed order', () => {
    assert.equal(sgrColorsContractsVersion, 2);
    assert.equal(sgrColorsSuiteId, 'ntilde-owned-sgr-colors-graded-v1');
    assert.deepEqual([...sgrColorsCaseIds], ['sgr.selective-reset.bold', 'sgr.selective-reset.italic',
        'sgr.reset', 'sgr.fg.standard', 'sgr.bg.standard']);
});

test('contracts pin the exact pinned fixture bytes for every case', () => {
    const feeds = Object.fromEntries(sgrColorsContracts.map(contract => [contract.caseId, sgrColorsFeeds(contract).slice(1)]));
    assert.deepEqual(feeds['sgr.selective-reset.bold'], ['C\x1b[1;2;3mX\x1b[22mY']);
    assert.deepEqual(feeds['sgr.selective-reset.italic'], ['C\x1b[1;3mX\x1b[23mY']);
    assert.deepEqual(feeds['sgr.reset'], ['C\x1b[1;3;4mX\x1b[0mY']);
    assert.deepEqual(feeds['sgr.fg.standard'], ['C\x1b[31mX\x1b[34mY']);
    assert.deepEqual(feeds['sgr.bg.standard'], ['C\x1b[41mX\x1b[44mY']);
    assert.deepEqual(sgrColorsContracts.map(sgrColorsReads), [1, 1, 1, 1, 1]);
});

test('wrong input variants are rejected against the contract bytes', () => {
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.reset');
    for (const variant of [
        ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', 'C\x1b[1;3mX\x1b[0mY'],
        ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H', 'C\x1b[1;3;4mX\x1b[0mY', 'extra'],
        ['\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H'],
        [],
    ]) assert.throws(() => validateSgrColorsFeeds(contract, variant), /do not match the pinned contract/);
    assert.doesNotThrow(() => validateSgrColorsFeeds(contract, sgrColorsFeeds(contract)));
});

test('contract grader passes the observed positive screens', () => {
    for (const caseId of sgrColorsCaseIds) {
        const graded = syntheticSgrColorsGrade(caseId);
        assert.equal(graded.outcome, 'supported', caseId);
        assert.equal(graded.note, undefined);
    }
});

function screenWith(caseId, mutate) {
    const screen = structuredClone(syntheticSgrColorsScreen(caseId));
    mutate(screen);
    return screen;
}

const cellsOf = screen => [0, 1, 2].map(col => sgrColorsCellAt(screen, 0, col));
const gradeOf = (contract, screen) => gradeSgrColorsCase(contract, cellsOf(screen));

test('initially-unset controls keep failures inconclusive', () => {
    // The styled X cell never became bold/dim/italic: the control was not measured.
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.selective-reset.bold');
    const graded = gradeOf(contract, screenWith('sgr.selective-reset.bold', screen => {
        screen.attributeRuns = [{ count: 360, flags: 768, fg: -1, bg: -1 }];
    }));
    assert.equal(graded.outcome, 'inconclusive');
    assert.equal(graded.note, 'Neutral baseline, styled setup, or reset readback was not measured');
});

test('accidental clearing of an unrelated attribute is unsupported, not inconclusive', () => {
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.selective-reset.bold');
    // SGR 22 also cleared the preserved italic: a measured mismatch.
    const graded = gradeOf(contract, screenWith('sgr.selective-reset.bold', screen => {
        screen.attributeRuns[2] = { count: 1, flags: 768, fg: -1, bg: -1 };
    }));
    assert.equal(graded.outcome, 'unsupported');
    // And a control cell that already carries the target attribute is inconclusive.
    const stale = gradeOf(contract, screenWith('sgr.selective-reset.bold', screen => {
        screen.attributeRuns[0] = { count: 1, flags: 768 | 0x01, fg: -1, bg: -1 };
    }));
    assert.equal(stale.outcome, 'inconclusive');
});

test('a setup cell with color drift keeps the control inconclusive', () => {
    const setupStyles = {
        'sgr.selective-reset.bold': 768 | 0x01 | 0x40 | 0x04,
        'sgr.selective-reset.italic': 768 | 0x01 | 0x04,
        'sgr.reset': 768 | 0x01 | 0x04 | 0x08,
    };
    for (const [caseId, styledFlags] of Object.entries(setupStyles)) {
        const contract = sgrColorsContracts.find(item => item.caseId === caseId);
        // X carries a palette foreground color the pinned fixture never set.
        const graded = gradeOf(contract, screenWith(caseId, screen => {
            screen.attributeRuns = [
                { count: 1, flags: 768, fg: -1, bg: -1 },
                { count: 1, flags: styledFlags | 0x4000, fg: 2, bg: -1 },
                { count: 358, flags: 768, fg: -1, bg: -1 },
            ];
        }));
        assert.equal(graded.outcome, 'inconclusive', caseId);
        assert.equal(graded.note, 'Neutral baseline, styled setup, or reset readback was not measured', caseId);
    }
});

test('selective-reset.italic clearing the preserved bold is unsupported', () => {
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.selective-reset.italic');
    const graded = gradeOf(contract, screenWith('sgr.selective-reset.italic', screen => {
        screen.attributeRuns[2] = { count: 1, flags: 768, fg: -1, bg: -1 };
    }));
    assert.equal(graded.outcome, 'unsupported');
});

test('full reset leaving palette-zero colors instead of default markers is unsupported', () => {
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.reset');
    const rebuild = after => screen => {
        screen.attributeRuns = [{ count: 1, flags: 768, fg: -1, bg: -1 },
            { count: 1, flags: 768 | 0x01 | 0x04 | 0x08, fg: -1, bg: -1 }, after,
            { count: 357, flags: 768, fg: -1, bg: -1 }];
    };
    // SGR 0 "restored" colors to palette index 0 rather than the default
    // markers: default-versus-explicit-color confusion is a measured mismatch.
    const graded = gradeOf(contract, screenWith('sgr.reset',
        rebuild({ count: 1, flags: 0x0200 | 0x4000, fg: 0, bg: -1 })));
    assert.equal(graded.outcome, 'unsupported');
    // A style surviving the reset is likewise a measured mismatch.
    const surviving = gradeOf(contract, screenWith('sgr.reset',
        rebuild({ count: 1, flags: 768 | 0x01, fg: -1, bg: -1 })));
    assert.equal(surviving.outcome, 'unsupported');
});

test('contracts v1 keeps its measured control semantics for older evidence', () => {
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.selective-reset.bold');
    const screen = screenWith('sgr.selective-reset.bold', value => {
        value.attributeRuns = [
            { count: 1, flags: 768, fg: -1, bg: -1 },
            { count: 1, flags: (768 | 0x01 | 0x40 | 0x04) | 0x4000, fg: 2, bg: -1 },
            { count: 1, flags: 768 | 0x04, fg: -1, bg: -1 },
            { count: 357, flags: 768, fg: -1, bg: -1 },
        ];
    });
    const cells = cellsOf(screen);
    // v1 (as measured): the colored setup cell does not invalidate the control.
    assert.equal(gradeSgrColorsCase(contract, cells, 1).outcome, 'supported');
    // v2 (current): color drift on the setup cell keeps the control invalid.
    assert.equal(gradeSgrColorsCase(contract, cells, 2).outcome, 'inconclusive');
    assert.throws(() => gradeSgrColorsCase(contract, cells, 3), /Unknown SGR-colors contracts revision/);
});

test('contracts v1 keeps the measured full-reset control for setup-cell dim', () => {
    const contract = sgrColorsContracts.find(item => item.caseId === 'sgr.reset');
    // The setup cell also carries faint: the measured v1 contract did not
    // check unrelated setup attributes for the full reset; v2 does.
    const screen = screenWith('sgr.reset', value => {
        value.attributeRuns = [
            { count: 1, flags: 768, fg: -1, bg: -1 },
            { count: 1, flags: 768 | 0x01 | 0x04 | 0x08 | 0x40, fg: -1, bg: -1 },
            { count: 1, flags: 768, fg: -1, bg: -1 },
            { count: 357, flags: 768, fg: -1, bg: -1 },
        ];
    });
    const cells = cellsOf(screen);
    assert.equal(gradeSgrColorsCase(contract, cells, 1).outcome, 'supported');
    assert.equal(gradeSgrColorsCase(contract, cells, 2).outcome, 'inconclusive');
});

test('wrong palette indices are unsupported while exact indices pass', () => {
    const fg = sgrColorsContracts.find(item => item.caseId === 'sgr.fg.standard');
    const wrongIndex = gradeOf(fg, screenWith('sgr.fg.standard', screen => {
        screen.attributeRuns[1] = { count: 1, flags: 0x0200 | 0x4000, fg: 2, bg: -1 };
    }));
    assert.equal(wrongIndex.outcome, 'unsupported');
    const defaultConfusion = gradeOf(fg, screenWith('sgr.fg.standard', screen => {
        // X kept the default marker: SGR 31 did not select a palette index.
        screen.attributeRuns[1] = { count: 1, flags: 768, fg: -1, bg: -1 };
    }));
    assert.equal(defaultConfusion.outcome, 'unsupported');
    const bg = sgrColorsContracts.find(item => item.caseId === 'sgr.bg.standard');
    const wrongBg = gradeOf(bg, screenWith('sgr.bg.standard', screen => {
        screen.attributeRuns[2] = { count: 1, flags: 0x0100 | 0x8000, fg: -1, bg: 5 };
    }));
    assert.equal(wrongBg.outcome, 'unsupported');
});

test('the v2 cell projection distinguishes markers, indices and unexposed RGB', () => {
    const plain = cellsOf(syntheticSgrColorsScreen('sgr.reset'));
    assert.ok(plain.every(cell => defaultColors(cell) && cell.fg === null && cell.bg === null && cell.fgIndex === -1 && cell.bgIndex === -1));
    const fgCells = cellsOf(syntheticSgrColorsScreen('sgr.fg.standard'));
    assert.equal(fgCells[1].fg, undefined);
    assert.equal(fgCells[1].fgIndex, 1);
    assert.equal(fgCells[1].bg, null);
    assert.ok(bgDefaultMarker(fgCells[1]) && !fgDefaultMarker(fgCells[1]));
    const bgCells = cellsOf(syntheticSgrColorsScreen('sgr.bg.standard'));
    assert.equal(bgCells[1].bg, undefined);
    assert.equal(bgCells[1].bgIndex, 1);
    assert.ok(fgDefaultMarker(bgCells[1]) && !bgDefaultMarker(bgCells[1]));
    assert.equal(syntheticSgrColorsCells('sgr.reset')[1].underline, true);
    assert.equal(syntheticSgrColorsCells('sgr.selective-reset.bold')[1].dim, true);
});

test('the shared attribute flag expansion rejects incomplete or invalid runs', () => {
    const base = syntheticSgrColorsScreen('sgr.fg.standard');
    assert.equal(expandScreenFlags(base).length, 360);
    const short = structuredClone(base); short.attributeRuns = [{ count: 10, flags: 768, fg: -1, bg: -1 }];
    assert.throws(() => expandScreenFlags(short), /Incomplete graded attribute runs/);
    const invalid = structuredClone(base); invalid.attributeRuns = [{ count: 360, flags: 0x1ffff, fg: -1, bg: -1 }];
    assert.throws(() => expandScreenFlags(invalid), /Invalid graded attribute runs/);
});
