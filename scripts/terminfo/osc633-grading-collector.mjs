// Append access to the pinned OSC 633 lifecycle probes without changing their code or verdicts.
// The preceding OSC 133 layer already pins the shared promptConsumptionResult helper.
import { osc133Seams } from './osc133-grading-collector.mjs';

export const osc633AdapterId = 'ntilde-owned-osc633-grading-v1';
export const osc633CaseIds = Object.freeze(['extensions.osc633-a', 'extensions.osc633-b', 'extensions.osc633-c', 'extensions.osc633-d']);

// These registrations have the same structure as the independently pinned OSC 133
// registrations, with a distinct protocol number and IDs. Pin the helper as well.
export const osc633Seams = Object.freeze([
    ...osc133Seams.slice(0, 4).map(seam => seam.replaceAll('osc133-', 'osc633-').replaceAll(']133;', ']633;')),
    osc133Seams[4],
    'export const ntildeOsc133Adapter = Object.freeze({',
    'export function ntildeRunOsc133Termless(probeId, ctx) {',
    'export async function ntildeRunOsc133Terminal(probeId, ctx) {',
]);

export function adaptOsc633Collector(osc133Source) {
    const source = String(osc133Source);
    for (const seam of osc633Seams) {
        if (source.split(seam).length !== 2) throw new Error('Pinned OSC 633 probe seam changed; refusing derivation.');
    }
    return source + `

// Local OSC 633 grading extension ${JSON.stringify(osc633AdapterId)}.
export const ntildeOsc633Adapter = Object.freeze({ id: ${JSON.stringify(osc633AdapterId)}, probeIds: ${JSON.stringify([...osc633CaseIds])} });
export function ntildeRunOsc633Termless(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || !ntildeOsc633Adapter.probeIds.includes(probeId) || typeof probe.termless !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = probe.termless(ctx);
  return { probeId, ...result };
}
export async function ntildeRunOsc633Terminal(probeId, ctx) {
  init_src();
  const probe = ALL_PROBES.find(item => item.id === probeId);
  if (!probe || !ntildeOsc633Adapter.probeIds.includes(probeId) || typeof probe.term !== "function") throw new Error("Unknown pinned probe " + probeId);
  const result = await probe.term(ctx);
  return { probeId, ...result };
}
`;
}
