import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { test } from 'node:test';
import { adaptCursorQualCollector, cursorGeometryRepairId, cursorQualAdapterId, cursorQualSeams, cursorRepairedProbeIds, pinnedCursorSection, pinnedCursorSectionSha256,
    pinnedReverseWrapSha256, pinnedShapeSha256, pinnedTermContextSha256 } from './cursor-qual-grading-collector.mjs';
import { pinnedCallbackDigests } from './cursor-qual-grading.mjs';

const sha = text => createHash('sha256').update(text).digest('hex');
const { sectionStart, sectionEnd, reverseWrapStart, reverseWrapEnd, shapeStart, shapeEnd, contextStart, contextEnd, helperSeams } = cursorQualSeams;

/** A synthetic layered source with every pinned seam, and the digests that make it acceptable. */
function synthetic({ reverseBody = ' /* reverse */ ', shapeBody = ' /* shape */ ', context = ' /* context */ ', extra = '' } = {}) {
    const reverse = reverseWrapStart + reverseBody;
    const shape = shapeStart + shapeBody + shapeEnd;
    const section = sectionStart + '    // other probes\n' + reverse + reverseWrapEnd + ' cup-boundaries ...\n' + shape + extra;
    const contextText = contextStart + context + '\n';
    const source = ['// head\n', helperSeams.join('\n'), '\n', section, sectionEnd, '// tail\n', contextText, contextEnd, ' rest\n'].join('');
    return { source, expected: { section: sha(section), reverseWrap: sha(reverse + reverseWrapEnd.slice(0, 0)), shape: sha(shape), context: sha(contextText) } };
}
const spanDigest = (source, start, end) => { const a = source.indexOf(start), b = source.indexOf(end, a); return sha(source.slice(a, b)); };

test('derivation appends the exports and leaves every upstream byte untouched', () => {
    const { source, expected } = synthetic();
    // The reverse-wrap digest covers the registration up to its end marker.
    const reverseText = source.slice(source.indexOf(reverseWrapStart), source.indexOf(reverseWrapEnd));
    const derived = adaptCursorQualCollector(source, { ...expected, reverseWrap: sha(reverseText) });
    assert.ok(derived.startsWith(source));
    assert.match(derived.slice(source.length), /export const ntildeCursorAdapter/);
    for (const name of ['ntildeCursorProbe', 'ntildeCursorRepair', 'ntildeRunCursorTermless', 'ntildeRunCursorApp']) assert.ok(derived.includes(`export ${name === 'ntildeRunCursorApp' ? 'async ' : ''}function ${name}(`), name);
    assert.ok(derived.includes(JSON.stringify(cursorQualAdapterId)) && derived.includes(JSON.stringify(cursorGeometryRepairId)));
    assert.deepEqual([...cursorRepairedProbeIds], ['cursor.reverse-wrap', 'cursor.shape']);
    // The repair is a probe-record spread at run time: the bundle text itself never gains a declaration.
    assert.ok(!derived.slice(0, source.length).includes('termNeedsGeometry'));
    assert.equal(adaptCursorQualCollector(source, { ...expected, reverseWrap: sha(reverseText) }), derived, 'derivation is deterministic');
});

