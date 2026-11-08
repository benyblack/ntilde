import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrPaletteContracts, sgrPaletteCaseIds, sgrPaletteContractsVersion, sgrPaletteFeeds, sgrPaletteReads,
    sgrPaletteReset, sgrPaletteClear, validateSgrPaletteFeeds, gradeSgrPaletteCase, sgrPaletteObservation,
    sgrPaletteCellAt, cubeReferences } from './sgr-palette-contracts.mjs';
import { canonicalPalette, paletteBinding, syntheticSgrPaletteReads, syntheticSgrPaletteScreens, syntheticSgrPaletteScreen,
    syntheticSgrPaletteGrade, colorCell, plainCell, paletteChannel, literalChannel, defaultChannel } from './fixtures/sgr-palette-evidence.mjs';

/** Grade one case after mutating only that case's synthetic reads. */
const grade = (caseId, mutate, binding) => syntheticSgrPaletteGrade(caseId, binding, id => {
    const reads = syntheticSgrPaletteReads(id);
    if (id === caseId) mutate?.(reads);
    return reads;
});
const withPalette = edit => { const palette = canonicalPalette(); edit(palette); return paletteBinding({ palette }); };
const channelOf = caseId => (caseId.includes('.fg.') ? 'fg' : 'bg');
const cellWith = (cell, channel, spec, attrs) => colorCell(cell.char, { ...cell, [channel]: spec, attrs: attrs ?? cell.attrs });

test('the contracts cover exactly the six pinned ids in qualification order', () => {
    assert.equal(sgrPaletteContractsVersion, 1);
    assert.deepEqual([...sgrPaletteCaseIds], ['sgr.fg.bright', 'sgr.bg.bright', 'sgr.fg.256', 'sgr.bg.256', 'sgr.fg.default', 'sgr.bg.default']);
    for (const contract of sgrPaletteContracts) {
        assert.equal(contract.probeId, contract.caseId);
        assert.equal(sgrPaletteReads(contract), 2);
        assert.deepEqual(sgrPaletteFeeds(contract), [sgrPaletteReset, contract.fixture, sgrPaletteClear, contract.control]);
        assert.deepEqual([...contract.pinnedFeeds], [contract.fixture]);
        assert.match(contract.appearanceScope, /rendered appearance is not claimed/);
    }
});

test('every fixture is the exact pinned terminfo.dev@4.0.0 byte sequence', () => {
    const fixtures = Object.fromEntries(sgrPaletteContracts.map(contract => [contract.caseId, contract.fixture]));
    assert.deepEqual(fixtures, {
        'sgr.fg.bright': 'C\x1b[91mX\x1b[94mY',
        'sgr.bg.bright': 'C\x1b[101mX\x1b[104mY',
        'sgr.fg.256': 'C\x1b[38;2;95;135;175mA\x1b[38;2;215;135;95mB\x1b[38;5;67mX\x1b[38;5;173mY',
        'sgr.bg.256': 'C\x1b[48;2;95;135;175mA\x1b[48;2;215;135;95mB\x1b[48;5;67mX\x1b[48;5;173mY',
        'sgr.fg.default': 'C\x1b[31mX\x1b[39mR',
        'sgr.bg.default': 'C\x1b[42mX\x1b[49mR',
    });
    assert.deepEqual(cubeReferences.map(item => item.index), [67, 173]);
});

test('every case is supported on faithful synthetic evidence with both layers recorded', () => {
    for (const contract of sgrPaletteContracts) {
        const result = syntheticSgrPaletteGrade(contract.caseId);
        assert.deepEqual(result, { outcome: 'supported', layers: { stored: 'supported', resolved: 'supported' } }, contract.caseId);
    }
});

test('recorded feeds must be byte-identical to the contract', () => {
    const contract = sgrPaletteContracts[0];
    validateSgrPaletteFeeds(contract, sgrPaletteFeeds(contract));
    assert.throws(() => validateSgrPaletteFeeds(contract, [sgrPaletteReset, 'C\x1b[31mX\x1b[34mY', sgrPaletteClear, contract.control]), /do not match/);
    assert.throws(() => validateSgrPaletteFeeds(contract, []), /do not match/);
});

