import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrTruecolorContracts, sgrTruecolorCaseIds, sgrTruecolorContractsVersion, sgrTruecolorFeeds, sgrTruecolorReads,
    sgrTruecolorReset, sgrTruecolorClear, validateSgrTruecolorFeeds, gradeSgrTruecolorCase, sgrTruecolorObservation,
    sgrTruecolorCellAt } from './sgr-truecolor-contracts.mjs';
import { canonicalPalette, paletteBinding, syntheticSgrTruecolorReads, syntheticSgrTruecolorScreens, syntheticSgrTruecolorScreen,
    syntheticSgrTruecolorGrade, colorCell, plainCell, paletteChannel, literalChannel, defaultChannel } from './fixtures/sgr-truecolor-evidence.mjs';

/** Grade one case after mutating only that case's synthetic reads. */
const grade = (caseId, mutate, binding) => syntheticSgrTruecolorGrade(caseId, binding, id => {
    const reads = syntheticSgrTruecolorReads(id);
    if (id === caseId) mutate?.(reads);
    return reads;
});
const withPalette = edit => { const palette = canonicalPalette(); edit(palette); return paletteBinding({ palette }); };
const channelOf = caseId => (caseId.includes('.fg.') ? 'fg' : 'bg');
const cellWith = (cell, channel, spec, attrs) => colorCell(cell.char, { ...cell, [channel]: spec, attrs: attrs ?? cell.attrs });
const ids = ['sgr.fg.truecolor', 'sgr.bg.truecolor'];
const firstOf = caseId => (channelOf(caseId) === 'fg' ? [255, 128, 0] : [0, 255, 128]);

test('the contracts cover exactly the two pinned ids; the related extensions.truecolor variant is not substituted', () => {
    assert.equal(sgrTruecolorContractsVersion, 1);
    assert.deepEqual([...sgrTruecolorCaseIds], ids);
    assert.ok(!sgrTruecolorCaseIds.includes('extensions.truecolor'));
    for (const contract of sgrTruecolorContracts) {
        assert.equal(contract.probeId, contract.caseId);
        assert.equal(sgrTruecolorReads(contract), 2);
        assert.deepEqual(sgrTruecolorFeeds(contract), [sgrTruecolorReset, contract.fixture, sgrTruecolorClear, contract.control]);
        assert.deepEqual([...contract.pinnedFeeds], [contract.fixture]);
        assert.match(contract.appearanceScope, /rendered appearance is not claimed/);
        assert.match(contract.appearanceScope, /semicolon 38\/48;2;r;g;b form only/);
    }
});

