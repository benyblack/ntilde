// Expose the pinned IRM probe without rewriting its termless or terminal path.
// The source policy and the preceding adapter remain independently bound by the
// owned runner and receipt; this local seam guard prevents fixture substitution.
import { insertReplacePinnedSeam } from './insert-replace-pinned.mjs';
import { charEditSeams } from './char-edit-grading-collector.mjs';

export const insertReplaceAdapterId = 'ntilde-owned-insert-replace-grading-v1';
const required = Object.freeze([
    insertReplacePinnedSeam,
    'export const ntildeLrMarginsAdapter = Object.freeze({',
    'export function ntildeRunLrMarginsTermless(probeId, ctx) {',
    ...charEditSeams.filter(seam => seam.startsWith('function isBlank(') || seam.startsWith('function parserStateResult(')),
]);

/** Append an export only after verifying the pinned paths and preceding layer. */
export function adaptInsertReplaceCollector(upstream) {
    const source = String(upstream);
    for (const seam of required) {
        if (source.split(seam).length !== 2) throw new Error('Pinned insert/replace probe seam changed; refusing derivation.');
    }
    return source + `\n
export const ntildeInsertReplaceAdapter = Object.freeze({
  id: ${JSON.stringify(insertReplaceAdapterId)},
  probeIds: ["modes.insert-replace"]
});
export function ntildeRunInsertReplaceTermless(probeId, ctx) {
  if (probeId !== "modes.insert-replace") throw new Error("Unknown pinned probe " + probeId);
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.termless !== "function") throw new Error("Missing pinned probe " + probeId);
  return { probeId, ...probe.termless(ctx) };
}
export async function ntildeRunInsertReplaceTerminal(probeId, ctx) {
  if (probeId !== "modes.insert-replace") throw new Error("Unknown pinned probe " + probeId);
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || typeof probe.term !== "function") throw new Error("Missing pinned terminal probe " + probeId);
  return { probeId, ...await probe.term(ctx) };
}
`;
}
