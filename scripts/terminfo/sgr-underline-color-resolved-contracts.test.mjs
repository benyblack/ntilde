import assert from 'node:assert/strict';
import { test } from 'node:test';
import { underlineResolvedContracts, underlineResolvedCaseIds, underlineResolvedMutation, underlineResolvedReset, underlineResolvedFeeds,
    underlineResolvedReads, validateUnderlineResolvedFeeds, gradeUnderlineResolvedCase, underlineResolvedObservation, expectedUnderline,
    validateUnderlineResolvedScreen } from './sgr-underline-color-resolved-contracts.mjs';
import { syntheticUnderlineResolvedGrade, syntheticUnderlineScreen, underlineBinding, underlineCells, phases } from './fixtures/underline-color-resolved-evidence.mjs';
import { canonicalPalette, paletteBinding, colorCell, paletteChannel, literalChannel } from './fixtures/sgr-palette-evidence.mjs';

const [indexed, literal, inherit] = underlineResolvedContracts.map(contract => contract.caseId);
const grade = (caseId, options) => syntheticUnderlineResolvedGrade(caseId, options);

test('the three Ntilde extension controls are fixed, ordered and carry no pinned identity', () => {
    assert.deepEqual([...underlineResolvedCaseIds], ['ntilde.underline-color-resolved.indexed', 'ntilde.underline-color-resolved.literal',
        'ntilde.underline-color-resolved.default-inheritance']);
    for (const contract of underlineResolvedContracts) {
        assert.equal(contract.kind, 'ntilde-extension-control');
        assert.equal(contract.check, 'ntilde-extension-contract');
        assert.equal(contract.pinnedPath, undefined);
        assert.equal(underlineResolvedReads(contract), 3);
        assert.equal(underlineResolvedFeeds(contract)[0], underlineResolvedReset);
        assert.doesNotThrow(() => validateUnderlineResolvedFeeds(contract, underlineResolvedFeeds(contract)));
        assert.throws(() => validateUnderlineResolvedFeeds(contract, [...underlineResolvedFeeds(contract).slice(0, -1), '\x1b]104\x07']), /do not match/);
    }
    assert.match(underlineResolvedReset, /\x1b\]104\x07$/);
});

test('honest App observations are supported with stored and App-resolved layers', () => {
    for (const caseId of underlineResolvedCaseIds) {
        const result = grade(caseId);
        assert.equal(result.outcome, 'supported', caseId);
        assert.deepEqual(result.layers, { stored: 'supported', appResolved: 'supported' });
    }
});

test('the expectation is derived from stored state and a palette, never from the App value', () => {
    const palette = canonicalPalette(), defaults = { fg: 0xffe5e5e5, bg: 0xff000000 };
    const cell = overrides => ({ underlineStyle: 1, underlineColorIndex: -1, underlineArgb: null, inverse: false,
        fgSource: 'default', fgIndex: -1, foregroundArgb: null, bgSource: 'default', bgIndex: -1, backgroundArgb: null, ...overrides });
    assert.deepEqual(expectedUnderline(cell({ underlineStyle: 0 }), palette, defaults), { source: 'none', index: -1, inheritsFrom: undefined, argb: undefined });
    assert.equal(expectedUnderline(cell({ underlineColorIndex: 5 }), palette, defaults).argb, palette[5]);
    assert.equal(expectedUnderline(cell({ underlineArgb: 0x00ff0080 }), palette, defaults).argb, 0xffff0080);
    assert.equal(expectedUnderline(cell(), palette, defaults).argb, defaults.fg);
    assert.equal(expectedUnderline(cell({ inverse: true }), palette, defaults).argb, defaults.bg);
    assert.equal(expectedUnderline(cell({ inverse: true }), palette, defaults).inheritsFrom, 'background');
    assert.equal(expectedUnderline(cell({ fgSource: 'palette', fgIndex: 5 }), palette, defaults).argb, palette[5]);
    assert.equal(expectedUnderline(cell({ fgSource: 'literal', foregroundArgb: 0x000a141e }), palette, defaults).argb, 0xff0a141e);
    assert.equal(expectedUnderline(cell({ inverse: true, bgSource: 'palette', bgIndex: 5, fgSource: 'literal', foregroundArgb: 1 }), palette, defaults).argb, palette[5]);
});

