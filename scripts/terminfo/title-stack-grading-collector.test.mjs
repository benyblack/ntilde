import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptTitleStackCollector, titleStackAdapterId, titleStackSeams } from './title-stack-grading-collector.mjs';
import { replayTitleStackTerminal, replayTitleStackTermless, titleStackCaseIds, titleStackPinned, titleStackTerminalContext, titleStackTermlessContext } from './title-stack-contracts.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => titleStackSeams.join('\n/* gap */\n') + '\n';

test('derives the title-stack extension by appending exports only', () => {
    const source = verbatim(), derived = adaptTitleStackCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeTitleStackAdapter = Object.freeze({\n  id: "${titleStackAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunTitleStackTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunTitleStackTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...titleStackCaseIds])));
});

test('the seams pin both registrations with both paths and the left/right-margin layer', () => {
    const escaped = text => text.replaceAll('\x1b', '\\x1B').replaceAll('\x07', '\\x07');
    for (const caseId of titleStackCaseIds) {
        const seam = titleStackSeams.find(item => item.includes(`...probe("${caseId}"`));
        assert.ok(seam, caseId);
        const pinned = titleStackPinned[caseId];
        for (const feed of pinned.termlessFeeds) assert.ok(seam.includes(`ctx.feed("${escaped(feed)}");`), `${caseId} termless fixture ${JSON.stringify(feed)}`);
        for (const write of pinned.terminalWrites) assert.ok(seam.includes(`ctx.write("${escaped(write)}");`), `${caseId} terminal fixture ${JSON.stringify(write)}`);
        assert.equal(seam.split('ctx.getTitle()').length - 1, 3, `${caseId} reads the title three times`);
        assert.ok(seam.includes('original === "pushed-title" && changed === "new-title" && restored === "pushed-title" ? true : null'), 'the verdict never becomes unsupported');
        assert.ok(seam.includes('const pos = await ctx.queryCursorPosition();') && seam.includes('observation: { outcome: "inconclusive", reason: "insufficient-evidence", evidence: "consumed" }'));
        assert.ok(seam.includes(pinned.expected), `${caseId} criterion`);
        assert.ok(seam.includes('termlessObservationEvidence: "parser-state"'));
    }
    assert.ok(titleStackSeams.includes('export const ntildeLrMarginsAdapter = Object.freeze({'), 'derived from the left/right-margin layer');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of titleStackSeams) {
        assert.throws(() => adaptTitleStackCollector(verbatim().split(seam).join('CHANGED')), /Pinned title-stack probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptTitleStackCollector(verbatim() + seam + '\n'), /Pinned title-stack probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change its bytes, its reads or its verdict rule', () => {
    for (const [from, to] of [['\\x1B[22;0t', '\\x1B[22;1t'], ['\\x1B[23;0t', '\\x1B[23;2t'], ['pushed-title"', 'other-title"'], ['changed === "new-title"', 'changed !== "new-title"'],
        ['? true : null', '? true : false'], ['restored === "pushed-title" ?', 'restored === "new-title" ?'], ['ctx.getTitle()', 'ctx.getIconName()']]) {
        assert.ok(titleStackSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptTitleStackCollector(titleStackSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const titlesOf = (original, changed, restored) => ({ 'pinned-original': original, 'pinned-changed': changed, 'pinned-restored': restored });
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over served terminals', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptTitleStackCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-tst-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeTitleStackAdapter.probeIds], [...titleStackCaseIds]);
        const observed = [titlesOf('pushed-title', 'new-title', 'pushed-title'), titlesOf('pushed-title', 'new-title', 'new-title'), titlesOf('x', 'new-title', 'pushed-title'),
            titlesOf('pushed-title', 'y', 'pushed-title'), titlesOf('', '', '')];
        for (const titles of observed) for (const caseId of titleStackCaseIds) {
            const termlessContext = titleStackTermlessContext(caseId, titles), terminalContext = titleStackTerminalContext(caseId, [3, 7]);
            const { probeId, ...termless } = module.ntildeRunTitleStackTermless(caseId, termlessContext);
            const { probeId: terminalId, ...terminal } = await module.ntildeRunTitleStackTerminal(caseId, terminalContext);
            assert.equal(probeId, caseId); assert.equal(terminalId, caseId);
            assert.equal(termlessContext.fedCount, 4); assert.equal(termlessContext.readCount, 3); assert.equal(terminalContext.writtenCount, titleStackPinned[caseId].terminalWrites.length);
            assert.deepEqual(termless, replayTitleStackTermless(caseId, titles), `${caseId} termless`);
            assert.deepEqual(terminal, replayTitleStackTerminal(caseId, true), `${caseId} terminal`);
            assert.notEqual(termless.observation.outcome, 'unsupported', 'the pinned termless probe never returns unsupported');
            assert.equal(terminal.observation.outcome, 'inconclusive', 'the pinned terminal path never decides');
        }
        assert.equal(module.ntildeRunTitleStackTermless(titleStackCaseIds[0], titleStackTermlessContext(titleStackCaseIds[0], observed[0])).observation.outcome, 'supported');
    } finally { rmSync(file, { force: true }); }
});

test('the served pinned contexts refuse any feed, write, read or query outside the pinned fixture', () => {
    const [push, pop] = titleStackCaseIds;
    const context = titleStackTermlessContext(push, titlesOf('a', 'b', 'c'));
    assert.throws(() => context.feed('\x1b]2;other\x07'), /feed differs/);
    assert.throws(() => context.getTitle(), /outside its contracted points/, 'a read before the first feed');
    const fed = titleStackTermlessContext(push, titlesOf('a', 'b', 'c'));
    fed.feed(titleStackPinned[push].termlessFeeds[0]); assert.equal(fed.getTitle(), 'a');
    assert.throws(() => fed.getTitle(), /outside its contracted points/, 'a second read at the same point');
    const terminal = titleStackTerminalContext(pop, [0, 0]);
    assert.throws(() => terminal.queryCursorPosition(), /early/);
    assert.throws(() => terminal.write('\x1b[22;1t'), /write differs/);
});
