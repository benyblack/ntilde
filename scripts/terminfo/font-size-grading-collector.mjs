import { createHash } from 'node:crypto';
import { fontSizeIds } from './font-size-contracts.mjs';

export const fontSizeAdapterId = 'ntilde-owned-osc-font-size-grading-v1';
const hash = text => createHash('sha256').update(text).digest('hex');
// Filled from the inspected frozen bundle; every helper and exact registration is pinned.
export const fontSizeSeams = Object.freeze([
    ["function unmeasuredStateEffect(", "\nfunction sixelDa1Result(", "9e43e301a341194650b0a9cfb5064e484f6268f028b7933ebb381ca7de1ba981"],
    [
        "function oscReplyResult(",
        "\nfunction unverifiedEffect(",
        "933f7567f2df14e9f018eda083fa86931d06c64adef3b426605b66272de7218d"
    ],
    [
        "function oscSimpleQueryProbe(",
        "\nfunction colorResetProbe(",
        "f6dd2c1fc55afdcde8680bf18f956f9a111635eb77b5762bee6099e53e5395a9"
    ],
    [
        "    oscSimpleQueryProbe(\"extensions.osc7770-font-size\"",
        "\n    oscSimpleQueryProbe(\"extensions.osc7777-font-window-size\"",
        "48383faabcf9be173c76f1e677f6ee9618f5d4747f2d0a978947b97b5abff6f6"
    ],
    [
        "    oscSimpleQueryProbe(\"extensions.osc7777-font-window-size\"",
        "\n    probe(",
        "e25c36313b487c5407f91db82e5e64ebf5b7eb45cfd3cc03cbf63733ce52fb1a"
    ],
    [
        "    probe(\"extensions.osc710-font-normal\"",
        "\n    probe(",
        "10cf4a1f5ac71042aa0c6ada905a79633cdca0c6ad7b88b398b881bbde4cd535"
    ]
]);

export function adaptFontSizeCollector(source) {
    source = String(source);
    for (const [start, end, digest] of fontSizeSeams) {
        if (source.split(start).length !== 2) throw new Error('Font-size pinned seam changed.');
        const at = source.indexOf(start), stop = source.indexOf(end, at + start.length);
        if (stop < 0 || hash(source.slice(at, stop)) !== digest) throw new Error('Font-size pinned probe bytes changed.');
    }
    return source + `
export function ntildeFontSelectionProbe() { init_src(); return ALL_PROBES.find(p=>p.id==="extensions.osc710-font-normal"); }
export const ntildeFontSizeAdapter = Object.freeze({id:${JSON.stringify(fontSizeAdapterId)},probeIds:${JSON.stringify(fontSizeIds)}});
export function ntildeRunFontSizeTermless(id, ctx) {
  init_src();
  if (!ntildeFontSizeAdapter.probeIds.includes(id)) throw new Error("Unknown font-size probe");
  return {probeId:id,...ALL_PROBES.find(p=>p.id===id).termless(ctx)};
}
export async function ntildeRunFontSizeTerminal(id, ctx) {
  init_src();
  if (!ntildeFontSizeAdapter.probeIds.includes(id)) throw new Error("Unknown font-size probe");
  return {probeId:id,...await ALL_PROBES.find(p=>p.id===id).term(ctx)};
}
`;
}

export const fontSelectionAuditId = 'extensions.osc710-font-normal';
