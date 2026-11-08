import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrUnderlineColorContracts, sgrUnderlineColorCaseIds, sgrUnderlineColorContractsVersion, sgrUnderlineColorSuiteId,
    sgrUnderlineColorNoncePrefix, sgrUnderlineColorReset, gradeSgrUnderlineColorCase, sgrUnderlineColorObservation,
    sgrUnderlineColorFeeds, sgrUnderlineColorReads, validateSgrUnderlineColorFeeds, requestedUnderlineColor, equalLiteral,
    mutation } from './sgr-underline-color-contracts.mjs';
import { syntheticSgrUnderlineColorReads, syntheticSgrUnderlineColorScreens, syntheticSgrUnderlineColorGrade, mutatedBinding,
    ulCell, colorCell, plainCell, paletteBinding, paletteChannel, literalChannel } from './fixtures/sgr-underline-color-evidence.mjs';

const INDEXED = 'sgr.underline-color-indexed', RGB = 'sgr.underline-color-rgb', RESET = 'sgr.underline-color-reset';
const byId = id => sgrUnderlineColorContracts.find(contract => contract.caseId === id);
const grade = (caseId, edit, binding) => syntheticSgrUnderlineColorGrade(caseId, binding, id => {
    const reads = syntheticSgrUnderlineColorReads(id);
    if (id === caseId && edit) edit(reads);
    return reads;
});
const replaceCell = (cells, char, cell) => { cells[cells.findIndex(item => item.char === char)] = cell; };
/** Edit a cell in the fixture, and in the mutated/restored groups where it also appears. */
const everywhere = (reads, char, make) => {
    for (const group of ['fixture', 'control', 'mutated', 'restored']) {
        if (reads[group]?.some(item => item.char === char)) replaceCell(reads[group], char, make());
    }
};

test('the contracts pin exactly the three verified feature ids and their exact fixture bytes', () => {
    assert.deepEqual([...sgrUnderlineColorCaseIds], [INDEXED, RGB, RESET]);
    assert.deepEqual(sgrUnderlineColorContracts.map(contract => contract.fixture), [
        '\x1b[4;38;5;4m\x1b[59mA\x1b[58;5;5mX\x1b[38;5;5m\x1b[59mB\x1b[58;5;4mY',
        '\x1b[4m\x1b[59m\x1b[38;2;0;0;255mA\x1b[38;2;0;255;0mB\x1b[58;2;255;0;128mX',
        '\x1b[4m\x1b[59mC\x1b[58;2;255;0;128mX\x1b[59mY']);
    assert.equal(sgrUnderlineColorContractsVersion, 1);
    assert.equal(sgrUnderlineColorSuiteId, 'ntilde-owned-sgr-underline-color-graded-v1');
    assert.equal(sgrUnderlineColorNoncePrefix, 'NTILDE_UC_');
    assert.ok(sgrUnderlineColorReset.endsWith('\x1b]104\x07'));
    for (const contract of sgrUnderlineColorContracts) {
        assert.deepEqual([...contract.pinnedFeeds], [contract.fixture]);
        assert.match(contract.appearanceScope, /not claimed/);
        assert.equal(contract.pinnedRead, 0);
        assert.equal(sgrUnderlineColorReads(contract), contract.mutated ? 4 : 2);
        assert.doesNotThrow(() => validateSgrUnderlineColorFeeds(contract, sgrUnderlineColorFeeds(contract)));
        assert.throws(() => validateSgrUnderlineColorFeeds(contract, [...sgrUnderlineColorFeeds(contract).slice(0, -1), 'x']), /do not match/);
    }
    assert.equal(sgrUnderlineColorContracts.filter(contract => contract.mutated).length, 1, 'only the indexed case mutates the palette');
    assert.ok(!sgrUnderlineColorContracts.some(contract => contract.caseId === 'sgr.underline.color'), 'the aggregate case is not qualified');
});