test('derivation refuses any changed, repeated, missing or already-declaring pinned seam', () => {
    const { source, expected } = synthetic();
    const reverseText = source.slice(source.indexOf(reverseWrapStart), source.indexOf(reverseWrapEnd));
    const good = { ...expected, reverseWrap: sha(reverseText) };
    assert.doesNotThrow(() => adaptCursorQualCollector(source, good));
    for (const [name, change] of [['digest of the section', { section: '0'.repeat(64) }], ['digest of reverse-wrap', { reverseWrap: '0'.repeat(64) }], ['digest of shape', { shape: '0'.repeat(64) }], ['digest of the context', { context: '0'.repeat(64) }]]) {
        assert.throws(() => adaptCursorQualCollector(source, { ...good, ...change }), /changed; refusing derivation/, name);
    }
    assert.throws(() => adaptCursorQualCollector(source.replace(helperSeams[0], 'x')), /helper seam changed/);
    assert.throws(() => adaptCursorQualCollector(source + helperSeams[1]), /helper seam changed/);
    assert.throws(() => adaptCursorQualCollector(source.replace(sectionStart, ''), good), /cursor section seam changed/);
    assert.throws(() => adaptCursorQualCollector(source + sectionStart, good), /cursor section seam changed/);
    assert.throws(() => adaptCursorQualCollector(source.replace(sectionEnd, ''), good), /cursor section seam changed/);
    assert.throws(() => adaptCursorQualCollector(source.replace(contextStart, 'function other({'), good), /terminal context seam changed/);
    assert.throws(() => adaptCursorQualCollector(source.replace(contextEnd, 'function other('), good), /terminal context seam changed/);
    // A source that already declares geometry on a repaired probe is a different (upstream-fixed) bundle: the repair must not double-apply.
    for (const options of [{ reverseBody: ' termNeedsGeometry: true ' }, { shapeBody: ' termNeedsGeometry: true ' }]) {
        const declared = synthetic(options), text = declared.source.slice(declared.source.indexOf(reverseWrapStart), declared.source.indexOf(reverseWrapEnd));
        assert.throws(() => adaptCursorQualCollector(declared.source, { ...declared.expected, reverseWrap: sha(text) }), /already declares geometry; refusing derivation/);
    }
    assert.equal(pinnedCursorSection(source), source.slice(source.indexOf(sectionStart), source.indexOf(sectionEnd)));
    assert.throws(() => pinnedCursorSection('nothing'), /cursor section seam changed/);
});

test('the pinned digests are well-formed and distinct', () => {
    const all = [pinnedCursorSectionSha256, pinnedReverseWrapSha256, pinnedShapeSha256, pinnedTermContextSha256, ...Object.values(pinnedCallbackDigests).flatMap(entry => Object.values(entry))];
    assert.ok(all.every(value => /^[a-f\d]{64}$/.test(value)));
    assert.equal(new Set(all).size, all.length);
    assert.equal(spanDigest('abc', 'a', 'c'), sha('ab'));
});

// The optional, cache-backed proof: the real pinned bundle must satisfy every digest, and the derived extension must reproduce the stock failure.
const bundle = fileURLToPath(new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url));
const pinnedBundleSha256 = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
const haveBundle = existsSync(bundle) && sha(readFileSync(bundle)) === pinnedBundleSha256;

test('the real pinned bundle satisfies the section, reverse-wrap, shape and terminal-context digests', { skip: haveBundle ? false : 'pinned collector cache is not prepared (artifacts/terminfo/tool)' }, () => {
    const text = readFileSync(bundle, 'utf8');
    assert.equal(sha(pinnedCursorSection(text)), pinnedCursorSectionSha256);
    const section = pinnedCursorSection(text);
    assert.equal(sha(section.slice(section.indexOf(reverseWrapStart), section.indexOf(reverseWrapEnd))), pinnedReverseWrapSha256);
    assert.equal(sha(section.slice(section.indexOf(shapeStart), section.indexOf(shapeEnd) + shapeEnd.length)), pinnedShapeSha256);
    assert.equal(sha(text.slice(text.indexOf(contextStart), text.indexOf(contextEnd))), pinnedTermContextSha256);
    assert.equal((section.match(/termNeedsGeometry/g) ?? []).length, 8, 'only the other probes declare geometry');
});

