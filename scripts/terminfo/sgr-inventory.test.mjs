import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { buildSgrInventory, sgrInventorySuites } from './sgr-inventory.mjs';

const published = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-pinned-inventory.json', import.meta.url)));
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);

test('the published inventory is exactly what the versioned contracts derive', () => {
    assert.deepEqual(published, buildSgrInventory());
});

test('all 32 pinned SGR ids are covered, none is unqualified and no selective-reset case is pending', () => {
    assert.equal(published.total, 32);
    assert.equal(published.cases.length, 32);
    assert.deepEqual(published.unqualified, []);
    assert.deepEqual(published.pendingSelectiveReset, []);
    assert.ok(published.cases.every(item => item.status === 'qualified' && item.coverage.length >= 1));
    for (const id of ['sgr.blink', 'sgr.overline', 'sgr.underline.color', 'sgr.selective-reset.underline', 'sgr.selective-reset.inverse']) {
        assert.ok(published.cases.some(item => item.id === id), id);
    }
    assert.match(published.scope, /Not a stock compatibility score/);
});

test('overlapping fixtures and ids are identified without merging identities', () => {
    assert.deepEqual(published.overlaps.idsCoveredBySeveralSuites, ['sgr.bg.standard', 'sgr.fg.standard']);
    assert.deepEqual(published.overlaps.idsSharingFixtureBytes, [['sgr.underline-color-rgb', 'sgr.underline.color']]);
    const ids = published.cases.map(item => item.id);
    assert.equal(new Set(ids).size, 32, 'distinct ids: the shared fixture never merges identities');
    assert.ok(ids.includes('sgr.underline-color-rgb') && ids.includes('sgr.underline.color'));
});

test('every covering suite is a known suite with its publication on disk', () => {
    const known = new Map(sgrInventorySuites.map(item => [item.suite, item.doc]));
    for (const item of published.cases) {
        for (const entry of item.coverage) {
            assert.equal(known.get(entry.suite), entry.doc);
            assert.ok(existsSync(new URL(`../../docs/qa/terminfo/${entry.doc}`, import.meta.url)) || entry.suite === 'sgr-closure', entry.doc);
        }
    }
});

test('the inventory ids equal the pinned bundle registrations when the collector cache is installed', { skip: existsSync(bundleUrl) ? false : 'collector cache not installed' }, () => {
    const source = readFileSync(bundleUrl, 'utf8');
    const start = source.indexOf('  sgrProbes = [');
    const end = source.indexOf('\n  ];', start);
    const block = source.slice(start, end);
    const ids = [...new Set([...block.matchAll(/(?:Probe|probe)\("(sgr\.[^"]+)"/g)].map(match => match[1]))].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    assert.equal(ids.length, 32);
    assert.deepEqual(ids, published.cases.map(item => item.id));
});