test('every case is supported with all controls valid', () => {
    for (const id of sgrUnderlineColorCaseIds) {
        const result = syntheticSgrUnderlineColorGrade(id);
        assert.equal(result.outcome, 'supported', id);
        assert.equal(result.layers.stored, 'supported');
    }
    assert.deepEqual(syntheticSgrUnderlineColorGrade(RESET).layers, { stored: 'supported', resolved: 'not-evaluated' });
    assert.deepEqual(syntheticSgrUnderlineColorGrade(INDEXED).layers, { stored: 'supported', resolved: 'supported' });
});

test('indexed: wrong palette indices are unsupported and the layers say which one failed', () => {
    for (const [char, index, expected] of [['X', 6, /palette index 6 instead of palette index 5/], ['Y', 3, /palette index 3 instead of palette index 4/]]) {
        const result = grade(INDEXED, reads => everywhere(reads, char, () => ulCell(char, { fg: paletteChannel(char === 'X' ? 4 : 5), index })));
        assert.equal(result.outcome, 'unsupported');
        assert.match(result.note, expected);
        assert.equal(result.layers.stored, 'unsupported');
    }
});

test('indexed: an index/literal/default confusion is never graded from equal RGB', () => {
    // A literal with exactly palette 5's resolved RGB resolves the same, but is not the requested index: stored unsupported.
    const literal = grade(INDEXED, reads => everywhere(reads, 'X', () => ulCell('X', { fg: paletteChannel(4), rgb: { r: 0xcd, g: 0, b: 0xcd } })));
    assert.equal(literal.outcome, 'unsupported');
    assert.match(literal.note, /literal 0x[0-9a-f]+ instead of palette index 5/);
    // The foreground used as the underline: a default underline on X, where the pinned fixture requested an explicit index.
    const inherited = grade(INDEXED, reads => everywhere(reads, 'X', () => ulCell('X', { fg: paletteChannel(4) })));
    assert.equal(inherited.outcome, 'unsupported');
    assert.match(inherited.note, /the default color instead of palette index 5/);
    // An explicit color left on a cell the fixture reset with SGR 59.
    const left = grade(INDEXED, reads => everywhere(reads, 'B', () => ulCell('B', { fg: paletteChannel(5), index: 5 })));
    assert.equal(left.outcome, 'unsupported');
    assert.match(left.note, /SGR 59 left an explicit underline color/);
});

test('indexed: indistinguishable palette controls, DECSCNM and drift are inconclusive, never unsupported', () => {
    const same = grade(INDEXED, reads => {
        everywhere(reads, 'A', () => ulCell('A', { fg: paletteChannel(5) }));
        everywhere(reads, 'X', () => ulCell('X', { fg: paletteChannel(5), index: 5 }));
        everywhere(reads, 'Y', () => ulCell('Y', { fg: paletteChannel(5), index: 4 }));
    });
    assert.equal(same.outcome, 'inconclusive');
    const reversed = grade(INDEXED, null, { ...paletteBinding(), reverseVideo: true });
    assert.equal(reversed.outcome, 'inconclusive');
    assert.match(reversed.note, /DECSCNM/);
    for (const drift of [{ attrs: ['bold'] }, { attrs: ['overline'] }, { bg: paletteChannel(1) }, { fg: literalChannel(1, 2, 3) }]) {
        const result = grade(INDEXED, reads => everywhere(reads, 'X', () => ulCell('X', { fg: paletteChannel(4), index: 5, ...drift })));
        assert.equal(result.outcome, 'inconclusive', JSON.stringify(drift));
        assert.match(result.note, /drifted/);
    }
    const styled = grade(INDEXED, reads => everywhere(reads, 'X', () => ulCell('X', { fg: paletteChannel(4), index: 5, style: 3 })));
    assert.equal(styled.outcome, 'inconclusive', 'a changed underline style invalidates the setup');
});

test('indexed: the literal-versus-index, explicit-versus-default and neutral controls must hold', () => {
    const broken = [
        reads => { reads.control[0] = ulCell('P', { index: equalLiteral.r }); },                        // P not literal
        reads => { reads.control[0] = ulCell('P', { rgb: { r: 95, g: 135, b: 176 } }); },              // P differs from Q
        reads => { reads.control[1] = ulCell('Q', { rgb: equalLiteral }); },                           // Q not an index
        reads => { reads.control[2] = ulCell('R', { fg: paletteChannel(5) }); },                       // R default instead of explicit
        reads => { reads.control[3] = ulCell('N'); },                                                   // neutral cell underlined
    ];
    for (const edit of broken) {
        const result = grade(INDEXED, edit);
        assert.equal(result.outcome, 'inconclusive');
        assert.match(result.note, /control cells were not measured/);
    }
});