test('bright: wrong indices, the standard indices and literal storage are unsupported', () => {
    for (const caseId of ['sgr.fg.bright', 'sgr.bg.bright']) {
        const channel = channelOf(caseId);
        for (const [name, spec] of [['standard index', paletteChannel(1)], ['wrong bright index', paletteChannel(10)], ['literal', literalChannel(255, 0, 0)], ['default marker', defaultChannel]]) {
            // A faithfully wrong App stores the same thing for the target and its repeat on the control row.
            const result = grade(caseId, reads => {
                reads.fixture[1] = cellWith(reads.fixture[1], channel, spec);
                reads.control[1] = cellWith(reads.control[1], channel, spec);
            });
            assert.equal(result.outcome, 'unsupported', `${caseId} ${name}`);
            assert.equal(result.layers.stored, 'unsupported');
            assert.equal(result.layers.resolved, 'not-evaluated');
        }
        const second = grade(caseId, reads => { reads.fixture[2] = cellWith(reads.fixture[2], channel, paletteChannel(4)); });
        assert.equal(second.outcome, 'unsupported', caseId);
    }
});

test('bright: selecting bright through bold/intensity is unsupported, never confused with bright indices', () => {
    for (const caseId of ['sgr.fg.bright', 'sgr.bg.bright']) {
        const channel = channelOf(caseId);
        const boldStandard = grade(caseId, reads => {
            reads.fixture[1] = cellWith(reads.fixture[1], channel, paletteChannel(1), ['bold']);
            reads.control[1] = cellWith(reads.control[1], channel, paletteChannel(1), ['bold']);
        });
        assert.equal(boldStandard.outcome, 'unsupported');
        assert.match(boldStandard.note, /bold\/intensity/);
        const boldRightIndex = grade(caseId, reads => {
            reads.fixture[1] = cellWith(reads.fixture[1], channel, paletteChannel(9), ['bold']);
            reads.control[1] = cellWith(reads.control[1], channel, paletteChannel(9), ['bold']);
        });
        assert.equal(boldRightIndex.outcome, 'unsupported', 'bold on the target is not bright selection');
    }
});

test('bright: the bold control must store the STANDARD index plus the bold flag; a RGB-looking bright cannot pass', () => {
    for (const caseId of ['sgr.fg.bright', 'sgr.bg.bright']) {
        const channel = channelOf(caseId);
        for (const [name, mutate] of [
            ['bold promoted to the bright index', reads => { reads.control[0] = cellWith(reads.control[0], channel, paletteChannel(9), ['bold']); }],
            ['bold flag missing', reads => { reads.control[0] = cellWith(reads.control[0], channel, paletteChannel(1), []); }],
            ['repeat drifted from the target', reads => { reads.control[1] = cellWith(reads.control[1], channel, paletteChannel(12)); }],
            ['repeat stayed bold', reads => { reads.control[1] = cellWith(reads.control[1], channel, paletteChannel(9), ['bold']); }],
            ['reset did not clear', reads => { reads.control[2] = cellWith(reads.control[2], channel, paletteChannel(1)); }],
        ]) {
            const result = grade(caseId, mutate);
            assert.equal(result.outcome, 'inconclusive', `${caseId} ${name}`);
            assert.match(result.note, /Bold-versus-bright/);
        }
    }
});

test('bright: aliased or indistinguishable palette entries stay inconclusive, never unsupported', () => {
    for (const [name, binding] of [
        ['bright entries aliased', withPalette(palette => { palette[12] = palette[9]; })],
        ['bright aliased to the standard entry', withPalette(palette => { palette[9] = palette[1]; palette[12] = palette[4]; })],
    ]) {
        const fg = syntheticSgrPaletteGrade('sgr.fg.bright', binding);
        if (name === 'bright entries aliased') {
            assert.equal(fg.outcome, 'inconclusive', name);
            assert.equal(fg.note, 'Baseline, theme colors, or color readback were indistinguishable');
            assert.deepEqual(fg.layers, { stored: 'supported', resolved: 'inconclusive' });
        } else {
            // Distinct from each other and from the default: the pinned criterion holds even though the
            // bright entries repeat the standard RGB values; stored indices carry the exact claim.
            assert.equal(fg.outcome, 'supported', name);
        }
    }
    const sameAsDefault = syntheticSgrPaletteGrade('sgr.bg.bright', paletteBinding({ background: canonicalPalette()[9] }));
    assert.equal(sameAsDefault.outcome, 'supported', 'a default channel is the null marker, distinct by definition');
});

