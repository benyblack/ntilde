// Explicitly versioned collector extension for the cursor qualification (the four pinned cursor.cup-scroll-region, cursor.hide,
// cursor.shape and cursor.reverse-wrap cases). This is a twenty-first digest-checked derivation layered on top of the attribute-rectangle
// grading extension: the pinned bundle, the owned adapter and every earlier grading extension stay byte-for-byte unchanged, and each graded
// result carries this extension's identity so it can never be presented as stock output.
//
// The extension adds no probe of its own. It exposes the PINNED termless AND app callbacks of the four contracted cases so independently
// observed states, retained CPR replies and controlled captures can be graded by the collector's own criteria, and it adds the explicit
// GEOMETRY REPAIR of cursor.reverse-wrap and cursor.shape:
//
//   The pinned probe record of cursor.reverse-wrap lacks `termNeedsGeometry: true`. Its app callback reads ctx.rows and ctx.cols first, the
//   shared createTermContext getters throw UndeclaredTerminalGeometry for an undeclared probe, and the callback therefore fails before its
//   first terminal byte (the retained stock collector error). The record of cursor.shape lacks it too: its app callback reads ctx.rows and
//   ctx.cols only after its two captures, so the stock run (which supplies no capture adapter) never reaches them, but any run that does
//   capture hits the same error after the captures. The repair changes exactly one piece of metadata per probe: the probe RECORD handed to
//   createTermContext is `{ ...original, termNeedsGeometry: true }`. The termless and app callback bytes are the original function objects,
//   the bundle text is never rewritten and the original (unrepaired) execution stays available and is re-run for every report so the
//   original failure is retained.
//
// Derivation refuses unless the retained attribute-rectangle extension still contains the exact pinned cursor section (every registration and
// helper of terminfo.dev@4.0.0's cursor.ts, verified by one SHA-256 over the verbatim section text), the exact reverse-wrap registration (a
// second SHA-256) and the exact createTermContext source (a third), each exactly once.
import { createHash } from 'node:crypto';
import { cursorQualCaseIds } from './cursor-qual-contracts.mjs';

/** Identity of this twenty-first layered extension. */
export const cursorQualAdapterId = 'ntilde-owned-cursor-qualification-grading-v1';
/** Identity of the geometry-repaired derivation of cursor.reverse-wrap. */
export const cursorGeometryRepairId = 'ntilde-owned-cursor-geometry-repair-v1';

/** The verbatim pinned section markers and digests (terminfo.dev@4.0.0, bundle aa5f2bf9…d1f6). */
const sectionStart = '// ../probe-defs/src/cursor.ts\n';
const sectionEnd = '// ../probe-defs/src/text.ts\n';
export const pinnedCursorSectionSha256 = 'bcac625f473395750bb333b5ba55a936b2abc8cefb177bdb8cc0af6ec5fbfbb9';
const reverseWrapStart = '    probe("cursor.reverse-wrap"';
const reverseWrapEnd = '    {\n      ...probe("cursor.cup-boundaries"';
export const pinnedReverseWrapSha256 = 'ba1dd51dce34f86b4b8870118373b37eac27e3b89e444478110cda15838316da';
const shapeStart = '    probe("cursor.shape"';
const shapeEnd = '        ctx.write("\\x1B[0 q");\n      }\n    }),\n';
export const pinnedShapeSha256 = '02901418b4b75420eb90c5f60dc84e11763158ada87484ef8056e43f4138aba3';
/** The probes whose record lacks the geometry declaration and which the repair covers. */
export const cursorRepairedProbeIds = Object.freeze(['cursor.reverse-wrap', 'cursor.shape']);
const contextStart = 'function createTermContext({';
const contextEnd = 'function selectAppProbes(ids) {';
export const pinnedTermContextSha256 = 'c24dac9fe125e1e00989a21868da6915615a86432ebe678217ff4e6760ad20f2';

/** The retained previous layer's exports, each present exactly once. */
const helperSeams = Object.freeze([
    'export const ntildeAttrRectAdapter = Object.freeze({',
    'export function ntildeRunAttrRectTermless(probeId, ctx) {',
]);

/** The pinned seams, exposed so portable tests can build a synthetic layered source. */
export const cursorQualSeams = Object.freeze({ sectionStart, sectionEnd, reverseWrapStart, reverseWrapEnd, shapeStart, shapeEnd, contextStart, contextEnd, helperSeams });

const digestOf = text => createHash('sha256').update(text).digest('hex');

/** The text between two seams, which must each occur exactly once, or a refusal. */
function pinnedSpan(source, start, end, label, from = 0) {
    const begin = source.indexOf(start, from);
    if (begin < 0 || source.indexOf(start, begin + 1) !== -1) throw new Error(`Pinned ${label} seam changed; refusing derivation.`);
    const stop = source.indexOf(end, begin);
    if (stop < 0 || source.indexOf(end, stop + 1) !== -1) throw new Error(`Pinned ${label} seam changed; refusing derivation.`);
    return source.slice(begin, stop);
}