test('indexed: the OSC 4 mutation must change exactly the contracted entries and restore every one', () => {
    const bad = {
        'mutation not applied': reads => { reads.mutatedBinding = paletteBinding(); },
        'unrelated entry also changed': reads => {
            const binding = mutatedBinding(paletteBinding()); binding.ansiPaletteArgb[100] = 0xff010203; reads.mutatedBinding = binding;
        },
        'unrelated entry persists after restore': reads => {
            const binding = paletteBinding(); binding.ansiPaletteArgb[200] = 0xff010203; reads.restoredBinding = binding;
        },
        'mutated entry not restored': reads => { reads.restoredBinding = mutatedBinding(paletteBinding()); },
        'wrong mutated value': reads => {
            const binding = mutatedBinding(paletteBinding()); binding.ansiPaletteArgb[5] = 0xff654321; reads.mutatedBinding = binding;
        },
        'override list not reported': reads => { reads.mutatedBinding = { ...mutatedBinding(paletteBinding()), paletteOverrideIndices: [] }; },
        'override list not cleared on restore': reads => { reads.restoredBinding = { ...paletteBinding(), paletteOverrideIndices: [4] }; },
    };
    for (const [name, edit] of Object.entries(bad)) {
        const result = grade(INDEXED, edit);
        assert.equal(result.outcome, 'inconclusive', name);
        assert.match(result.note, /OSC 4 palette mutation and restoration/, name);
    }
    // Stored cells that change under mutation, or a literal that follows the palette, void the control.
    assert.equal(grade(INDEXED, reads => replaceCell(reads.mutated, 'X', ulCell('X', { fg: paletteChannel(4), index: 6 }))).outcome, 'inconclusive');
    assert.equal(grade(INDEXED, reads => replaceCell(reads.mutated, 'P', ulCell('P', { rgb: { r: 1, g: 2, b: 3 } }))).outcome, 'inconclusive');
    assert.deepEqual(Object.keys(mutation).map(Number), [4, 5, 67]);
});

test('rgb: wrong components, palette approximations and a substituted foreground are unsupported', () => {
    for (const rgb of [{ r: 255, g: 0, b: 129 }, { r: 254, g: 0, b: 128 }, { r: 255, g: 1, b: 128 }, { r: 0, g: 255, b: 0 }]) {
        const result = grade(RGB, reads => replaceCell(reads.fixture, 'X', ulCell('X', { fg: literalChannel(0, 255, 0), rgb })));
        assert.equal(result.outcome, 'unsupported', JSON.stringify(rgb));
        assert.match(result.note, /literal 0x[0-9a-f]+ instead of the exact literal bytes/);
    }
    // A palette approximation (cube entry 198 is 255,0,135) and the foreground's own color are not the requested literal.
    const palette = grade(RGB, reads => replaceCell(reads.fixture, 'X', ulCell('X', { fg: literalChannel(0, 255, 0), index: 198 })));
    assert.equal(palette.outcome, 'unsupported');
    assert.match(palette.note, /palette index 198/);
    const inherited = grade(RGB, reads => replaceCell(reads.fixture, 'X', ulCell('X', { fg: literalChannel(0, 255, 0) })));
    assert.equal(inherited.outcome, 'unsupported');
    assert.match(inherited.note, /the default color/);
    assert.equal(inherited.layers.stored, 'unsupported');
    // Literal bytes with an index or the reverse are refused as conflicting stored fields by the decoder.
    const screens = syntheticSgrUnderlineColorScreens(RGB);
    const conflict = structuredClone(screens);
    conflict[0].cellDetails.find(run => run.underlineArgb !== null).underlineColorIndex = 5;
    assert.throws(() => sgrUnderlineColorObservation(byId(RGB), conflict, paletteBinding()), /Invalid stored cell detail run/);
});

