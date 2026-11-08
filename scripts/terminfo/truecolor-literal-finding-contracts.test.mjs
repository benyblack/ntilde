import assert from 'node:assert/strict';
import { test } from 'node:test';
import { truecolorFindingContracts, truecolorFindingCaseIds, truecolorFindingContractsVersion, truecolorFindingFeeds,
    truecolorFindingReads, truecolorFindingReset, validateTruecolorFindingFeeds, gradeTruecolorFindingCase,
    truecolorFindingObservation, snappedValues } from './truecolor-literal-finding-contracts.mjs';
import { findingBinding, findingCells, phases, syntheticTruecolorFindingGrade } from './fixtures/truecolor-literal-finding-evidence.mjs';
import { canonicalPalette, paletteBinding, colorCell, plainCell, paletteChannel, literalChannel, defaultChannel,
    syntheticSgrPaletteScreen } from './fixtures/sgr-palette-evidence.mjs';

const ids = truecolorFindingContracts.map(contract => contract.caseId);
const grade = (caseId, { cells, binding } = {}) => syntheticTruecolorFindingGrade(caseId, {
    cellsOf: cells ? (contract, phase) => cells(contract, phase, findingCells(contract)) : null,
    bindingOf: binding ? (contract, phase) => binding(contract, phase, findingBinding(contract, phase)) : null,
});
const withCell = (cells, position, cell) => { const copy = [...cells]; copy[position] = cell; return copy; };
const own = (contract, spec) => ({ [contract.channel]: spec });

test('the suite is a finding, not a terminfo.dev case: ten ids, fg then bg, five exact values each', () => {
    assert.equal(truecolorFindingContractsVersion, 1);
    assert.equal(ids.length, 10);
    assert.ok(ids.every(id => id.startsWith('finding.truecolor-literal.') && !id.startsWith('sgr.') && !id.startsWith('extensions.')));
    assert.deepEqual(ids.slice(0, 5), snappedValues.map(v => `finding.truecolor-literal.fg.${v.r}-${v.g}-${v.b}`));
    assert.deepEqual(ids.slice(5), snappedValues.map(v => `finding.truecolor-literal.bg.${v.r}-${v.g}-${v.b}`));
    assert.deepEqual([...truecolorFindingCaseIds], ids);
    assert.deepEqual(snappedValues.map(v => [v.r, v.g, v.b, v.index]),
        [[0, 55, 218, 4], [58, 150, 221, 4], [12, 12, 12, 0], [204, 204, 204, 7], [242, 242, 242, 15]]);
});

test('every case replays the exact fixture, mutation and restore bytes in the three supported forms', () => {
    const fg = truecolorFindingContracts[2], bg = truecolorFindingContracts[9];
    assert.equal(fg.fixture, 'B\x1b[38;5;0mI\x1b[0m\x1b[38;2;12;12;12mS\x1b[0m\x1b[38:2::12:12:12mC\x1b[0m\x1b[38:2:0:12:12:12mD\x1b[0m');
    assert.equal(bg.fixture, 'B\x1b[48;5;15mI\x1b[0m\x1b[48;2;242;242;242mS\x1b[0m\x1b[48:2::242:242:242mC\x1b[0m\x1b[48:2:0:242:242:242mD\x1b[0m');
    assert.equal(fg.mutate, '\x1b]4;0;rgb:ff/00/ff\x07');
    assert.equal(bg.restore, '\x1b]104;15\x07');
    for (const contract of truecolorFindingContracts) {
        assert.equal(truecolorFindingReads(contract), 3);
        assert.deepEqual(truecolorFindingFeeds(contract), [truecolorFindingReset, contract.fixture, contract.mutate, contract.restore]);
        assert.ok(!/[34]8;2;\d+;\d+;\d+;[0-9;]*[34]8/.test(contract.fixture), 'no combined single-sequence fg+bg form');
        assert.match(contract.scope, /rendered appearance is not claimed/);
    }
    assert.ok(truecolorFindingReset.endsWith('\x1b]104\x07'), 'the reset clears every palette override');
});

