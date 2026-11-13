#!/usr/bin/env node
// Offline: list owned-collection intervals under one or more artifacts/terminfo directories and the GUI overlaps between them.
import { mergeCollectionRows, overlappingCollections, readCollectionIntervals } from './terminfo/collection-intervals.mjs';

const roots = process.argv.slice(2);
if (roots.length === 0 || roots.some(root => root.startsWith('-'))) {
    console.error('Usage: node scripts/terminfo-collection-overlap.mjs <path/to/artifacts/terminfo> [...]');
    process.exitCode = 2;
} else {
    const rows = mergeCollectionRows(roots.flatMap(root => readCollectionIntervals(root)));
    const iso = value => (value === null ? 'unrecorded' : new Date(value).toISOString());
    console.log(JSON.stringify({ intervals: rows.map(row => ({ ...row, start: iso(row.start), end: iso(row.end) })),
        overlaps: overlappingCollections(rows).map(pair => ({ first: pair.first.directory + '@' + pair.first.location, second: pair.second.directory + '@' + pair.second.location,
            overlapSeconds: Number.isFinite(pair.overlapMs) ? Math.round(pair.overlapMs / 100) / 10 : null, openEnded: pair.openEnded })) }, null, 2));
}
