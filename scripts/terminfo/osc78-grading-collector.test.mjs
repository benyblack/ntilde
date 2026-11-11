import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptOsc78Collector, osc78AdapterId, osc78Seams } from './osc78-grading-collector.mjs';
import { osc78CaseIds, osc78Pinned, osc7CaseId, osc8CaseId } from './osc78-contracts.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => osc78Seams.join('\n/* gap */\n') + '\n';

test('derives the OSC 7/8 extension by appending exports only', () => {
    const source = verbatim(), derived = adaptOsc78Collector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeOsc78Adapter = Object.freeze({\n  id: "${osc78AdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunOsc78Termless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunOsc78Terminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...osc78CaseIds])));
});

test('the seams pin both registrations with both paths, the hyperlink helper and the left/right-margin layer', () => {
    const osc7 = osc78Seams.find(item => item.includes('probe("extensions.osc7-cwd"'));
    assert.ok(osc7.includes('ctx.capabilities.extensions.has("osc7")') && osc7.includes('evidence: "legacy"') && osc7.includes('evidence: "none"'));
    assert.ok(osc7.includes('No working-directory metadata readback for OSC 7') && !osc7.includes('ctx.feed(') && !osc7.includes('ctx.write('), 'the pinned OSC 7 probe carries no fixture bytes');
    const osc8 = osc78Seams.find(item => item.includes('...probe("extensions.osc8"'));
    assert.ok(osc8.includes(`const uri = "${osc78Pinned[osc8CaseId].terminalWrites[1].match(/8;;([^\x07]*)/)[1]}";`));
    assert.ok(osc8.includes('ctx.feed(`\\x1B[1;1H\\x1B[2KA\\x1B]8;;${uri}\\x07LINK\\x1B]8;;\\x07Z`);'), 'termless fixture');
    assert.ok(osc8.includes('ctx.write("\\x1B[1;1H\\x1B[2K");') && osc8.includes('ctx.write("A\\x1B]8;;https://example.com/osc8-proof\\x07LINK\\x1B]8;;\\x07Z");'), 'terminal fixture');
    assert.ok(osc8.includes('const expectedChars = "ALINKZ";') && osc8.includes('[null, uri, uri, uri, uri, null]') && osc8.includes('cells.slice(1, 5)'));
    assert.ok(osc8.includes('Cursor movement cannot verify OSC 8 link metadata or click behavior') && osc8.includes('termNeedsGeometry: true'));
    const helper = osc78Seams.find(item => item.startsWith('function readHyperlinkMetadata('));
    for (const part of ['if (!available)', 'getHyperlinkAt method', 'return hyperlink;']) assert.ok(helper.includes(part), part);
    assert.ok(osc78Seams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of osc78Seams) {
        assert.throws(() => adaptOsc78Collector(verbatim().split(seam).join('CHANGED')), /Pinned OSC 7\/8 probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptOsc78Collector(verbatim() + seam + '\n'), /Pinned OSC 7\/8 probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would gain a verdict, change its URI, its range or its bytes', () => {
    for (const [from, to] of [['declared: ctx.capabilities.extensions.has("osc7")', 'declared: true'], ['evidence: "legacy"', 'evidence: "none"'],
        ['https://example.com/osc8-proof', 'https://example.com/other'], ['[null, uri, uri, uri, uri, null]', '[null, uri, uri, uri, null, null]'],
        ['const expectedChars = "ALINKZ";', 'const expectedChars = "ALINKX";'], ['cells.slice(1, 5)', 'cells.slice(1, 4)'], ['ctx.getCell(0, col)', 'ctx.getCell(1, col)'],
        ['ctx.cols < 7', 'ctx.cols < 6'], ['evidence: "consumed"', 'evidence: "parser-state"']]) {
        assert.ok(osc78Seams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptOsc78Collector(osc78Seams.map(seam => seam.replaceAll(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over served terminals, matching the independent replay', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { osc78TermlessContext, osc78TerminalContext, replayOsc78Pinned } = await import('./osc78-contracts.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptOsc78Collector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-lnk-test-${process.pid}.mjs`, import.meta.url));
    const run = (lines, cursor, links) => ({ lines, rows: 12, cols: 48, cursorRow: cursor[0], cursorCol: cursor[1], links });
    const uri = 'https://example.com/osc8-proof', link = { row: 0, col: 1, length: 4, identity: 0, uri, id: null };
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeOsc78Adapter.probeIds], [...osc78CaseIds]);
        const screens = [
            ['pinned effect', run(['ALINKZ'], [0, 6]), [link]], ['no link metadata', run(['ALINKZ'], [0, 6]), []], ['link leaked past the close', run(['ALINKZ'], [0, 6]), [{ ...link, length: 5 }]],
            ['link ends early', run(['ALINKZ'], [0, 6]), [{ ...link, length: 3 }]], ['wrong URI', run(['ALINKZ'], [0, 6]), [{ ...link, uri: 'https://example.com/other' }]],
            ['wrong text', run(['ALINKX'], [0, 6]), [link]], ['link on the witness only', run(['ALINKZ'], [0, 6]), [{ ...link, col: 5, length: 1 }]]];
        for (const [name, screen, links] of screens) for (const caseId of osc78CaseIds) {
            const termlessContext = osc78TermlessContext(caseId, screen, links), terminalContext = osc78TerminalContext(caseId, screen);
            const { probeId, ...termless } = module.ntildeRunOsc78Termless(caseId, termlessContext);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunOsc78Terminal(caseId, terminalContext);
            assert.equal(probeId, caseId); assert.equal(terminalId, caseId);
            assert.equal(termlessContext.fedCount, osc78Pinned[caseId].termlessFeeds.length); assert.equal(terminalContext.writtenCount, osc78Pinned[caseId].terminalWrites.length);
            const replay = replayOsc78Pinned(caseId, screen, links);
            assert.deepEqual(termless, replay.termless, `${name} ${caseId} termless`);
            assert.deepEqual(terminal, replay.terminal, `${name} ${caseId} terminal`);
            assert.equal(terminal.observation.outcome, 'inconclusive', 'neither terminal path decides');
            if (caseId === osc7CaseId) assert.equal(termless.observation.outcome, 'inconclusive');
        }
        const outcome = async (screen, links) => module.ntildeRunOsc78Termless(osc8CaseId, osc78TermlessContext(osc8CaseId, screen, links)).observation.outcome;
        assert.equal(await outcome(...screens[0].slice(1)), 'supported');
        for (const index of [2, 4]) assert.equal(await outcome(...screens[index].slice(1)), 'unsupported', screens[index][0]);
        for (const index of [1, 3, 5, 6]) assert.equal(await outcome(...screens[index].slice(1)), 'inconclusive', screens[index][0]);
    } finally { rmSync(file, { force: true }); }
});
