// A portable stand-in for the derived cursor extension's live exports, built on the grading module's ports. The real bundle is exercised by the
// optional cache-backed tests; this double lets the worker driver and the parent replay run without the pinned collector installed.
import { createHash } from 'node:crypto';
import { termlessPorts, appPorts, pinnedCallbackDigests } from '../cursor-qual-grading.mjs';
import { cursorQualAdapterId, cursorGeometryRepairId, cursorRepairedProbeIds } from '../cursor-qual-grading-collector.mjs';

export const syntheticCursorModule = Object.freeze({
    ntildeCursorAdapter: Object.freeze({ id: cursorQualAdapterId, probeIds: ['cursor.cup-scroll-region', 'cursor.hide', 'cursor.shape', 'cursor.reverse-wrap'],
        geometryRepairId: cursorGeometryRepairId, repairedProbeIds: [...cursorRepairedProbeIds] }),
    ntildeRunCursorTermless: (probeId, ctx) => ({ probeId, ...termlessPorts[probeId](ctx) }),
    async ntildeRunCursorApp(probeId, backend, { repaired, geometry }) {
        const writes = [];
        const geometryDeclared = repaired || !cursorRepairedProbeIds.includes(probeId) || probeId === 'cursor.hide';
        const ctx = { get rows() { return guard().rows; }, get cols() { return guard().cols; }, write(text) { writes.push(text); backend.write(text); },
            queryCursorPosition: () => backend.queryCursorPosition(), capture: backend.capture && (checkpoint => backend.capture(checkpoint)) };
        const guard = () => {
            if (!geometryDeclared) {
                const error = new Error(`App callback ${probeId} accessed undeclared or unavailable terminal geometry`);
                error.name = 'UndeclaredTerminalGeometry';
                throw error;
            }
            return geometry;
        };
        try { return { probeId, ...appPorts[probeId](ctx), writes }; }
        catch (error) { return { probeId, threw: { name: error.name, message: error.message }, writes }; }
    },
    ntildeCursorRepair(probeId) {
        const digests = pinnedCallbackDigests[probeId];
        // The stand-in cannot read the callback sources, so it reports the pinned digests directly.
        return { probeId, originalKeys: ['id', 'termless', 'term'], repairedKeys: ['id', 'termless', 'term', 'termNeedsGeometry'], originalTermNeedsGeometry: null,
            repairedTermNeedsGeometry: true, sameTermless: true, sameTerm: true, termlessSha256: digests.termlessSha256, termSha256: digests.termSha256 };
    },
});
export const sha256 = text => createHash('sha256').update(text).digest('hex');