test('bright: unrelated attributes or the unaffected channel drifting invalidates the control', () => {
    for (const caseId of ['sgr.fg.bright', 'sgr.bg.bright']) {
        const channel = channelOf(caseId), other = channel === 'fg' ? 'bg' : 'fg';
        const italic = grade(caseId, reads => { reads.fixture[1] = cellWith(reads.fixture[1], channel, paletteChannel(9), ['italic']); });
        assert.equal(italic.outcome, 'inconclusive');
        const otherDrift = grade(caseId, reads => { reads.fixture[2] = cellWith(reads.fixture[2], other, paletteChannel(2)); });
        assert.equal(otherDrift.outcome, 'inconclusive');
        assert.match(otherDrift.note, /unaffected channel/);
    }
});

test('256: wrong indices and literal or default storage of the target cells are unsupported', () => {
    for (const caseId of ['sgr.fg.256', 'sgr.bg.256']) {
        const channel = channelOf(caseId);
        for (const [position, spec, name] of [
            [3, paletteChannel(68), 'wrong first index'], [4, paletteChannel(172), 'wrong second index'],
            [3, literalChannel(95, 135, 175), 'palette cell stored as literal bytes'], [4, defaultChannel, 'default marker'],
            [3, paletteChannel(173), 'swapped indices'],
        ]) {
            const result = grade(caseId, reads => { reads.fixture[position] = cellWith(reads.fixture[position], channel, spec); });
            assert.equal(result.outcome, 'unsupported', `${caseId} ${name}`);
            assert.equal(result.layers.stored, 'unsupported');
        }
    }
});

test('256: the direct-RGB references must be literal storage, not palette indices', () => {
    for (const caseId of ['sgr.fg.256', 'sgr.bg.256']) {
        const channel = channelOf(caseId);
        for (const [name, mutate] of [
            ['reference stored as palette', reads => { reads.fixture[1] = cellWith(reads.fixture[1], channel, paletteChannel(67)); }],
            ['reference with other bytes', reads => { reads.fixture[2] = cellWith(reads.fixture[2], channel, literalChannel(215, 135, 96)); }],
            ['reference default', reads => { reads.fixture[1] = cellWith(reads.fixture[1], channel, defaultChannel); }],
        ]) {
            const result = grade(caseId, mutate);
            assert.equal(result.outcome, 'inconclusive', `${caseId} ${name}`);
            assert.match(result.note, /direct-RGB reference/);
        }
    }
});

test('256: palette changes and aliasing keep the pinned inconclusive, with stored storage still exact', () => {
    for (const [name, binding] of [
        ['index 67 overridden', withPalette(palette => { palette[67] = 0xff010203; })],
        ['index 173 overridden', withPalette(palette => { palette[173] = 0xff010203; })],
        ['indices aliased', withPalette(palette => { palette[173] = palette[67]; })],
        ['cube entries shifted', withPalette(palette => { [palette[67], palette[173]] = [palette[173], palette[67]]; })],
    ]) {
        for (const caseId of ['sgr.fg.256', 'sgr.bg.256']) {
            const result = syntheticSgrPaletteGrade(caseId, binding);
            assert.equal(result.outcome, 'inconclusive', `${caseId} ${name}`);
            assert.equal(result.note, 'RGB reference was not calibrated, or the backend may use a custom indexed palette');
            assert.deepEqual(result.layers, { stored: 'supported', resolved: 'inconclusive' });
        }
    }
});

test('256: boundary-index controls separate palette index 0/255 from the default marker', () => {
    for (const caseId of ['sgr.fg.256', 'sgr.bg.256']) {
        const channel = channelOf(caseId);
        for (const [name, mutate] of [
            ['index 0 stored as default', reads => { reads.control[0] = cellWith(reads.control[0], channel, defaultChannel); }],
            ['index 255 stored as index 254', reads => { reads.control[1] = cellWith(reads.control[1], channel, paletteChannel(254)); }],
            ['reset not neutral', reads => { reads.control[2] = cellWith(reads.control[2], channel, paletteChannel(0)); }],
        ]) {
            assert.equal(grade(caseId, mutate).outcome, 'inconclusive', `${caseId} ${name}`);
        }
    }
});

