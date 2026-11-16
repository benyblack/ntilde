import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { test } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { adaptOsc633Collector, osc633AdapterId, osc633CaseIds, osc633Seams } from './osc633-grading-collector.mjs';

const verbatim = () => osc633Seams.join('\n/* gap */\n') + '\n';

test('the extension appends exports and refuses changed or duplicated pinned seams', () => {
    const source = verbatim(), derived = adaptOsc633Collector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(osc633AdapterId));
    for (const seam of osc633Seams) {
        assert.throws(() => adaptOsc633Collector(source.replace(seam, 'changed')), /seam changed/);
        assert.throws(() => adaptOsc633Collector(source + seam), /seam changed/);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned registrations execute both unchanged paths', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const bytes = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(bytes).digest('hex'), 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6');
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins', 'osc133'];
    let source = adaptCollector(bytes.toString(), fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    for (const name of names) {
        const module = await import(name === 'grading' ? './grading-collector.mjs' : `./${name}-grading-collector.mjs`);
        source = Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt'))(source);
    }
    source = adaptOsc633Collector(source);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-osc633-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, source);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeOsc633Adapter.probeIds], [...osc633CaseIds]);
        for (const [index, id] of osc633CaseIds.entries()) {
            const marker = ['A', 'B', 'C', 'D;0'][index];
            const fixture = `\x1b]633;${marker}\x07X`;
            for (const consumed of [true, false]) {
                const feeds = [], writes = [];
                const termless = module.ntildeRunOsc633Termless(id, { feed: value => feeds.push(value), getCell: (row, col) => {
                    assert.deepEqual([row, col], [0, 0]); return { char: consumed ? 'X' : ' ' };
                } });
                const terminal = await module.ntildeRunOsc633Terminal(id, { write: value => writes.push(value), queryCursorPosition: async () => ({ col: consumed ? 2 : 1 }) });
                assert.deepEqual(feeds, [fixture]);
                assert.deepEqual(writes, ['\x1b[1;1H\x1b[2K', fixture]);
                for (const result of [termless, terminal]) {
                    assert.equal(result.probeId, id);
                    assert.equal(result.pass, false);
                    assert.equal(result.observation.outcome, 'inconclusive');
                    assert.equal(result.observation.evidence, 'consumed');
                    assert.match(result.note, consumed ? /^Marker was consumed/ : /^Marker consumption was not confirmed/);
                }
            }
        }
        assert.throws(() => module.ntildeRunOsc633Termless('extensions.osc133-a', {}), /Unknown pinned probe/);
        await assert.rejects(module.ntildeRunOsc633Terminal('extensions.osc633-e', {}), /Unknown pinned probe/);
    } finally { rmSync(file, { force: true }); }
});