test('every fixture is the exact pinned terminfo.dev@4.0.0 byte sequence, in the semicolon form only', () => {
    assert.deepEqual(Object.fromEntries(sgrTruecolorContracts.map(contract => [contract.caseId, contract.fixture])), {
        'sgr.fg.truecolor': 'C\x1b[31mA\x1b[34mB\x1b[38;2;255;128;0mX\x1b[38;2;17;97;201mY',
        'sgr.bg.truecolor': 'C\x1b[41mA\x1b[44mB\x1b[48;2;0;255;128mX\x1b[48;2;17;97;201mY',
    });
    // No colon, colorspace-id or combined single-sequence variant is ever fed.
    for (const contract of sgrTruecolorContracts) {
        for (const feed of [contract.fixture, contract.control]) {
            assert.ok(!feed.includes(':'), 'no colon form');
            assert.ok(!/\x1b\[[34]8;2;;/.test(feed), 'no optional colorspace id');
            assert.ok(!/[34]8;2;\d+;\d+;\d+;[0-9;]*[34]8/.test(feed), 'no combined fg+bg single sequence');
        }
    }
});

test('the control group is the contracted literal/palette/boundary/isolation sequence', () => {
    assert.equal(sgrTruecolorContracts[0].control,
        '\x1b[38;2;95;135;175mP\x1b[38;5;67mQ\x1b[38;2;0;0;0mL\x1b[38;2;255;255;255mH\x1b[0m\x1b[42m\x1b[38;2;17;97;201mU\x1b[0mR');
    assert.equal(sgrTruecolorContracts[1].control,
        '\x1b[48;2;95;135;175mP\x1b[48;5;67mQ\x1b[48;2;0;0;0mL\x1b[48;2;255;255;255mH\x1b[0m\x1b[31m\x1b[48;2;17;97;201mU\x1b[0mR');
});

test('both cases are supported on faithful synthetic evidence with both layers recorded', () => {
    for (const caseId of ids) {
        assert.deepEqual(syntheticSgrTruecolorGrade(caseId), { outcome: 'supported', layers: { stored: 'supported', resolved: 'supported' } }, caseId);
    }
});

test('recorded feeds must be byte-identical to the contract', () => {
    const contract = sgrTruecolorContracts[0];
    validateSgrTruecolorFeeds(contract, sgrTruecolorFeeds(contract));
    assert.throws(() => validateSgrTruecolorFeeds(contract, [sgrTruecolorReset, 'C\x1b[31mA\x1b[34mB\x1b[38;2;255;128;1mX\x1b[38;2;17;97;201mY', sgrTruecolorClear, contract.control]), /do not match/);
    assert.throws(() => validateSgrTruecolorFeeds(contract, []), /do not match/);
});

test('swapped or altered RGB components are unsupported at the stored layer', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId), [r, g, b] = firstOf(caseId);
        for (const [position, bytes, name] of [
            [3, [b, g, r], 'swapped r and b of the first sample'], [3, [g, r, b], 'swapped r and g'], [3, [r, b, g], 'swapped g and b'],
            [3, [r, g, b === 255 ? 254 : b + 1], 'first sample off by one in blue'], [3, [r === 255 ? 254 : r + 1, g, b], 'first sample off by one in red'],
            [3, [17, 97, 201], 'first sample replaced by the second'], [4, [201, 97, 17], 'second sample reversed'],
            [4, [17, 97, 200], 'second sample off by one'], [4, [r, g, b], 'second sample replaced by the first'],
        ]) {
            const result = grade(caseId, reads => { reads.fixture[position] = cellWith(reads.fixture[position], channel, literalChannel(...bytes)); });
            assert.equal(result.outcome, 'unsupported', `${caseId} ${name}`);
            assert.deepEqual(result.layers, { stored: 'unsupported', resolved: 'not-evaluated' }, `${caseId} ${name}`);
            assert.match(result.note, /did not store the requested literal RGB bytes/);
        }
    }
});

test('literal, palette-index and default-marker confusion on the targets is unsupported', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId);
        for (const [position, spec, name] of [
            [3, paletteChannel(208), 'first stored as a palette index'], [4, paletteChannel(67), 'second stored as a palette index'],
            [3, defaultChannel, 'first on the default marker'], [4, defaultChannel, 'second on the default marker'],
            [3, paletteChannel(0), 'first as palette index 0'],
        ]) {
            const result = grade(caseId, reads => { reads.fixture[position] = cellWith(reads.fixture[position], channel, spec); });
            assert.equal(result.outcome, 'unsupported', `${caseId} ${name}`);
            assert.equal(result.layers.stored, 'unsupported');
        }
    }
});

test('equal resolved RGB cannot conceal a palette-index storage: the contract is stricter than the pinned probe', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId);
        // Index 200 is overridden to the second sample's RGB, so the pinned resolved-only check is satisfied.
        const binding = withPalette(palette => { palette[200] = 0xff1161c9; });
        const result = grade(caseId, reads => { reads.fixture[4] = cellWith(reads.fixture[4], channel, paletteChannel(200)); }, binding);
        assert.equal(result.outcome, 'unsupported', caseId);
        assert.deepEqual(result.layers, { stored: 'unsupported', resolved: 'not-evaluated' });
    }
});