test('default: an explicit nondefault setup is required; a cell that merely stays default proves nothing', () => {
    for (const caseId of ['sgr.fg.default', 'sgr.bg.default']) {
        const channel = channelOf(caseId);
        const stayedDefault = grade(caseId, reads => { reads.fixture[1] = cellWith(reads.fixture[1], channel, defaultChannel); });
        assert.equal(stayedDefault.outcome, 'inconclusive', `${caseId} setup never applied`);
        assert.equal(stayedDefault.note, 'Distinct colored setup or reset color readback was absent');
        const literalSetup = grade(caseId, reads => { reads.fixture[1] = cellWith(reads.fixture[1], channel, literalChannel(205, 0, 0)); });
        assert.equal(literalSetup.outcome, 'inconclusive', `${caseId} literal setup is not the contracted palette setup`);
        const wrongIndex = grade(caseId, reads => { reads.fixture[1] = cellWith(reads.fixture[1], channel, paletteChannel(5)); });
        assert.equal(wrongIndex.outcome, 'inconclusive');
    }
});

test('default: a setup that resolves to the theme default is indistinguishable and stays inconclusive', () => {
    const fg = syntheticSgrPaletteGrade('sgr.fg.default', paletteBinding({ foreground: canonicalPalette()[1] }));
    assert.equal(fg.outcome, 'inconclusive');
    assert.deepEqual(fg.layers, { stored: 'supported', resolved: 'inconclusive' });
    const bg = syntheticSgrPaletteGrade('sgr.bg.default', paletteBinding({ background: canonicalPalette()[2] }));
    assert.equal(bg.outcome, 'inconclusive');
});

test('default: a reset that leaves palette, literal or index-0 storage is unsupported', () => {
    for (const caseId of ['sgr.fg.default', 'sgr.bg.default']) {
        const channel = channelOf(caseId), setup = channel === 'fg' ? 1 : 2;
        for (const [name, spec] of [['still the setup index', paletteChannel(setup)], ['palette index 0', paletteChannel(0)], ['literal bytes', literalChannel(1, 2, 3)]]) {
            const result = grade(caseId, reads => { reads.fixture[2] = cellWith(reads.fixture[2], channel, spec); });
            assert.equal(result.outcome, 'unsupported', `${caseId} ${name}`);
            assert.match(result.note, /default marker/);
        }
        const otherDrift = grade(caseId, reads => { reads.fixture[2] = cellWith(reads.fixture[2], channel === 'fg' ? 'bg' : 'fg', paletteChannel(3)); });
        assert.equal(otherDrift.outcome, 'unsupported');
    }
});

test('default: the unaffected channel must survive the reset, and an invalid isolation control cannot grade', () => {
    for (const caseId of ['sgr.fg.default', 'sgr.bg.default']) {
        const channel = channelOf(caseId), other = channel === 'fg' ? 'bg' : 'fg';
        const clearsBoth = grade(caseId, reads => { reads.control[1] = cellWith(reads.control[1], other, defaultChannel); });
        assert.equal(clearsBoth.outcome, 'unsupported', `${caseId} reset also cleared the other channel`);
        assert.match(clearsBoth.note, /did not reset only/);
        const keepsTarget = grade(caseId, reads => { reads.control[1] = cellWith(reads.control[1], channel, paletteChannel(channel === 'fg' ? 1 : 2)); });
        assert.equal(keepsTarget.outcome, 'unsupported');
        for (const [name, mutate] of [
            ['setup never applied to both channels', reads => { reads.control[0] = cellWith(reads.control[0], other, defaultChannel); }],
            ['reset cell not neutral', reads => { reads.control[2] = cellWith(reads.control[2], channel, paletteChannel(1)); }],
        ]) {
            assert.equal(grade(caseId, mutate).outcome, 'inconclusive', `${caseId} ${name}`);
        }
    }
});

