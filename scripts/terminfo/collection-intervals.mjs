// Offline audit helper: reconstruct owned-collection intervals from retained run directories and report overlaps.
// It reads only context.json and ownership-proof.json (plus failure.json) and never launches, signals or locks anything,
// so it is safe to run while another session owns the desktop. Times are what the runs recorded about themselves; an
// overlap is a direct fact about two recorded intervals, never proof of which side broke exclusion or what it disturbed.
import { existsSync, lstatSync, readFileSync, readdirSync } from 'node:fs';
import { basename, join } from 'node:path';

const readJson = path => { try { return JSON.parse(readFileSync(path, 'utf8')); } catch { return undefined; } };
const instant = value => (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T[\d:.]+Z$/.test(value) ? Date.parse(value) : Number.NaN);

/** One interval per `owned-*` run directory with a recorded start; `end` is the recorded cleanup time or null. */
export function readCollectionIntervals(artifactsTerminfoDir, label = artifactsTerminfoDir) {
    const rows = [];
    for (const entry of readdirSync(artifactsTerminfoDir)) {
        if (!/^owned-[A-Za-z0-9]+$/.test(entry)) continue;
        const dir = join(artifactsTerminfoDir, entry);
        if (!lstatSync(dir).isDirectory()) continue;
        const context = readJson(join(dir, 'context.json'));
        const start = instant(context?.startedAt);
        if (!Number.isFinite(start)) continue;
        const proof = readJson(join(dir, 'ownership-proof.json'));
        const end = instant(proof?.cleanedUpAt);
        const executable = typeof context.executable === 'string' ? basename(context.executable) : '';
        rows.push({
            location: label, directory: entry, runId: String(context.runId ?? ''), start, end: Number.isFinite(end) ? end : null,
            // /usr/bin/true is the lifecycle smoke tests' stand-in: it never opens a GUI window.
            gui: executable !== 'true', failed: existsSync(join(dir, 'failure.json')), policy: String(context.sourceSnapshotPolicy ?? ''),
        });
    }
    return rows.sort((a, b) => a.start - b.start);
}

/** Pairs of GUI collections whose recorded intervals intersect; an interval without a cleanup time is reported open-ended. */
export function overlappingCollections(rows) {
    const gui = rows.filter(row => row.gui), pairs = [];
    for (let i = 0; i < gui.length; i++) {
        for (let j = i + 1; j < gui.length; j++) {
            const [a, b] = gui[i].start <= gui[j].start ? [gui[i], gui[j]] : [gui[j], gui[i]];
            const aEnd = a.end ?? Number.POSITIVE_INFINITY;
            if (b.start < aEnd) pairs.push({ first: a, second: b, overlapMs: Math.min(aEnd, b.end ?? Number.POSITIVE_INFINITY) - b.start, openEnded: a.end === null || b.end === null });
        }
    }
    return pairs;
}
