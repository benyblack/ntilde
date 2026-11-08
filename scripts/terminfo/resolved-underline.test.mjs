import assert from 'node:assert/strict';
import { test } from 'node:test';
import { copyColorBinding, copyResolvedColors, expandResolvedColors, colorBindingHash, checkUnderlineCoherence } from './resolved-colors.mjs';
import { screenSnapshot } from './agent-observer.mjs';
import { syntheticUnderlineScreen, underlineBinding } from './fixtures/underline-color-resolved-evidence.mjs';
import { syntheticSgrPaletteScreen, paletteBinding, colorCell, paletteChannel } from './fixtures/sgr-palette-evidence.mjs';

const cells = [colorCell('A', { underlineStyle: 1 }), colorCell('X', { underlineStyle: 1, underlineColorIndex: 5 }), colorCell('Z')];
const screen = () => syntheticUnderlineScreen(cells, underlineBinding('before'));

test('an opted-in screen validates, expands and keeps the underline beside both channels', () => {
    const value = screen();
    const snapshot = screenSnapshot(value, true, true, false, true, false, true, true);
    const expanded = expandResolvedColors(snapshot.resolvedColors, snapshot.rows * snapshot.cols, snapshot.colorBinding);
    assert.deepEqual(expanded.slice(0, 3).map(cell => cell.ul.source), ['inherit', 'palette', 'none']);
    assert.doesNotThrow(() => checkUnderlineCoherence(snapshot));
    assert.equal(snapshot.colorBinding.underlineStage, 'underline-resolution');
});

test('the opt-in is both ways strict: requested means present, not requested means absent', () => {
    assert.throws(() => screenSnapshot(screen(), true, true, false, true, false, true, false), /does not match the request/);
    const plain = syntheticSgrPaletteScreen(cells, paletteBinding());
    assert.doesNotThrow(() => screenSnapshot(plain, true, true, false, true, false, true, false));
    assert.throws(() => screenSnapshot(plain, true, true, false, true, false, true, true), /does not match the request/);
});

test('an unrequested projection cannot smuggle App underline values into frozen-shape evidence', () => {
    const plain = syntheticSgrPaletteScreen(cells, paletteBinding());
    const smuggled = { ...plain, resolvedColors: plain.resolvedColors.map(run => ({ ...run, ul: { source: 'none', index: -1 } })) };
    assert.throws(() => screenSnapshot(smuggled, true, true, false, true, false, true, false), /Invalid resolved-color run/);
});

test('binding hashes of frozen-shape bindings are unchanged by the additive fields', () => {
    const base = paletteBinding();
    const staged = { ...base, underlineStage: 'underline-resolution', underlineStageNote: 'x'.repeat(80) };
    assert.equal(colorBindingHash(base), colorBindingHash({ ...base }));
    assert.notEqual(colorBindingHash(base), colorBindingHash(staged));
    assert.equal(Object.hasOwn(copyColorBinding(base), 'underlineStage'), false);
    assert.throws(() => copyColorBinding({ ...base, underlineStage: 'other', underlineStageNote: 'x'.repeat(80) }), /underline stage/);
    assert.throws(() => copyColorBinding({ ...base, underlineStage: 'underline-resolution' }), /underline stage/);
});

const invalid = (name, change, message = /Invalid resolved-color run/) => test('underline run validation refuses ' + name, () => {
    const value = screen();
    const runs = change(value.resolvedColors.map(run => ({ ...run, ul: { ...run.ul } })));
    assert.throws(() => copyResolvedColors(runs, value.rows * value.cols, copyColorBinding(value.colorBinding)), message);
});
invalid('an unknown source', runs => { runs[0].ul.source = 'theme'; return runs; });
invalid('none with a color', runs => { runs[2].ul = { source: 'none', index: -1, argb: 0xff000000 }; return runs; });
invalid('palette with the wrong entry value', runs => { runs[1].ul.argb = 0xff010203; return runs; });
invalid('palette with an out-of-range index', runs => { runs[1].ul.index = 256; return runs; });
invalid('literal with an index', runs => { runs[1].ul = { source: 'literal', index: 5, argb: 0xff010203 }; return runs; });
invalid('inherit without a channel', runs => { delete runs[0].ul.inheritsFrom; return runs; });
invalid('inherit with an unknown channel', runs => { runs[0].ul.inheritsFrom = 'cursor'; return runs; });
invalid('a transparent color', runs => { runs[1].ul.argb = 0x00010203; return runs; });
invalid('a missing underline', runs => { delete runs[0].ul; return runs; });