test('the independent ordinary-color controls must be the contracted standard palette cells', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId);
        for (const [position, spec, name] of [
            [1, literalChannel(205, 0, 0), 'A stored as literal'], [1, paletteChannel(2), 'A wrong index'], [2, paletteChannel(3), 'B wrong index'],
            [2, defaultChannel, 'B default'],
        ]) {
            // A wrong target AND an invalid control must never grade unsupported.
            const result = grade(caseId, reads => {
                reads.fixture[position] = cellWith(reads.fixture[position], channel, spec);
                reads.fixture[3] = cellWith(reads.fixture[3], channel, literalChannel(1, 2, 3));
            });
            assert.equal(result.outcome, 'inconclusive', `${caseId} ${name}`);
            assert.match(result.note, /ordinary-color controls/);
        }
    }
});

test('a preceding or uncalibrated ordinary color keeps the pinned inconclusive, never unsupported', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId), [r, g, b] = firstOf(caseId);
        // B resolves to the first requested RGB: it could mimic an ignored first request.
        const mimic = withPalette(palette => { palette[4] = 0xff000000 + r * 65536 + g * 256 + b; });
        for (const wrongTarget of [false, true]) {
            const result = grade(caseId, reads => {
                if (wrongTarget) reads.fixture[3] = cellWith(reads.fixture[3], channel, literalChannel(1, 2, 3));
            }, mimic);
            assert.equal(result.outcome, 'inconclusive', `${caseId} mimic wrongTarget=${wrongTarget}`);
            assert.equal(result.note, 'The preceding color can mimic an ignored first RGB request');
        }
        const aliased = withPalette(palette => { palette[1] = palette[4]; });
        const result = grade(caseId, reads => { reads.fixture[3] = cellWith(reads.fixture[3], channel, literalChannel(1, 2, 3)); }, aliased);
        assert.equal(result.outcome, 'inconclusive', `${caseId} aliased ordinary controls`);
        assert.equal(result.note, 'Independent color-channel calibration was absent');
    }
});

test('literal-versus-palette controls: storage must differ while the resolved appearance is equal', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId);
        for (const [name, mutate] of [
            ['literal sample stored as palette 67', reads => { reads.control[0] = cellWith(reads.control[0], channel, paletteChannel(67)); }],
            ['palette 67 stored as literal bytes', reads => { reads.control[1] = cellWith(reads.control[1], channel, literalChannel(95, 135, 175)); }],
            ['literal sample altered', reads => { reads.control[0] = cellWith(reads.control[0], channel, literalChannel(95, 135, 176)); }],
            ['palette 67 stored as 68', reads => { reads.control[1] = cellWith(reads.control[1], channel, paletteChannel(68)); }],
            ['literal sample on the default marker', reads => { reads.control[0] = cellWith(reads.control[0], channel, defaultChannel); }],
        ]) {
            const result = grade(caseId, mutate);
            assert.equal(result.outcome, 'inconclusive', `${caseId} ${name}`);
            assert.match(result.note, /Literal-versus-palette/);
        }
        // A theme whose index 67 is not the cube value breaks the equal-appearance premise: inconclusive, not a verdict.
        const shifted = syntheticSgrTruecolorGrade(caseId, withPalette(palette => { palette[67] = 0xff010203; }));
        assert.equal(shifted.outcome, 'inconclusive', `${caseId} palette changed`);
        assert.match(shifted.note, /do not resolve alike/);
    }
});

test('boundary component controls: black and white literals are literal storage, not default or palette', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId);
        for (const [name, mutate] of [
            ['literal black stored as default', reads => { reads.control[2] = cellWith(reads.control[2], channel, defaultChannel); }],
            ['literal black stored as palette 0', reads => { reads.control[2] = cellWith(reads.control[2], channel, paletteChannel(0)); }],
            ['literal white stored as palette 15', reads => { reads.control[3] = cellWith(reads.control[3], channel, paletteChannel(15)); }],
            ['literal white altered', reads => { reads.control[3] = cellWith(reads.control[3], channel, literalChannel(255, 255, 254)); }],
            ['literal black altered', reads => { reads.control[2] = cellWith(reads.control[2], channel, literalChannel(0, 1, 0)); }],
        ]) {
            const result = grade(caseId, mutate);
            assert.equal(result.outcome, 'inconclusive', `${caseId} ${name}`);
            assert.match(result.note, /boundary-component/);
        }
    }
});

