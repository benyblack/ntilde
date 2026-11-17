import { createHash } from 'node:crypto';
import { cellSizeIds } from './cell-size-contracts.mjs';

export const cellSizeAdapterId = 'ntilde-owned-osc-cell-size-grading-v1';
const hash = text => createHash('sha256').update(text).digest('hex');
// Filled from the inspected frozen bundle; every helper and exact registration is pinned.
export const cellSizeSeams = Object.freeze([
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
        "    oscSimpleQueryProbe(\"extensions.osc1337-cellsize\"",
        "\n    oscSimpleQueryProbe(\"extensions.osc1337-capabilities\"",
        "179e0a8d08a2ce0e8d09193130141fd1b8eec8adb1260a2184730df0d26845ce"
    ],
    [
        "    oscSimpleQueryProbe(\"extensions.osc776-cell-size\"",
        "\n    {",
        "ff06437b119bb8a4a1966e85b83ad8ceff26d94f383db45f0bf213d5969d82b6"
    ]
]);

export function adaptCellSizeCollector(source) {
    source = String(source);
    for (const [start, end, digest] of cellSizeSeams) {
        if (source.split(start).length !== 2) throw new Error('Cell-size pinned seam changed.');
        const at = source.indexOf(start), stop = source.indexOf(end, at + start.length);
        if (stop < 0 || hash(source.slice(at, stop)) !== digest) throw new Error('Cell-size pinned probe bytes changed.');
    }
    return source + `
export const ntildeCellSizeAdapter = Object.freeze({id:${JSON.stringify(cellSizeAdapterId)},probeIds:${JSON.stringify(cellSizeIds)}});
export function ntildeRunCellSizeTermless(id, ctx) {
  init_src();
  if (!ntildeCellSizeAdapter.probeIds.includes(id)) throw new Error("Unknown cell-size probe");
  return {probeId:id,...ALL_PROBES.find(p=>p.id===id).termless(ctx)};
}
export async function ntildeRunCellSizeTerminal(id, ctx) {
  init_src();
  if (!ntildeCellSizeAdapter.probeIds.includes(id)) throw new Error("Unknown cell-size probe");
  return {probeId:id,...await ALL_PROBES.find(p=>p.id===id).term(ctx)};
}
`;
}
