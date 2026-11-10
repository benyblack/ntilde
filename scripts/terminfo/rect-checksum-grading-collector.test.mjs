import assert from 'node:assert/strict';
import { test } from 'node:test';
import { existsSync } from 'node:fs';
import { adaptRectChecksumCollector, rectChecksumAdapterId, rectChecksumSeams } from './rect-checksum-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => rectChecksumSeams.join('\n/* gap */\n') + '\n';

test('derives the rectangle-checksum extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptRectChecksumCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeRectChecksumAdapter = Object.freeze({\n  id: "${rectChecksumAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunRectChecksumTermless(probeId, ctx) {'));
    assert.ok(derived.includes(JSON.stringify(['editing.decrqcra'])));
    assert.equal(adaptRectChecksumCollector(source), derived);
});

test('the seams pin exactly the DECRQCRA registration, checksumResult and the previous layer', () => {
    const registration = rectChecksumSeams.find(seam => seam.includes('probe("editing.decrqcra"'));
    assert.ok(registration.includes('ctx.feed("ABCDE\\x1B[1;1H");'));
    assert.ok(registration.includes('return checksumResult(ctx.feedCapture("\\x1B[1;1;1;1;1;5*y"));'));
    assert.ok(registration.includes('ctx.queryOutcome("\\x1B[1;1;1;1;1;1*y", /\\x1bP1!~[0-9A-Fa-f]{4}\\x1b\\\\/, 2000)'));
    const helper = rectChecksumSeams.find(seam => seam.startsWith('function checksumResult('));
    for (const part of ['outcome: supported ? "supported" : "inconclusive"', 'evidence: "query"', 'reason: response ? "invalid-reply" : "no-response"',
        'checksum arithmetic is not verified']) assert.ok(helper.includes(part), part);
    assert.ok(rectChecksumSeams.includes('export const ntildeColEditAdapter = Object.freeze({'), 'derived from the column-editing layer');
    assert.ok(!rectChecksumSeams.some(seam => /modes\.left-right-margin/.test(seam)), 'other cases are outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of rectChecksumSeams) {
        assert.throws(() => adaptRectChecksumCollector(verbatim().split(seam).join('CHANGED')), /Pinned rectangle-checksum probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptRectChecksumCollector(verbatim() + seam + '\n'), /Pinned rectangle-checksum probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptRectChecksumCollector(''), /Pinned rectangle-checksum probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criterion or notes were substituted', () => {
    const edits = [
        ['ctx.feed("ABCDE\\x1B[1;1H");', 'ctx.feed("ABCDE");'],
        ['ctx.feedCapture("\\x1B[1;1;1;1;1;5*y")', 'ctx.feedCapture("\\x1B[1;1;1;1;1;1*y")'],
        ['/\\x1bP1!~[0-9A-Fa-f]{4}\\x1b\\\\/.test(response)', '/\\x1bP/.test(response)'],
        ['outcome: supported ? "supported" : "inconclusive"', 'outcome: supported ? "supported" : "unsupported"'],
        ['checksum arithmetic is not verified', 'checksum arithmetic is verified'],
        ['ctx.queryOutcome("\\x1B[1;1;1;1;1;1*y"', 'ctx.queryOutcome("\\x1B[1;1;1;1;1;5*y"'],
    ];
    for (const [from, to] of edits) {
        assert.ok(rectChecksumSeams.some(seam => seam.includes(from)), from);
        const mutated = rectChecksumSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptRectChecksumCollector(mutated), /Pinned rectangle-checksum probe seam changed/, from);
    }
});

// The real pinned bundle, derived through all twenty-five earlier layers and EXECUTED over journaled replies: the pinned termless
// code must agree with the independent replay. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
test('the real pinned bundle derives through every layer and its DECRQCRA code agrees with the replay', { skip: haveBundle ? false : 'collector cache not installed' }, async () => {
    const { createHash } = await import('node:crypto');
    const { adaptCollector, upstreamBundleSha256 } = await import('./collector.mjs');
    const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
        'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
        'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit'];
    const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
        .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
    assert.equal(layers.length, 24);
    const { replayRectChecksumPinned } = await import('./rect-checksum-grading.mjs');
    const { rectChecksumRecordingContext } = await import('./rect-checksum-collector.mjs');
    const { rectChecksumContracts } = await import('./rect-checksum-contracts.mjs');
    const { writeFileSync, rmSync, readFileSync } = await import('node:fs');
    const { pathToFileURL, fileURLToPath } = await import('node:url');
    const source = readFileSync(bundleUrl);
    assert.equal(createHash('sha256').update(source).digest('hex'), upstreamBundleSha256);
    const owner = new URL('./ownership.mjs', import.meta.url).pathname;
    let text = adaptCollector(source.toString(), owner);
    for (const adapt of [...layers, adaptRectChecksumCollector]) text = adapt(text);
    const file = fileURLToPath(new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-csum-test-${process.pid}.mjs`, import.meta.url));
    const contract = rectChecksumContracts[0];
    const replyAt = raw => contract.roles.map((role, index) => (index === contract.pinnedRead && raw !== null ? { raw } : null));
    try {
        writeFileSync(file, text);
        const module = await import(pathToFileURL(file).href);
        assert.equal(module.ntildeRectChecksumAdapter.id, rectChecksumAdapterId);
        assert.deepEqual([...module.ntildeRectChecksumAdapter.probeIds], ['editing.decrqcra']);
        for (const [raw, outcome] of [['\x1bP1!~FEB1\x1b\\', 'supported'], ['\x1bP1!~feb1\x1b\\', 'supported'], [null, 'inconclusive'],
            ['\x1bP2!~FEB1\x1b\\', 'inconclusive'], ['\x1bP1!~FEB\x1b\\', 'inconclusive'], ['\x1bP0$r\x1b\\', 'inconclusive']]) {
            const replies = replyAt(raw);
            const context = rectChecksumRecordingContext(contract, replies);
            const live = module.ntildeRunRectChecksumTermless(contract.caseId, context);
            assert.equal(context.fedCount, 2, String(raw));
            const { probeId, ...result } = live;
            assert.equal(probeId, contract.caseId);
            assert.deepEqual(result, replayRectChecksumPinned(contract, replies), String(raw));
            assert.equal(live.observation.outcome, outcome, String(raw));
        }
    } finally { rmSync(file, { force: true }); }
});
