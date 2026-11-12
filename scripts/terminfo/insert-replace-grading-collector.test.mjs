import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { charEditSeams } from './char-edit-grading-collector.mjs';
import { adaptInsertReplaceCollector, insertReplaceAdapterId } from './insert-replace-grading-collector.mjs';
import { insertReplacePinnedSeam } from './insert-replace-pinned.mjs';

const helpers = charEditSeams.filter(seam => seam.startsWith('function isBlank(') || seam.startsWith('function parserStateResult('));
const previous = ['export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {'];
const input = () => [insertReplacePinnedSeam, ...helpers, ...previous].join('\n');

test('the retained seam pins both complete probe paths and appends only an export', () => {
    assert.equal(createHash('sha256').update(insertReplacePinnedSeam).digest('hex'), 'a9e9c28bc23dd45070e6f372c214a9cfff9adeb8491a4c71ea173f90c4d363b1');
    assert.match(insertReplacePinnedSeam, /ctx\.feed\("\\x1B\[4l\\x1B\[1;1H\\x1B\[2KABC"\)/);
    assert.match(insertReplacePinnedSeam, /ctx\.write\("ABCD"\)/);
    assert.match(insertReplacePinnedSeam, /Cursor advance does not measure whether X displaced the existing cells/);
    const source = input(), output = adaptInsertReplaceCollector(source);
    assert.ok(output.startsWith(source));
    assert.match(output.slice(source.length), new RegExp(insertReplaceAdapterId));
});

test('changed fixture, terminal path, helper, predecessor or duplicate seam is refused', () => {
    const source = input();
    for (const mutated of [source.replace('ABC"', 'ABD"'), source.replace('ctx.write("ABCD")', 'ctx.write("ABCE")'),
        source.replace('return char ===', 'return char !=='), source.replace('ntildeLrMarginsAdapter', 'otherAdapter'),
        source + insertReplacePinnedSeam]) {
        assert.throws(() => adaptInsertReplaceCollector(mutated), /seam changed/);
    }
});

const bundle = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through its predecessors and agrees with both independent replays',
    { skip: existsSync(bundle) ? false : 'collector cache not installed' }, async () => {
        const { readFileSync, writeFileSync, rmSync } = await import('node:fs');
        const { fileURLToPath, pathToFileURL } = await import('node:url');
        const { adaptCollector } = await import('./collector.mjs');
        const { adaptLrMarginsCollector } = await import('./lr-margins-grading-collector.mjs');
        const { insertReplaceTermlessContext, insertReplaceTerminalContext } = await import('./insert-replace-collector.mjs');
        const { evidence } = await import('./fixtures/insert-replace-grading-evidence.mjs');
        const { replayInsertReplacePinned, replayInsertReplaceTerminal } = await import('./insert-replace-pinned-replay.mjs');
        const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
            'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
            'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum'];
        const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
            .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
        const bytes = readFileSync(bundle);
        assert.equal(createHash('sha256').update(bytes).digest('hex'), 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6');
        let source = adaptCollector(bytes.toString(), new URL('./ownership.mjs', import.meta.url).pathname);
        for (const adapt of [...layers, adaptLrMarginsCollector, adaptInsertReplaceCollector]) source = adapt(source);
        const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-irm-test-${process.pid}.mjs`, import.meta.url));
        try {
            writeFileSync(file, source, { flag: 'wx' });
            const module = await import(pathToFileURL(file).href), screens = evidence().frames.slice(2, -1).map(frame => frame.screen);
            const termless = module.ntildeRunInsertReplaceTermless('modes.insert-replace', insertReplaceTermlessContext(screens));
            const expected = replayInsertReplacePinned({ cols: screens[1].cols, rows: screens[1].rows, seed: ['A', 'B', 'C', ' '],
                origin: { x: 0, y: 0, visible: true }, after: ['X', 'A', 'B', 'C'] });
            assert.deepEqual(termless, { probeId: 'modes.insert-replace', ...expected });
            const terminal = await module.ntildeRunInsertReplaceTerminal('modes.insert-replace', insertReplaceTerminalContext(screens.at(-1)));
            assert.deepEqual(terminal, { probeId: 'modes.insert-replace', ...replayInsertReplaceTerminal({ cols: screens[1].cols,
                rows: screens[1].rows, position: { row: 1, col: 3 } }) });
        } finally { rmSync(file, { force: true }); }
    });
