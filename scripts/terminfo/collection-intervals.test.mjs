import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { overlappingCollections, readCollectionIntervals } from './collection-intervals.mjs';

function run(root, name, { startedAt, cleanedUpAt, executable = '/x/Ntilde', failed = false, context = true }) {
    const dir = join(root, name); mkdirSync(dir, { recursive: true });
    if (context) writeFileSync(join(dir, 'context.json'), JSON.stringify({ startedAt, runId: name.slice(6).padEnd(32, '0'), executable, sourceSnapshotPolicy: 'p' }));
    if (cleanedUpAt) writeFileSync(join(dir, 'ownership-proof.json'), JSON.stringify({ cleanedUpAt }));
    if (failed) writeFileSync(join(dir, 'failure.json'), '{}');
}

test('recorded intervals overlap only when the later start precedes the earlier cleanup; smoke runs never count', () => {
    const root = mkdtempSync(join(tmpdir(), 'nt-intervals-'));
    try {
        run(root, 'owned-aaa', { startedAt: '2026-11-13T14:15:29.143Z', cleanedUpAt: '2026-11-13T14:17:34.830Z' });
        run(root, 'owned-bbb', { startedAt: '2026-11-13T14:15:36.872Z', cleanedUpAt: '2026-11-13T14:16:43.724Z' });
        run(root, 'owned-ccc', { startedAt: '2026-11-13T14:17:34.830Z', cleanedUpAt: '2026-11-13T14:19:00.000Z' }); // starts exactly at cleanup: no overlap
        run(root, 'owned-ddd', { startedAt: '2026-11-13T14:16:00.000Z', cleanedUpAt: '2026-11-13T14:16:01.000Z', executable: '/usr/bin/true' });
        run(root, 'owned-eee', { startedAt: '2026-11-13T14:30:00.000Z' }); // never recorded a cleanup
        run(root, 'owned-fff', { startedAt: '2026-11-13T14:40:00.000Z', cleanedUpAt: '2026-11-13T14:41:00.000Z', failed: true });
        run(root, 'owned-ggg', { startedAt: 'not a time', cleanedUpAt: '2026-11-13T14:41:00.000Z' });
        run(root, 'owned-hhh', { context: false });
        mkdirSync(join(root, 'tool')); writeFileSync(join(root, 'owned-stray'), 'not a directory');
        const rows = readCollectionIntervals(root, 'unit');
        assert.deepEqual(rows.map(row => row.directory), ['owned-aaa', 'owned-bbb', 'owned-ddd', 'owned-ccc', 'owned-eee', 'owned-fff']);
        assert.equal(rows.find(row => row.directory === 'owned-ddd').gui, false);
        assert.equal(rows.find(row => row.directory === 'owned-fff').failed, true);
        assert.equal(rows.find(row => row.directory === 'owned-eee').end, null);
        const pairs = overlappingCollections(rows);
        assert.deepEqual(pairs.map(pair => [pair.first.directory, pair.second.directory]), [['owned-aaa', 'owned-bbb'], ['owned-eee', 'owned-fff']]);
        assert.equal(Math.round(pairs[0].overlapMs), 66852); assert.equal(pairs[0].openEnded, false);
        assert.equal(pairs[1].openEnded, true, 'a run with no recorded cleanup is treated as still running');
        // An open-ended run overlaps everything that starts after it.
        run(root, 'owned-iii', { startedAt: '2026-11-13T14:45:00.000Z', cleanedUpAt: '2026-11-13T14:46:00.000Z' });
        const open = overlappingCollections(readCollectionIntervals(root)).filter(pair => pair.openEnded);
        assert.deepEqual(open.map(pair => [pair.first.directory, pair.second.directory]), [['owned-eee', 'owned-fff'], ['owned-eee', 'owned-iii']]);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