test('a grader-level disagreement between App observation and expectation is unsupported, with both values retained', () => {
    const contract = underlineResolvedContracts.find(item => item.caseId === indexed);
    const screens = phases.map(phase => syntheticUnderlineScreen(underlineCells(contract), underlineBinding(phase)));
    const observation = underlineResolvedObservation(contract, screens, screens[0].colorBinding);
    observation.groups[1][1] = { ...observation.groups[1][1], appObserved: { ...observation.groups[1][1].appObserved, argb: 0xff123456 } };
    const result = gradeUnderlineResolvedCase(contract, observation);
    assert.equal(result.outcome, 'unsupported');
    assert.deepEqual(result.layers, { stored: 'supported', appResolved: 'unsupported' });
    assert.equal(result.mismatches.length, 1);
    assert.equal(result.mismatches[0].read, 'mutated');
    assert.equal(result.mismatches[0].cell, 'X');
    assert.equal(result.mismatches[0].parentExpected.argb, underlineResolvedMutation[5]);
    assert.equal(result.mismatches[0].appObserved.argb, 0xff123456);
});

test('default, indexed and literal confusion in the App report is refused by screen coherence', () => {
    const contract = underlineResolvedContracts.find(item => item.caseId === indexed);
    const asLiteral = (spec, position, honest) => position === 1 ? { source: 'literal', index: -1, argb: honest.argb } : honest;
    const asDefault = (spec, position, honest) => position === 1 ? { source: 'inherit', index: -1, inheritsFrom: 'foreground', argb: 0xffe5e5e5 } : honest;
    const asNone = (spec, position, honest) => position === 1 ? { source: 'none', index: -1 } : honest;
    const wrongIndex = (spec, position, honest) => position === 1 ? { ...honest, index: 4, argb: paletteBinding().ansiPaletteArgb[4] } : honest;
    for (const [name, ulOf] of [['literal', asLiteral], ['inherit', asDefault], ['none', asNone], ['wrong palette index', wrongIndex]]) {
        assert.throws(() => validateUnderlineResolvedScreen(syntheticUnderlineScreen(underlineCells(contract), paletteBinding(), (spec, position, honest) => ulOf(spec, position, honest))),
            /Resolved .*(underline|Underline)/, name);
    }
});

test('a pre-mutation value reported against a mutated binding is refused: the App value must be that binding\'s entry', () => {
    const contract = underlineResolvedContracts.find(item => item.caseId === indexed);
    const stale = (spec, position, honest) => position === 1 ? { ...honest, argb: paletteBinding().ansiPaletteArgb[5] } : honest;
    assert.throws(() => validateUnderlineResolvedScreen(syntheticUnderlineScreen(underlineCells(contract), underlineBinding('mutated'), stale)), /Invalid resolved-color run/);
});

test('inherit must carry the inverse channel and the cell\'s own text color', () => {
    const contract = underlineResolvedContracts.find(item => item.caseId === inherit);
    const position = contract.setup.findIndex(want => want.char === 'D');
    const wrongChannel = (spec, index, honest) => index === position ? { ...honest, inheritsFrom: 'foreground' } : honest;
    const wrongColor = (spec, index, honest) => index === position ? { ...honest, argb: 0xff010203 } : honest;
    for (const ulOf of [wrongChannel, wrongColor]) {
        assert.throws(() => validateUnderlineResolvedScreen(syntheticUnderlineScreen(underlineCells(contract), paletteBinding(), ulOf)), /inherited underline disagrees/);
    }
});