/** The verbatim pinned cursor section of a source, or a refusal when it is absent, repeated or changed. */
export const pinnedCursorSection = source => pinnedSpan(String(source), sectionStart, sectionEnd, 'cursor section');

/** Derive the cursor grading extension from the attribute-rectangle grading extension. Only portable tests pass different expected digests, to
 * exercise the refusals on a synthetic source; every production caller uses the pinned digests. */
export function adaptCursorQualCollector(attrRectSource, expected = {}) {
    const source = String(attrRectSource);
    const digests = { section: pinnedCursorSectionSha256, reverseWrap: pinnedReverseWrapSha256, shape: pinnedShapeSha256, context: pinnedTermContextSha256, ...expected };
    for (const seam of helperSeams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned cursor helper seam changed; refusing derivation.');
    }
    const section = pinnedCursorSection(source);
    if (digestOf(section) !== digests.section) throw new Error('Pinned cursor probes changed; refusing derivation.');
    // The reverse-wrap registration inside the verified section: its callbacks are the bytes the repaired record keeps.
    const registration = pinnedSpan(section, reverseWrapStart, reverseWrapEnd, 'reverse-wrap registration');
    if (digestOf(registration) !== digests.reverseWrap) throw new Error('Pinned reverse-wrap probe changed; refusing derivation.');
    if (registration.includes('termNeedsGeometry')) throw new Error('Pinned reverse-wrap probe already declares geometry; refusing derivation.');
    const shape = pinnedSpan(section, shapeStart, shapeEnd, 'shape registration');
    const shapeText = shape + shapeEnd;
    if (digestOf(shapeText) !== digests.shape) throw new Error('Pinned shape probe changed; refusing derivation.');
    if (shapeText.includes('termNeedsGeometry')) throw new Error('Pinned shape probe already declares geometry; refusing derivation.');
    const context = pinnedSpan(source, contextStart, contextEnd, 'terminal context');
    if (digestOf(context) !== digests.context) throw new Error('Pinned terminal context changed; refusing derivation.');
    // The appended block only adds exports; nothing upstream is rewritten.
    return source + `

// Local cursor grading extension ${JSON.stringify(cursorQualAdapterId)}.
// Derived from the attribute-rectangle grading extension; probe definitions, policy
// gates and stock grading remain intact. The only repair is the geometry declaration
// of cursor.reverse-wrap, applied to the probe RECORD at run time (${JSON.stringify(cursorGeometryRepairId)}).
export const ntildeCursorAdapter = Object.freeze({
  id: ${JSON.stringify(cursorQualAdapterId)},
  probeIds: ${JSON.stringify([...cursorQualCaseIds])},
  geometryRepairId: ${JSON.stringify(cursorGeometryRepairId)},
  repairedProbeIds: ${JSON.stringify([...cursorRepairedProbeIds])}
});
/** The pinned probe record, or its geometry-repaired copy (cursor.reverse-wrap and cursor.shape only; the callbacks are the original functions). */
export function ntildeCursorProbe(probeId, repaired) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function" || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  if (!repaired) return probe;
  if (!${JSON.stringify([...cursorRepairedProbeIds])}.includes(probeId) || Object.hasOwn(probe, "termNeedsGeometry")) throw new Error("Only an undeclared reverse-wrap or shape record can be repaired");
  return { ...probe, termNeedsGeometry: true };
}
/** The exact metadata change and the verbatim callback sources the repaired record keeps. */
export function ntildeCursorRepair(probeId) {
  const original = ntildeCursorProbe(probeId, false);
  const repaired = ntildeCursorProbe(probeId, true);
  return {
    probeId: original.id,
    originalKeys: Object.keys(original),
    repairedKeys: Object.keys(repaired),
    originalTermNeedsGeometry: Object.hasOwn(original, "termNeedsGeometry") ? original.termNeedsGeometry : null,
    repairedTermNeedsGeometry: repaired.termNeedsGeometry,
    sameTermless: original.termless === repaired.termless,
    sameTerm: original.term === repaired.term,
    termlessSource: String(original.termless),
    termSource: String(original.term)
  };
}
/** Execute one pinned termless probe over caller-served states. */
export function ntildeRunCursorTermless(probeId, ctx) {
  const probe = ntildeCursorProbe(probeId, false);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
/** Execute one pinned app callback over a caller-served backend (write, queryCursorPosition, capture) through the real createTermContext, so the
 * geometry declaration is enforced exactly as the stock collector enforces it. A thrown error is the retained result, with the bytes sent first. */
export async function ntildeRunCursorApp(probeId, backend, options) {
  const probe = ntildeCursorProbe(probeId, options.repaired === true);
  const writes = [];
  const context = createTermContext({ out: { write(text) { backend.write(text); } }, writes, events: [], probe, geometry: options.geometry });
  context.queryCursorPosition = async () => backend.queryCursorPosition();
  if (backend.capture) context.capture = async checkpoint => backend.capture(checkpoint);
  try {
    const result = await probe.term(context);
    return { probeId, ...result, writes };
  } catch (error) {
    return { probeId, threw: { name: error instanceof Error ? error.name : "Error", message: error instanceof Error ? error.message : String(error) }, writes };
  }
}
`;
}