test('rgb: a default underline already matching the request, drift and the control cells keep the verdict inconclusive', () => {
    const matching = grade(RGB, reads => replaceCell(reads.fixture, 'B', ulCell('B', { fg: literalChannel(0, 255, 0), rgb: requestedUnderlineColor })));
    assert.deepEqual([matching.outcome, matching.note], ['inconclusive', 'Default underline already matches the requested color'],
        'criterion-defined ambiguity stays inconclusive');
    const drift = grade(RGB, reads => replaceCell(reads.fixture, 'X', ulCell('X', { fg: literalChannel(0, 255, 0), rgb: requestedUnderlineColor, attrs: ['italic'] })));
    assert.equal(drift.outcome, 'inconclusive');
    for (const edit of [
        reads => { reads.control[2] = ulCell('R', { fg: literalChannel(255, 0, 128), rgb: requestedUnderlineColor }); }, // foreground-substitution control must keep a default underline
        reads => { reads.control[2] = ulCell('R', { fg: literalChannel(255, 0, 129) }); },
        reads => { reads.control[1] = ulCell('Q', { index: 5 }); },
        reads => { reads.control[3] = ulCell('N'); },
    ]) {
        const result = grade(RGB, edit);
        assert.equal(result.outcome, 'inconclusive');
        assert.match(result.note, /control cells were not measured/);
    }
    assert.equal(grade(RGB, reads => replaceCell(reads.fixture, 'A', ulCell('A', { fg: literalChannel(0, 255, 0) }))).outcome, 'inconclusive',
        'indistinguishable foreground controls');
    // The colon forms are labeled controls only: whatever they store never changes the graded verdict.
    assert.equal(grade(RGB, reads => replaceCell(reads.control, 'C', ulCell('C', { index: 9 }))).outcome, 'supported');
});

test('reset: a nondefault setup is required, and SGR 59 must return Y to the default without clearing the style', () => {
    assert.equal(grade(RESET).outcome, 'supported');
    const noSetup = grade(RESET, reads => replaceCell(reads.fixture, 'X', ulCell('X')));
    assert.equal(noSetup.outcome, 'inconclusive');
    assert.match(noSetup.note, /prerequisite was not observed/);
    const wrongSetup = grade(RESET, reads => replaceCell(reads.fixture, 'X', ulCell('X', { index: 5 })));
    assert.equal(wrongSetup.outcome, 'inconclusive', 'an indexed color is not the requested literal setup');
    const baselineRequested = grade(RESET, reads => replaceCell(reads.fixture, 'C', ulCell('C', { rgb: requestedUnderlineColor })));
    assert.equal(baselineRequested.outcome, 'inconclusive');
    const left = grade(RESET, reads => replaceCell(reads.fixture, 'Y', ulCell('Y', { rgb: requestedUnderlineColor })));
    assert.deepEqual([left.outcome, left.note], ['unsupported', 'SGR 59 left the explicit rgb(255,0,128) underline color on Y']);
    const other = grade(RESET, reads => replaceCell(reads.fixture, 'Y', ulCell('Y', { index: 3 })));
    assert.equal(other.outcome, 'unsupported');
    assert.match(other.note, /palette index 3 instead of the default underline color/);
    for (const style of [0, 3]) {
        const cleared = grade(RESET, reads => replaceCell(reads.fixture, 'Y', style === 0 ? plainCell('Y') : ulCell('Y', { style })));
        assert.equal(cleared.outcome, 'inconclusive', `style ${style}`);
        assert.match(cleared.note, /cleared or changed the underline style/);
    }
});