test('the unaffected channel must survive a truecolor request, and the reset cell must be neutral', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId), other = channel === 'fg' ? 'bg' : 'fg';
        for (const [name, mutate] of [
            ['isolation cell lost the other channel', reads => { reads.control[4] = cellWith(reads.control[4], other, defaultChannel); }],
            ['isolation cell other channel changed', reads => { reads.control[4] = cellWith(reads.control[4], other, paletteChannel(5)); }],
            ['isolation cell lost the literal', reads => { reads.control[4] = cellWith(reads.control[4], channel, defaultChannel); }],
            ['reset cell keeps a literal', reads => { reads.control[5] = cellWith(reads.control[5], channel, literalChannel(1, 2, 3)); }],
            ['reset cell keeps the other channel', reads => { reads.control[5] = cellWith(reads.control[5], other, paletteChannel(1)); }],
            ['target cell gained the other channel', reads => { reads.fixture[3] = cellWith(reads.fixture[3], other, paletteChannel(2)); }],
            ['target cell gained italic', reads => { reads.fixture[4] = cellWith(reads.fixture[4], channel, literalChannel(17, 97, 201), ['italic']); }],
            ['target cell gained an overline', reads => { reads.fixture[3] = cellWith(reads.fixture[3], channel, literalChannel(...firstOf(caseId)), ['overline']); }],
        ]) {
            assert.equal(grade(caseId, mutate).outcome, 'inconclusive', `${caseId} ${name}`);
        }
    }
});

test('shared controls: reverse video, an active baseline and missing cells never become unsupported', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId);
        const wrong = reads => { reads.fixture[3] = cellWith(reads.fixture[3], channel, literalChannel(1, 2, 3)); };
        const reverse = syntheticSgrTruecolorGrade(caseId, { ...paletteBinding(), reverseVideo: true });
        assert.equal(reverse.outcome, 'inconclusive', caseId);
        assert.match(reverse.note, /DECSCNM/);
        assert.deepEqual(reverse.layers, { stored: 'not-evaluated', resolved: 'not-evaluated' });
        for (const baseline of [colorCell('C', { attrs: ['italic'] }), colorCell('C', { fg: paletteChannel(7) }), colorCell('C', { bg: paletteChannel(7) })]) {
            assert.equal(grade(caseId, reads => { wrong(reads); reads.fixture[0] = baseline; }).outcome, 'inconclusive');
        }
        const missing = grade(caseId, reads => { wrong(reads); reads.fixture[1] = plainCell('?'); });
        assert.equal(missing.outcome, 'inconclusive');
        assert.equal(missing.note, 'RGB target or independent color-control cells were not exposed');
        assert.equal(grade(caseId, reads => { wrong(reads); reads.fixture[3] = plainCell('Z'); }).outcome, 'inconclusive');
    }
});

test('the contract never grades a case supported where the cells are not exactly the contracted bytes', () => {
    for (const caseId of ids) {
        const channel = channelOf(caseId);
        // A palette entry with the same resolved bytes is "supported" by the pinned resolved-only check but never by the contract.
        const binding = withPalette(palette => { const [r, g, b] = firstOf(caseId); palette[201] = 0xff000000 + r * 65536 + g * 256 + b; palette[202] = 0xff1161c9; });
        const result = grade(caseId, reads => {
            reads.fixture[3] = cellWith(reads.fixture[3], channel, paletteChannel(201));
            reads.fixture[4] = cellWith(reads.fixture[4], channel, paletteChannel(202));
        }, binding);
        assert.notEqual(result.outcome, 'supported', caseId);
        assert.equal(result.outcome, 'unsupported');
    }
});

