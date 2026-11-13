import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptXtgettcapCollector, xtgettcapAdapterId, xtgettcapSeams } from './xtgettcap-grading-collector.mjs';
import { pinned, replayXtgettcapPinned, xtgettcapCaseIds, xtgettcapContracts } from './xtgettcap-contracts.mjs';
import { xtgettcapRecordingContext } from './xtgettcap-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => xtgettcapSeams.join('\n/* gap */\n') + '\n';
const refusal = '\x1bP0+r\x1b\\';
const da = '\x1b[?62;4;22c';

test('derives the XTGETTCAP extension by appending exports only', () => {
    const source = verbatim(), derived = adaptXtgettcapCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeXtgettcapAdapter = Object.freeze({\n  id: "${xtgettcapAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunXtgettcapTermless(probeId, ctx) {'));
    assert.ok(derived.includes('export async function ntildeRunXtgettcapTerminal(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify([...xtgettcapCaseIds])));
});

test('the seam pins the exact registration and the title/icon-stack layer', () => {
    const seam = xtgettcapSeams[0];
    assert.ok(seam.includes('id: "device.xtgettcap"'));
    assert.ok(seam.includes('query: "\\x1BP+q544e\\x1B\\\\"'), 'the exact pinned request bytes');
    assert.ok(seam.includes('valid: /\\x1bP1\\+r544e=(?:[0-9A-Fa-f]{2})+\\x1b\\\\/i'), 'the case-insensitive value criterion');
    assert.ok(seam.includes('refusal: /\\x1bP0\\+r\\x1b\\\\/'), 'the bare refusal criterion');
    assert.ok(seam.includes('malformed: /\\x1bP[01]\\+r/'), 'the malformed framing criterion');
    assert.ok(seam.includes(JSON.stringify(pinned.expected)), 'the pinned criterion text');
    assert.ok(xtgettcapSeams.includes('export const ntildeTitleStackAdapter = Object.freeze({'), 'derived from the title/icon-stack layer');
    assert.ok(xtgettcapSeams.includes('export function ntildeRunTitleStackTermless(probeId, ctx) {'));
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of xtgettcapSeams) {
        assert.throws(() => adaptXtgettcapCollector(verbatim().split(seam).join('CHANGED')), /Pinned XTGETTCAP probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptXtgettcapCollector(verbatim() + seam + '\n'), /Pinned XTGETTCAP probe seam changed/, seam.slice(0, 40));
    }
});

test('refuses a pinned body that would change its request bytes, its criteria or its refusal framing', () => {
    for (const [from, to] of [['+q544e', '+q5463'], ['[0-9A-Fa-f]{2}', '[0-9A-Fa-f]'], ['P0\\+r', 'P2\\+r'], ['P[01]\\+r', 'P[012]\\+r'],
        ['status 1 for TN', 'status 1 for Tc'], ['device.xtgettcap', 'device.xtgetxres']]) {
        assert.ok(xtgettcapSeams.some(seam => seam.includes(from)), from);
        assert.throws(() => adaptXtgettcapCollector(xtgettcapSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n')), /seam changed/, from);
    }
});

const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
test('the real pinned bundle derives through every layer and the pinned code runs over served replies', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, async () => {
    const { adaptCollector } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins', 'title-stack'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    for (const adapt of [...layers, adaptXtgettcapCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-xgtc-test-${process.pid}.mjs`, import.meta.url));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.deepEqual([...module.ntildeXtgettcapAdapter.probeIds], [...xtgettcapCaseIds]);
        // The refused pinned exchange: both paths are inconclusive, the termless one with the pinned note.
        const refused = { barrier: da, replies: [refusal] };
        const termlessContext = xtgettcapRecordingContext(xtgettcapContracts[0], refused);
        const { probeId, ...termless } = module.ntildeRunXtgettcapTermless('device.xtgettcap', termlessContext);
        assert.equal(probeId, 'device.xtgettcap');
        assert.equal(termlessContext.fedCount, 1);
        assert.deepEqual(termless, replayXtgettcapPinned(refusal));
        assert.equal(termless.observation.outcome, 'inconclusive');
        assert.equal(termless.observation.reason, 'insufficient-evidence');
        // The terminal path replays over the same journaled bytes and is equally inconclusive.
        const terminalContext = xtgettcapRecordingContext(xtgettcapContracts[0], refused);
        const { probeId: terminalId, ...terminal } = await module.ntildeRunXtgettcapTerminal('device.xtgettcap', terminalContext);
        assert.equal(terminalId, 'device.xtgettcap');
        assert.deepEqual(terminal, replayXtgettcapPinned(refusal + da));
        assert.equal(terminal.observation.outcome, 'inconclusive');
        // A served value reply keeps the pinned criterion able to say supported (the refusal stays honest, not blind).
        const valueContext = xtgettcapRecordingContext(xtgettcapContracts[0], { barrier: da, replies: ['\x1bP1+r544e=323536' + '\x1b\\'] });
        assert.equal(module.ntildeRunXtgettcapTermless('device.xtgettcap', valueContext).observation.outcome, 'supported');
    } finally { rmSync(file, { force: true }); }
});

test('the served pinned contexts refuse any feed, write, read or query outside the pinned fixture', async () => {
    const contract = xtgettcapContracts[0];
    const context = xtgettcapRecordingContext(contract, { barrier: da, replies: [refusal] });
    assert.throws(() => context.feed(pinned.query), /must not plain-feed/);
    assert.throws(() => context.write(pinned.query), /must not write/);
    assert.throws(() => context.getCell(), /must not read cells/);
    assert.equal(context.feedCapture(pinned.query), refusal);
    assert.throws(() => context.feedCapture(pinned.query), /captures more than once/);
    const asked = xtgettcapRecordingContext(contract, { barrier: da, replies: [refusal] });
    await assert.rejects(asked.queryWithSentinelOutcome('\x1bP+q5463\x1b\\', /x/), /differs from the contract/);
    const terminal = xtgettcapRecordingContext(contract, { barrier: da, replies: [refusal] });
    await terminal.queryWithSentinelOutcome(pinned.query, /\x1bP0\+r\x1b\\/);
    await assert.rejects(terminal.queryWithSentinelOutcome(pinned.query, /\x1bP0\+r\x1b\\/), /outside its fixture/, 'a second terminal query is refused');
});