test('recorded feeds must be byte-identical to the contract', () => {
    const contract = truecolorFindingContracts[0];
    validateTruecolorFindingFeeds(contract, truecolorFindingFeeds(contract));
    assert.throws(() => validateTruecolorFindingFeeds(contract, [truecolorFindingReset, contract.fixture, contract.mutate.replace('ff/00/ff', 'ff/00/fe'), contract.restore]), /do not match/);
    assert.throws(() => validateTruecolorFindingFeeds(contract, []), /do not match/);
});

test('literal storage that survives the OSC 4 mutation is supported at both layers, with per-form findings', () => {
    for (const id of ids) {
        const result = grade(id);
        assert.equal(result.outcome, 'supported', id);
        assert.deepEqual(result.layers, { stored: 'supported', resolved: 'supported' });
        for (const form of ['semicolon', 'colon-omitted-colorspace', 'colon-explicit-colorspace']) {
            assert.equal(result.findings[form].stored, 'literal');
            assert.equal(result.findings[form].initialMatchesRequested, true);
            assert.equal(result.findings[form].changedByMutation, false);
        }
    }
});

test('the pre-fix shape (literal rewritten to the palette index) is unsupported, with its observable consequences', () => {
    for (const contract of truecolorFindingContracts) {
        const result = grade(contract.caseId, { cells: (c, phase) => findingCells(c, { aliased: true }) });
        assert.equal(result.outcome, 'unsupported', contract.caseId);
        assert.deepEqual(result.layers, { stored: 'unsupported', resolved: 'unsupported' });
        assert.match(result.note, new RegExp(`stored as palette index ${contract.index}`));
        for (const finding of Object.values(result.findings)) {
            assert.equal(finding.stored, `palette:${contract.index}`);
            assert.equal(finding.initialMatchesRequested, false, 'the default theme resolves the entry to a different color');
            assert.equal(finding.changedByMutation, true, 'the literal followed the OSC 4 change');
        }
    }
});

test('a single aliased form is enough to fail the case', () => {
    for (const position of [2, 3, 4]) {
        const result = grade(ids[3], { cells: (c, phase, faithful) => withCell(faithful, position, colorCell('SCD'[position - 2], own(c, paletteChannel(c.index)))) });
        assert.equal(result.outcome, 'unsupported');
        assert.equal(result.layers.stored, 'unsupported');
    }
});

test('altered literal bytes are unsupported only with valid controls; a stale resolved color is a resolved-layer failure', () => {
    for (const contract of truecolorFindingContracts) {
        const altered = grade(contract.caseId, { cells: (c, phase, faithful) => withCell(faithful, 2, colorCell('S',
            own(c, literalChannel(c.requested.r, c.requested.g, c.requested.b === 255 ? 254 : c.requested.b + 1)))) });
        assert.equal(altered.outcome, 'inconclusive', 'neither the requested literal nor the aliased index');
    }
    const contract = truecolorFindingContracts[0];
    const screens = phases.map(phase => syntheticSgrPaletteScreen(findingCells(contract), findingBinding(contract, phase)));
    const observation = truecolorFindingObservation(contract, screens);
    observation.mutated[2] = { ...observation.mutated[2], fg: { r: 255, g: 0, b: 255 } };
    const result = gradeTruecolorFindingCase(contract, observation);
    assert.equal(result.outcome, 'unsupported');
    assert.deepEqual(result.layers, { stored: 'supported', resolved: 'unsupported' });
});