test('the derived extension over the real chain reproduces the original failures and runs the geometry-repaired callbacks', { skip: haveBundle ? false : 'pinned collector cache is not prepared (artifacts/terminfo/tool)' }, async () => {
    const t = file => import(pathToFileURL(fileURLToPath(new URL(file, import.meta.url))).href);
    const { adaptCollector } = await t('./collector.mjs');
    const chain = [['./grading-collector.mjs', 'adaptGradingCollector'], ['./sgr-colors-grading-collector.mjs', 'adaptSgrColorsCollector'], ['./sgr-rgb-grading-collector.mjs', 'adaptSgrRgbCollector'],
        ['./sgr-attrs-grading-collector.mjs', 'adaptSgrAttrsCollector'], ['./sgr-palette-grading-collector.mjs', 'adaptSgrPaletteCollector'], ['./sgr-truecolor-grading-collector.mjs', 'adaptSgrTruecolorCollector'],
        ['./sgr-underline-grading-collector.mjs', 'adaptSgrUnderlineCollector'], ['./sgr-underline-color-grading-collector.mjs', 'adaptSgrUnderlineColorCollector'], ['./sgr-reset-grading-collector.mjs', 'adaptSgrResetCollector'],
        ['./sgr-closure-grading-collector.mjs', 'adaptSgrClosureCollector'], ['./erase-line-grading-collector.mjs', 'adaptEraseLineCollector'], ['./char-edit-grading-collector.mjs', 'adaptCharEditCollector'],
        ['./erase-display-grading-collector.mjs', 'adaptEraseDisplayCollector'], ['./line-edit-grading-collector.mjs', 'adaptLineEditCollector'], ['./charsets-grading-collector.mjs', 'adaptCharsetsCollector'],
        ['./scrollback-grading-collector.mjs', 'adaptScrollbackCollector'], ['./saved-reset-grading-collector.mjs', 'adaptSavedResetCollector'],
        ['./erase-attrs-region-grading-collector.mjs', 'adaptEraseAttrsRegionCollector'],
        ['./attr-rect-grading-collector.mjs', 'adaptAttrRectCollector']];
    let source = adaptCollector(readFileSync(bundle, 'utf8'), fileURLToPath(new URL('./ownership.mjs', import.meta.url)));
    for (const [file, name] of chain) source = (await t(file))[name](source);
    source = adaptCursorQualCollector(source);
    const target = join(bundle, '..', `ntilde-cursor-test-${sha(source).slice(0, 12)}.mjs`);
    writeFileSync(target, source);
    try {
        const module = await import(pathToFileURL(target).href);
        for (const id of cursorRepairedProbeIds) {
            const repair = module.ntildeCursorRepair(id);
            assert.deepEqual(repair.originalKeys, ['id', 'termless', 'term']);
            assert.deepEqual(repair.repairedKeys, ['id', 'termless', 'term', 'termNeedsGeometry']);
            assert.equal(repair.originalTermNeedsGeometry, null); assert.equal(repair.repairedTermNeedsGeometry, true);
            assert.ok(repair.sameTerm && repair.sameTermless);
            assert.equal(sha(repair.termlessSource), pinnedCallbackDigests[id].termlessSha256);
            assert.equal(sha(repair.termSource), pinnedCallbackDigests[id].termSha256);
        }
        assert.throws(() => module.ntildeCursorProbe('cursor.hide', true), /Only an undeclared reverse-wrap or shape record can be repaired/);
        assert.throws(() => module.ntildeCursorProbe('cursor.nope', false), /Unknown pinned probe/);
        const writes = [];
        const backend = { write: text => writes.push(text), queryCursorPosition: () => { throw new Error('queried'); }, capture: frame => ({ role: frame.role, label: frame.label, ref: `sha256:${'a'.repeat(64)}` }) };
        const original = await module.ntildeRunCursorApp('cursor.reverse-wrap', backend, { repaired: false, geometry: { rows: 40, cols: 140 } });
        assert.equal(original.threw.name, 'UndeclaredTerminalGeometry'); assert.deepEqual(original.writes, []); assert.deepEqual(writes, []);
        const shape = await module.ntildeRunCursorApp('cursor.shape', backend, { repaired: false, geometry: { rows: 40, cols: 140 } });
        assert.equal(shape.threw.name, 'UndeclaredTerminalGeometry'); assert.deepEqual(shape.writes, ['\x1b[5 q', '\x1b[0 q']);
        const repaired = await module.ntildeRunCursorApp('cursor.shape', backend, { repaired: true, geometry: { rows: 40, cols: 140 } });
        assert.equal(repaired.threw, undefined); assert.equal(repaired.observation.outcome, 'inconclusive'); assert.equal(repaired.observation.evidence, 'pixels');
        assert.deepEqual(JSON.parse(repaired.response), { rows: 40, cols: 140, cursorCell: null, detail: 'DECSCUSR shape fixture does not establish the cursor cell; only measured geometry and roles', control: 'Default cursor shape', target: 'Bar cursor shape' });
        const absent = await module.ntildeRunCursorApp('cursor.reverse-wrap', { write() {}, queryCursorPosition: () => null }, { repaired: true, geometry: { rows: 40, cols: 140 } });
        assert.equal(absent.observation.reason, 'no-response');
        const termless = module.ntildeRunCursorTermless('cursor.hide', { getCursor: () => ({ visible: null }), feed() {} });
        assert.equal(termless.observation.outcome, 'inconclusive');
    } finally { rmSync(target, { force: true }); }
});