test('shared controls: reverse video, an active baseline, unrelated attributes and missing cells never become unsupported', () => {
    for (const contract of sgrPaletteContracts) {
        const { caseId } = contract;
        const reverse = syntheticSgrPaletteGrade(caseId, { ...paletteBinding(), reverseVideo: true });
        assert.equal(reverse.outcome, 'inconclusive', caseId);
        assert.match(reverse.note, /DECSCNM/);
        assert.equal(grade(caseId, reads => { reads.fixture[0] = colorCell('C', { attrs: ['italic'] }); }).outcome, 'inconclusive');
        assert.equal(grade(caseId, reads => { reads.fixture[0] = colorCell('C', { fg: paletteChannel(7) }); }).outcome, 'inconclusive');
        assert.equal(grade(caseId, reads => { reads.fixture[0] = colorCell('C', { bg: paletteChannel(7) }); }).outcome, 'inconclusive');
        const missing = grade(caseId, reads => { reads.fixture[1] = plainCell('?'); });
        assert.equal(missing.outcome, 'inconclusive');
        assert.ok(missing.note.length > 0);
        const underlineDrift = grade(caseId, reads => { reads.fixture[1] = colorCell(reads.fixture[1].char, { ...reads.fixture[1], attrs: [...reads.fixture[1].attrs, 'strikethrough'] }); });
        assert.equal(underlineDrift.outcome, 'inconclusive', `${caseId} unrelated attribute drift`);
    }
});

test('the contract grader refuses unknown revisions and incomplete observations', () => {
    const contract = sgrPaletteContracts[0];
    const observation = sgrPaletteObservation(contract, syntheticSgrPaletteScreens(contract.caseId));
    assert.equal(gradeSgrPaletteCase(contract, observation).outcome, 'supported');
    assert.throws(() => gradeSgrPaletteCase(contract, observation, 2), /Unknown SGR-palette contracts revision/);
    assert.throws(() => gradeSgrPaletteCase(contract, { ...observation, fixture: observation.fixture.slice(1) }), /Missing sgr.fg.bright read captures/);
    assert.throws(() => gradeSgrPaletteCase(contract, { ...observation, control: [] }), /Missing sgr.fg.bright read captures/);
    assert.throws(() => sgrPaletteObservation(contract, [syntheticSgrPaletteScreens(contract.caseId)[0]]), /Missing SGR-palette read captures/);
});

test('DECSCNM seen in either read voids reverse-video proof', () => {
    const contract = sgrPaletteContracts[0];
    const [fixture, control] = syntheticSgrPaletteScreens(contract.caseId);
    const reversedControl = { ...control, colorBinding: { ...control.colorBinding, reverseVideo: true } };
    assert.equal(sgrPaletteObservation(contract, [fixture, reversedControl]).reverseVideo, true);
    assert.equal(sgrPaletteObservation(contract, [fixture, control]).reverseVideo, false);
});

test('the projection refuses attribute-run flags that contradict the stored details', () => {
    const screen = syntheticSgrPaletteScreen([colorCell('X', { fg: paletteChannel(9) })]);
    assert.equal(sgrPaletteCellAt(screen, 0, 0).fgIndex, 9);
    const contradictory = structuredClone(screen);
    contradictory.attributeRuns[0].flags |= 0x0001;
    assert.throws(() => sgrPaletteCellAt(contradictory, 0, 0), /Contradictory stored cell projections/);
});

test('the projection carries stored, literal and resolved state separately', () => {
    const screen = syntheticSgrPaletteScreen([colorCell('A', { fg: literalChannel(95, 135, 175) }), colorCell('X', { fg: paletteChannel(67) }), plainCell('C')]);
    const literal = sgrPaletteCellAt(screen, 0, 0), indexed = sgrPaletteCellAt(screen, 0, 1), plain = sgrPaletteCellAt(screen, 0, 2);
    assert.deepEqual([literal.fgSource, literal.fgIndex, literal.foregroundArgb & 0xffffff], ['literal', -1, 0x5f87af]);
    assert.deepEqual([indexed.fgSource, indexed.fgIndex, indexed.foregroundArgb], ['palette', 67, null]);
    assert.deepEqual(literal.fg, indexed.fg);
    assert.deepEqual([plain.fgSource, plain.fg, plain.fgIndex], ['default', null, -1]);
});