test('invalid controls never become conclusive, even beside an aliased target', () => {
    const faithful = id => grade(id).outcome;
    assert.equal(faithful(ids[0]), 'supported');
    const bad = { cells: (c, phase) => findingCells(c, { aliased: true }) };
    for (const [name, options, note] of [
        ['mutation never applied', { ...bad, binding: (c, phase) => findingBinding(c, 'before') }, /palette-mutation control/],
        ['mutation applied to a different entry', { ...bad, binding: (c, phase, b) => {
            if (phase !== 'mutated') return b;
            const palette = canonicalPalette(); palette[(c.index + 1) % 16] = 0xffff00ff;
            return { ...paletteBinding({ palette }), paletteOverrideIndices: [(c.index + 1) % 16] }; } }, /palette-mutation control/],
        ['mutation also changed another entry', { ...bad, binding: (c, phase, b) => {
            if (phase !== 'mutated') return b;
            const palette = [...b.ansiPaletteArgb]; palette[200] = 0xff123456; return { ...b, ansiPaletteArgb: palette }; } }, /palette-mutation control/],
        ['palette not restored', { ...bad, binding: (c, phase, b) => (phase === 'restored' ? findingBinding(c, 'mutated') : b) }, /palette-mutation control/],
        ['wrong mutation color', { ...bad, binding: (c, phase, b) => {
            if (phase !== 'mutated') return b;
            const palette = [...b.ansiPaletteArgb]; palette[c.index] = 0xff00ff00; return { ...b, ansiPaletteArgb: palette }; } }, /palette-mutation control/],
        ['reverse video', { ...bad, binding: (c, phase, b) => ({ ...b, reverseVideo: true }) }, /DECSCNM/],
        ['baseline active', { cells: (c, phase, f) => withCell(findingCells(c, { aliased: true }), 0, colorCell('B', { attrs: ['italic'] })) }, /Neutral baseline/],
        ['indexed control stored as literal', { cells: (c, phase) => withCell(findingCells(c, { aliased: true }), 1, colorCell('I', own(c, literalChannel(1, 2, 3)))) }, /positive control/],
        ['indexed control on the wrong index', { cells: (c, phase) => withCell(findingCells(c, { aliased: true }), 1, colorCell('I', own(c, paletteChannel((c.index + 1) % 16)))) }, /positive control/],
        ['fixture cell missing', { cells: (c, phase) => withCell(findingCells(c, { aliased: true }), 3, plainCell('?')) }, /not exposed/],
        ['other channel drift', { cells: (c, phase) => withCell(findingCells(c, { aliased: true }), 2, colorCell('S', { ...own(c, paletteChannel(c.index)), [c.channel === 'fg' ? 'bg' : 'fg']: paletteChannel(3) })) }, /unaffected channel/],
        ['unrelated attribute drift', { cells: (c, phase) => withCell(findingCells(c, { aliased: true }), 4, colorCell('D', { ...own(c, paletteChannel(c.index)), attrs: ['overline'] })) }, /unaffected channel or unrelated attributes/],
        ['aliased entry already equals the request', { ...bad, binding: (c, phase) => {
            const palette = canonicalPalette(); palette[c.index] = 0xff000000 + c.requested.r * 65536 + c.requested.g * 256 + c.requested.b;
            const base = paletteBinding({ palette });
            if (phase !== 'mutated') return base;
            const mutated = [...palette]; mutated[c.index] = 0xffff00ff;
            return { ...paletteBinding({ palette: mutated }), paletteOverrideIndices: [c.index] }; } }, /already equals/],
    ]) {
        for (const id of [ids[0], ids[7]]) {
            const result = grade(id, options);
            assert.equal(result.outcome, 'inconclusive', `${id} ${name}`);
            assert.match(result.note, note, `${id} ${name}`);
        }
    }
});

test('the grader refuses unknown revisions and incomplete observations', () => {
    const contract = truecolorFindingContracts[0];
    const screens = phases.map(phase => syntheticSgrPaletteScreen(findingCells(contract), findingBinding(contract, phase)));
    const observation = truecolorFindingObservation(contract, screens);
    assert.equal(gradeTruecolorFindingCase(contract, observation).outcome, 'supported');
    assert.throws(() => gradeTruecolorFindingCase(contract, observation, 2), /Unknown truecolor-finding contracts revision/);
    assert.throws(() => gradeTruecolorFindingCase(contract, { ...observation, bindings: [] }), /Missing finding.truecolor-literal.fg.0-55-218 read captures/);
    assert.throws(() => gradeTruecolorFindingCase(contract, { ...observation, mutated: observation.mutated.slice(1) }), /Missing/);
    assert.throws(() => truecolorFindingObservation(contract, screens.slice(1)), /Missing truecolor-finding read captures/);
});

test('default markers, palette index 0 and literal black stay distinct end to end', () => {
    const contract = truecolorFindingContracts.find(item => item.requested.r === 12);
    for (const [name, spec] of [['default marker', defaultChannel], ['palette 0', paletteChannel(0)], ['palette 7', paletteChannel(7)]]) {
        const result = grade(contract.caseId, { cells: (c, phase) => withCell(findingCells(c), 2, colorCell('S', own(c, spec))) });
        assert.notEqual(result.outcome, 'supported', name);
    }
});
