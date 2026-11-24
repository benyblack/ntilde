import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { modernRoot, serializeModern } from './modern-results.mjs';
import { ids } from './modern-x11-mouse.mjs';
import { buildModernX11MousePublication, modernX11MouseOutputPaths, readableModernX11Mouse,
    verifyX11MouseRun, x11MousePublication } from './modern-x11-mouse-results.mjs';

test('the retained X11 run rederives exactly three changed frozen App rows', () => {
    const report = buildModernX11MousePublication();
    const previous = JSON.parse(readFileSync(join(modernRoot, report.predecessor.publication, 'report.json')));
    const paths = modernX11MouseOutputPaths(modernRoot);
    assert.equal(readFileSync(paths.json, 'utf8').replaceAll('\r\n', '\n'), serializeModern(report));
    assert.equal(readFileSync(paths.markdown, 'utf8').replaceAll('\r\n', '\n'), readableModernX11Mouse(report));
    assert.deepEqual(report.totals, { pass: 62, fail: 0, inconclusive: 7 });
    assert.equal(report.requirementsCount, 69);
    assert.deepEqual(report.predecessor.totals, previous.totals);
    const changed = report.rows.filter((row, index) => JSON.stringify(row) !== JSON.stringify(previous.rows[index]));
    assert.deepEqual(new Set(changed.map(row => row.id)), new Set(ids));
    for (const row of changed) {
        const before = previous.rows.find(item => item.id === row.id);
        assert.equal(row.requirement, before.requirement);
        assert.equal(row.stockVerdict, before.stockVerdict);
        assert.equal(row.conclusion, 'pass');
    }
});

test('archive replay verifies display ancestry, PTY ownership and actual cleanup', () => {
    const directory = join(modernRoot, x11MousePublication);
    const scratch = mkdtempSync(join(tmpdir(), 'ntilde-x11-refusal-'));
    try {
        const manifest = JSON.parse(readFileSync(join(directory, 'manifest.json')));
        extractOwnedArchive(directory, manifest, scratch, join(directory, 'owned-run.zip'));
        const observation = join(scratch, 'observation.json');
        const raw = readFileSync(observation);
        const rejectMutation = (mutate, message) => {
            const record = JSON.parse(raw);
            mutate(record);
            writeFileSync(observation, JSON.stringify(record));
            assert.throws(() => verifyX11MouseRun(scratch), message);
        };
        assert.equal(verifyX11MouseRun(scratch).results.length, 3);
        rejectMutation(record => { record.display.server.ppid = record.lineage.child.pid; }, /owned XTEST display/);
        rejectMutation(record => { record.lineage.child.pty.descriptors[1].device.ino = '0'; }, /wrong direct PTY identity/);
        rejectMutation(record => { record.cleanup.socketGone = false; }, /unverified X11 App, worker or display cleanup/);
        writeFileSync(observation, raw);
        const lease = join(scratch, 'desktop-lease.jsonl');
        const audit = readFileSync(lease, 'utf8');
        writeFileSync(lease, audit.trimEnd().split('\n').slice(0, -1).join('\n'));
        assert.throws(() => verifyX11MouseRun(scratch), /desktop lease cleanup or release/);
        writeFileSync(lease, audit);
        const fixture = join(scratch, 'fixture-cleanup.json');
        const cleanup = JSON.parse(readFileSync(fixture));
        writeFileSync(fixture, JSON.stringify({ ...cleanup, removedAt: '1970-01-01T00:00:00.000Z' }));
        assert.throws(() => verifyX11MouseRun(scratch), /desktop lease cleanup or release/);
    } finally {
        rmSync(scratch, { recursive: true, force: true });
    }
});