test('the contract grader refuses unknown revisions and incomplete observations', () => {
    const contract = sgrTruecolorContracts[0];
    const observation = sgrTruecolorObservation(contract, syntheticSgrTruecolorScreens(contract.caseId));
    assert.equal(gradeSgrTruecolorCase(contract, observation).outcome, 'supported');
    assert.throws(() => gradeSgrTruecolorCase(contract, observation, 2), /Unknown SGR-truecolor contracts revision/);
    assert.throws(() => gradeSgrTruecolorCase(contract, { ...observation, fixture: observation.fixture.slice(1) }), /Missing sgr.fg.truecolor read captures/);
    assert.throws(() => gradeSgrTruecolorCase(contract, { ...observation, control: [] }), /Missing sgr.fg.truecolor read captures/);
    assert.throws(() => sgrTruecolorObservation(contract, [syntheticSgrTruecolorScreens(contract.caseId)[0]]), /Missing SGR-truecolor read captures/);
});

test('DECSCNM seen in either read voids reverse-video proof', () => {
    const contract = sgrTruecolorContracts[0];
    const [fixture, control] = syntheticSgrTruecolorScreens(contract.caseId);
    const reversedControl = { ...control, colorBinding: { ...control.colorBinding, reverseVideo: true } };
    assert.equal(sgrTruecolorObservation(contract, [fixture, reversedControl]).reverseVideo, true);
    assert.equal(sgrTruecolorObservation(contract, [fixture, control]).reverseVideo, false);
});

test('the projection refuses attribute-run flags that contradict the stored details', () => {
    const screen = syntheticSgrTruecolorScreen([colorCell('X', { fg: paletteChannel(9) })]);
    assert.equal(sgrTruecolorCellAt(screen, 0, 0).fgIndex, 9);
    const contradictory = structuredClone(screen);
    contradictory.attributeRuns[0].flags |= 0x0001;
    assert.throws(() => sgrTruecolorCellAt(contradictory, 0, 0), /Contradictory stored cell projections/);
});

test('the projection carries stored, literal and resolved state separately', () => {
    const screen = syntheticSgrTruecolorScreen([colorCell('A', { fg: literalChannel(95, 135, 175) }), colorCell('X', { fg: paletteChannel(67) }), plainCell('C')]);
    const literal = sgrTruecolorCellAt(screen, 0, 0), indexed = sgrTruecolorCellAt(screen, 0, 1), plain = sgrTruecolorCellAt(screen, 0, 2);
    assert.deepEqual([literal.fgSource, literal.fgIndex, literal.foregroundArgb & 0xffffff], ['literal', -1, 0x5f87af]);
    assert.deepEqual([indexed.fgSource, indexed.fgIndex, indexed.foregroundArgb], ['palette', 67, null]);
    assert.deepEqual(literal.fg, indexed.fg);
    assert.deepEqual([plain.fgSource, plain.fg, plain.fgIndex], ['default', null, -1]);
});

test('every verdict carries both layer states, including early inconclusive paths', () => {
    for (const contract of sgrTruecolorContracts) {
        const reverse = syntheticSgrTruecolorGrade(contract.caseId, { ...paletteBinding(), reverseVideo: true });
        assert.deepEqual(reverse.layers, { stored: 'not-evaluated', resolved: 'not-evaluated' }, contract.caseId);
        const active = grade(contract.caseId, reads => { reads.fixture[0] = colorCell('C', { attrs: ['italic'] }); });
        assert.deepEqual(active.layers, { stored: 'not-evaluated', resolved: 'not-evaluated' }, contract.caseId);
    }
});

test('the projection refuses stored detail indices that disagree with the attribute runs and resolved channels', () => {
    const screen = syntheticSgrTruecolorScreen([colorCell('X', { fg: paletteChannel(67) })]);
    assert.equal(sgrTruecolorCellAt(screen, 0, 0).fgIndex, 67);
    const detailsOnly = structuredClone(screen);
    detailsOnly.cellDetails[0].fg = 68;
    assert.throws(() => sgrTruecolorCellAt(detailsOnly, 0, 0), /Contradictory stored and resolved fg palette indices/);
    const runsOnly = structuredClone(screen);
    runsOnly.attributeRuns[0].fg = 68;
    assert.throws(() => sgrTruecolorCellAt(runsOnly, 0, 0), /Contradictory/);
});