test('coherence refuses an App underline that disagrees with the same cell\'s stored state', () => {
    const base = screenSnapshot(screen(), true, true, false, true, false, true, true);
    const mutate = (position, ul) => {
        const cellsOfRuns = expandResolvedColors(base.resolvedColors, base.rows * base.cols, base.colorBinding);
        cellsOfRuns[position] = { ...cellsOfRuns[position], ul };
        const runs = [];
        for (const cell of cellsOfRuns) {
            const last = runs.at(-1);
            if (last && JSON.stringify({ fg: last.fg, bg: last.bg, ul: last.ul }) === JSON.stringify({ fg: cell.fg, bg: cell.bg, ul: cell.ul })) last.count++;
            else runs.push({ count: 1, ...cell });
        }
        return { ...base, resolvedColors: runs };
    };
    const palette = base.colorBinding.ansiPaletteArgb;
    assert.throws(() => checkUnderlineCoherence(mutate(2, { source: 'palette', index: 5, argb: palette[5] })), /presence disagrees/);
    assert.throws(() => checkUnderlineCoherence(mutate(0, { source: 'none', index: -1 })), /presence disagrees/);
    assert.throws(() => checkUnderlineCoherence(mutate(1, { source: 'palette', index: 4, argb: palette[4] })), /palette index disagrees/);
    assert.throws(() => checkUnderlineCoherence(mutate(1, { source: 'literal', index: -1, argb: palette[5] })), /literal underline disagrees|palette index disagrees/);
    assert.throws(() => checkUnderlineCoherence(mutate(0, { source: 'inherit', index: -1, inheritsFrom: 'background', argb: base.colorBinding.backgroundArgb })), /inherited underline disagrees/);
    assert.throws(() => checkUnderlineCoherence({ ...base, colorBinding: paletteBinding() }), /was not requested/);
});

test('inverse cells with a foreground palette index still inherit the stored background channel', () => {
    const inverse = [colorCell('I', { underlineStyle: 1, attrs: ['inverse'], fg: paletteChannel(2), bg: paletteChannel(5) })];
    const snapshot = screenSnapshot(syntheticUnderlineScreen(inverse, paletteBinding()), true, true, false, true, false, true, true);
    const [cell] = expandResolvedColors(snapshot.resolvedColors, snapshot.rows * snapshot.cols, snapshot.colorBinding);
    assert.equal(cell.ul.inheritsFrom, 'background');
    assert.equal(cell.ul.argb, snapshot.colorBinding.ansiPaletteArgb[5]);
});

test('the owned observer sends the underline request flag only when asked', async () => {
    const { OwnedCellObserver } = await import('./agent-observer.mjs');
    const pane = '0f0e0d0c-0b0a-4009-8008-700600500400';
    const flagFor = async underline => {
        const requests = [];
        const observer = new OwnedCellObserver({ appData: 'a', ownerPid: 1, profileName: 'p', nonce: 'N', includeAttributes: true, includeCellDetails: true,
            includeOverline: true, includeResolvedColors: true, includeResolvedUnderlineColors: underline, prefix: 'sgur', expectedPaneId: pane,
            endpoint: () => 'endpoint',
            rpc: async (endpoint, method, params) => {
                requests.push({ method, params });
                if (method === 'listSessions') return { sessions: [{ paneId: pane, kind: 'local', windowless: false, profileName: 'p', rows: 6, cols: 60 }] };
                return {}; // the empty response is refused after the request was recorded
            } });
        await assert.rejects(() => observer.capture(() => 1000));
        return requests.find(item => item.method === 'readScreen').params;
    };
    assert.equal((await flagFor(true)).includeResolvedUnderlineColors, true);
    assert.equal((await flagFor(true)).includeResolvedColors, true);
    assert.equal(Object.hasOwn(await flagFor(false), 'includeResolvedUnderlineColors'), false);
});