test('mutation and palette controls: stale, partial, unrelated or pre-existing drift is inconclusive', () => {
    const mutatedOk = underlineBinding('mutated');
    const cases = [
        ['mutation not applied', (contract, phase) => phase === 'mutated' ? underlineBinding('before') : underlineBinding(phase)],
        ['not restored', (contract, phase) => phase === 'restored' ? mutatedOk : underlineBinding(phase)],
        ['partial mutation', (contract, phase) => phase === 'mutated' ? { ...mutatedOk, ansiPaletteArgb: mutatedOk.ansiPaletteArgb.map((argb, index) => index === 4 ? canonicalPalette()[4] : argb), paletteOverrideIndices: [5] } : underlineBinding(phase)],
        ['unrelated entry drift in the mutated read', (contract, phase) => phase === 'mutated' ? { ...mutatedOk, ansiPaletteArgb: mutatedOk.ansiPaletteArgb.map((argb, index) => index === 67 ? 0xff010101 : argb) } : underlineBinding(phase)],
        ['unrelated drift that stays in every read', (contract, phase) => ({ ...underlineBinding(phase), ansiPaletteArgb: underlineBinding(phase).ansiPaletteArgb.map((argb, index) => index === 200 ? 0xff020202 : argb) })],
        ['wrong override list', (contract, phase) => phase === 'mutated' ? { ...mutatedOk, paletteOverrideIndices: [4] } : underlineBinding(phase)],
        ['theme default changed', (contract, phase) => phase === 'restored' ? { ...underlineBinding(phase), foregroundArgb: 0xff010203 } : underlineBinding(phase)],
        ['reverse video on', (contract, phase) => ({ ...underlineBinding(phase), reverseVideo: true })],
    ];
    for (const [name, bindingOf] of cases) {
        for (const caseId of underlineResolvedCaseIds) {
            // `bindingOf` also supplies the baseline unless an explicit start binding is given, so pin the honest start.
            const result = grade(caseId, { bindingOf, baselineOf: () => underlineBinding('before') });
            assert.equal(result.outcome, 'inconclusive', `${name}: ${caseId}`);
        }
    }
});

test('an unprepared stored setup is inconclusive, never unsupported', () => {
    const drift = (change) => (contract, phase) => underlineCells(contract).map((cell, position) => position === 1 ? { ...cell, ...change } : cell);
    for (const [name, change] of [['cleared underline', { underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null }],
        ['different index', { underlineColorIndex: 4 }], ['bold drift', { attrs: ['bold'] }], ['foreground drift', { fg: paletteChannel(1) }],
        ['literal instead of index', { underlineColorIndex: -1, underlineArgb: 0xffff0080 }]]) {
        const result = grade(indexed, { cellsOf: drift(change) });
        assert.equal(result.outcome, 'inconclusive', name);
    }
});

test('stored cells that change across the mutation reads are inconclusive', () => {
    const cellsOf = (contract, phase) => underlineCells(contract).map((cell, position) => phase === 'mutated' && position === 1 ? { ...cell, underlineColorIndex: 4 } : cell);
    assert.equal(grade(indexed, { cellsOf }).outcome, 'inconclusive');
});

test('the literal aliasing control needs the default entry 5 color it is built on', () => {
    const palette = canonicalPalette(); palette[5] = 0xff010101;
    const bindingOf = (contract, phase) => underlineBinding(phase, { palette });
    // With a changed baseline palette the mutated binding is built from the canonical palette: the control refuses either way.
    assert.equal(grade(literal, { bindingOf, baselineOf: () => paletteBinding({ palette }) }).outcome, 'inconclusive');
});

test('the contracts never accept an unknown revision or a short capture', () => {
    const contract = underlineResolvedContracts[0];
    assert.throws(() => gradeUnderlineResolvedCase(contract, { groups: [], bindings: [], baseline: {} }), /Missing/);
    assert.throws(() => gradeUnderlineResolvedCase(contract, { groups: [[], [], []], bindings: [{}, {}, {}], baseline: {} }, 2), /Unknown/);
    assert.throws(() => underlineResolvedObservation(contract, [], {}), /Missing/);
});

test('literal and default-inheritance cases keep literal bytes and inherited channels distinct from palette follow', () => {
    // Honest evidence: the literal equal to default entry 5 does not follow the mutation, the inherited fg palette 5 does.
    const evidence = (caseId) => phases.map(phase => syntheticUnderlineScreen(underlineCells(underlineResolvedContracts.find(c => c.caseId === caseId)), underlineBinding(phase)));
    const literalScreens = evidence(literal);
    const column = underlineResolvedContracts.find(c => c.caseId === literal).setup.findIndex(want => want.char === 'P');
    const argbAt = (screen, col) => screen.resolvedColors.flatMap(run => Array.from({ length: run.count }, () => run))[col].ul.argb;
    assert.equal(argbAt(literalScreens[0], column), argbAt(literalScreens[1], column));
    const inheritScreens = evidence(inherit);
    const inheritColumn = underlineResolvedContracts.find(c => c.caseId === inherit).setup.findIndex(want => want.char === 'I');
    assert.notEqual(argbAt(inheritScreens[0], inheritColumn), argbAt(inheritScreens[1], inheritColumn));
    assert.equal(argbAt(inheritScreens[1], inheritColumn), underlineResolvedMutation[5]);
});
