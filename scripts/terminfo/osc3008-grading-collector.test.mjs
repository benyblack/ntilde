import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { test } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { adaptOsc3008Collector, osc3008AdapterId, osc3008Seams } from './osc3008-grading-collector.mjs';
import { osc3008CaseId, osc3008PinnedWrites } from './osc3008-contracts.mjs';

const verbatim = () => osc3008Seams.join('\n/* gap */\n') + '\n';

test('the extension appends exports and refuses changed or duplicated pinned seams', () => {
    const source = verbatim(), derived = adaptOsc3008Collector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(osc3008AdapterId));
    for (const seam of osc3008Seams) {
        assert.throws(() => adaptOsc3008Collector(source.replace(seam, 'changed')), /seam changed/);
        assert.throws(() => adaptOsc3008Collector(source + seam), /seam changed/);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned registration executes its one unchanged path and has no termless function', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const bytes = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(bytes).digest('hex'), 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6');
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    let source = adaptCollector(bytes.toString(), fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    for (const name of names) {
        const module = await import(name === 'grading' ? './grading-collector.mjs' : `./${name}-grading-collector.mjs`);
        source = Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt'))(source);
    }
    source = adaptOsc3008Collector(source);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-osc3008-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, source);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeOsc3008Adapter.probeIds], [osc3008CaseId]);
        assert.equal(module.ntildeOsc3008HasTermless(osc3008CaseId), false);
        for (const position of [{ row: 1, col: 1 }, { row: 3, col: 7 }, null]) {
            const writes = [];
            const result = await module.ntildeRunOsc3008Terminal(osc3008CaseId, { write: value => writes.push(value), queryCursorPosition: async () => position });
            assert.deepEqual(writes, [...osc3008PinnedWrites]);
            assert.equal(result.probeId, osc3008CaseId);
            assert.equal(result.pass, false);
            assert.equal(result.observation.outcome, 'inconclusive');
            assert.equal(result.observation.evidence, 'query');
            assert.equal(result.note, position ? 'Cursor response does not measure OSC 3008 systemd context' : 'No cursor response after OSC 3008 systemd context');
            assert.equal(result.response, position ? `${position.row};${position.col}` : undefined);
        }
        assert.throws(() => module.ntildeOsc3008HasTermless('extensions.osc3008-other'), /Unknown pinned probe/);
        await assert.rejects(module.ntildeRunOsc3008Terminal('extensions.osc3008-other', {}), /Unknown pinned terminal probe/);
    } finally { rmSync(file, { force: true }); }
});