test('reset: unrelated drift, DECSCNM and the style-preservation controls guard every verdict', () => {
    for (const drift of [{ attrs: ['bold'] }, { fg: paletteChannel(1) }, { bg: paletteChannel(2) }, { attrs: ['overline'] }]) {
        const result = grade(RESET, reads => replaceCell(reads.fixture, 'Y', ulCell('Y', drift)));
        assert.equal(result.outcome, 'inconclusive', JSON.stringify(drift));
    }
    assert.equal(grade(RESET, null, { ...paletteBinding(), reverseVideo: true }).outcome, 'inconclusive');
    const broken = [
        reads => { reads.control[1] = ulCell('T', { style: 1, fg: paletteChannel(2), bg: paletteChannel(4) }); },   // SGR 59 changed the style
        reads => { reads.control[1] = ulCell('T', { style: 3, index: 5, fg: paletteChannel(2), bg: paletteChannel(4) }); }, // indexed color not reset
        reads => { reads.control[1] = ulCell('T', { style: 3 }); },                                                    // fg/bg lost
        reads => { reads.control[0] = ulCell('S', { style: 3, fg: paletteChannel(2), bg: paletteChannel(4) }); },      // setup index missing
        reads => { reads.control[2] = ulCell('U'); },
        reads => { reads.control[3] = ulCell('V', { rgb: { r: 1, g: 2, b: 3 } }); },                                    // literal not reset
        reads => { reads.control[4] = ulCell('N'); },
    ];
    for (const edit of broken) {
        const result = grade(RESET, edit);
        assert.equal(result.outcome, 'inconclusive');
        assert.match(result.note, /controls were not measured/);
        // The controls guard unsupported verdicts too.
        const mismatch = grade(RESET, reads => { replaceCell(reads.fixture, 'Y', ulCell('Y', { rgb: requestedUnderlineColor })); edit(reads); });
        assert.equal(mismatch.outcome, 'inconclusive');
    }
});

test('a default underline is a marker that inherits the text color, never a fixed RGB', () => {
    for (const id of [INDEXED, RGB, RESET]) {
        const screens = syntheticSgrUnderlineColorScreens(id);
        const observation = sgrUnderlineColorObservation(byId(id), screens, paletteBinding());
        const defaults = [...observation.fixture, ...observation.control].filter(cell => cell.underlineSource === 'default');
        assert.ok(defaults.length >= 2, id);
        for (const cell of defaults) {
            assert.equal(cell.underlineColor, null);
            assert.equal(cell.underlineInheritsForeground, true);
            assert.equal(cell.underlineColorIndex, -1);
            assert.equal(cell.underlineArgb, null);
        }
    }
});

test('the grader refuses malformed observations and unknown revisions', () => {
    const contract = byId(INDEXED);
    const observation = sgrUnderlineColorObservation(contract, syntheticSgrUnderlineColorScreens(INDEXED), paletteBinding());
    assert.equal(gradeSgrUnderlineColorCase(contract, observation).outcome, 'supported');
    assert.throws(() => gradeSgrUnderlineColorCase(contract, observation, 2), /Unknown SGR-underline-color contracts revision/);
    assert.throws(() => gradeSgrUnderlineColorCase(contract, { ...observation, fixture: observation.fixture.slice(1) }), /Missing/);
    assert.throws(() => gradeSgrUnderlineColorCase(contract, { ...observation, mutated: undefined }), /Missing/);
    assert.throws(() => gradeSgrUnderlineColorCase(contract, null), /Missing/);
    assert.throws(() => sgrUnderlineColorObservation(contract, syntheticSgrUnderlineColorScreens(INDEXED).slice(1), paletteBinding()), /Missing/);
    assert.throws(() => sgrUnderlineColorObservation(contract, syntheticSgrUnderlineColorScreens(INDEXED), null), /Missing/);
    // Contradictory stored/flag projections are refused by the shared projection.
    const screens = syntheticSgrUnderlineColorScreens(RGB);
    const tampered = structuredClone(screens);
    tampered[0].attributeRuns[0].flags &= ~0x08;
    assert.throws(() => sgrUnderlineColorObservation(byId(RGB), tampered, paletteBinding()), /Contradictory|Invalid/i);
    const noStyle = structuredClone(screens);
    noStyle[0].cellDetails[0].underlineStyle = 0; noStyle[0].attributeRuns[0].flags &= ~0x08; noStyle[0].cellDetails[0].flags &= ~0x08;
    noStyle[0].cellDetails[0].underlineColorIndex = 4;
    assert.throws(() => sgrUnderlineColorObservation(byId(RGB), noStyle, paletteBinding()), /Invalid/);
    void colorCell;
});
