// The /qualification/ page's data must be the pinned publication's, and must refuse anything else:
// a changed, missing or malformed source fails the build instead of showing a number nobody measured.
// The generator lives in site/scripts/qualification-data.mjs; CI runs this file with the other
// terminfo self-tests, so no workflow edit is needed for it.
import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateRawSync } from 'node:zlib';
import {
    QualificationDataError, assertPublishable, buildQualificationData, findNewerCorePublications, layerNames,
    loadPin, methodGroupsFor, readZipEntry, repoRoot,
} from '../../site/scripts/qualification-data.mjs';
import { publication as scriptPublication, loadMeasurement, manifestSha256 } from '../terminfo-core-reverse-wide-publication.mjs';

const pin = loadPin();
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const here = dirname(fileURLToPath(import.meta.url));

/** A scratch copy of just the files the generator reads, so a test can damage them. */
function scratch() {
    const base = mkdtempSync(join(tmpdir(), 'nt-site-qual-'));
    for (const file of ['report.json', 'report.md', 'measurement.json', 'owned-run.zip']) {
        mkdirSync(join(base, pin.publication), { recursive: true });
        cpSync(join(repoRoot, pin.publication, file), join(base, pin.publication, file));
    }
    mkdirSync(join(base, 'docs/qa/terminfo/2026-11-11-core-results'), { recursive: true });
    cpSync(join(repoRoot, pin.requirements), join(base, pin.requirements));
    return base;
}

/** Rewrites one file in the scratch copy and pins the new bytes, so only the check under test can object. */
function damaged(base, relative, change, pinKey) {
    const file = join(base, relative), original = readFileSync(file);
    const next = change(original.toString('utf8'));
    writeFileSync(file, next);
    const changed = structuredClone(pin);
    if (pinKey) changed.sha256[pinKey] = sha(Buffer.from(next));
    return { pin: changed, restore: () => writeFileSync(file, original) };
}
function refuses(change, expected, { file = 'report.json', pinKey = 'report.json', json = true } = {}) {
    const base = scratch();
    try {
        const relative = file.includes('/') ? file : `${pin.publication}/${file}`;
        const { pin: p } = damaged(base, relative, text => {
            if (!json) return change(text);
            const report = JSON.parse(text); change(report); return JSON.stringify(report);
        }, pinKey);
        assert.throws(() => buildQualificationData({ base, pin: p }), error => error instanceof QualificationDataError && expected.test(error.message), String(expected));
    } finally { rmSync(base, { recursive: true, force: true }); }
}

test('the page data is the pinned publication: 80 unique rows, recomputed totals, recorded identity', () => {
    const data = buildQualificationData();
    assert.equal(data.rows.length, pin.expected.rows);
    assert.equal(new Set(data.rows.map(r => r.id)).size, data.rows.length);
    const report = JSON.parse(readFileSync(join(repoRoot, pin.publication, 'report.json'), 'utf8'));
    assert.deepEqual(data.rows.map(r => r.id), report.rows.map(r => r.id));
    for (const outcome of ['pass', 'fail', 'inconclusive']) {
        assert.equal(data.totals[outcome], data.rows.filter(r => r.outcome === outcome).length);
        assert.equal(data.totals[outcome], report.totals[outcome]);
    }
    assert.equal(data.totals.total, data.totals.pass + data.totals.fail + data.totals.inconclusive);
    assert.equal(data.claim, `${data.totals.pass} of ${data.totals.total} frozen Core requirements passed under the recorded macOS qualification configuration.`);
    assert.equal(data.measurement.runId, report.measurement.runId);
    assert.equal(data.measurement.appRevision, report.measurement.appRevision);
    assert.equal(data.measurement.scriptsRevision, report.measurement.scriptsRevision);
    assert.equal(data.measurement.platform.os, 'macOS');
    assert.match(data.measurement.platform.version, /^\d+(\.\d+)*$/);
    assert.equal(data.source.publication, pin.publication);
    assert.equal(data.source.evidenceRef, pin.evidenceRef);
    // The unverified labels stay labelled as such.
    assert.equal(data.measurement.appVersionSource, 'operator supplied; not independently verified');
    assert.equal(data.measurement.rawIdentity, 'unverified');
    for (const row of data.rows) {
        assert.ok(row.name && row.criterion && row.observed && row.limitation && row.evidence.length > 0 && row.methods.length > 0, row.id);
        assert.match(row.reportRowUrl, new RegExp(`/blob/${pin.evidenceRef}/${pin.publication}/report\\.md\\?plain=1#L\\d+$`));
        assert.match(row.termInfoUrl, /^https:\/\/terminfo\.dev\//);
    }
});

test('the pin is tied to the publication’s own verified manifest, and every row links to its line in report.md', () => {
    assert.equal(pin.publication, scriptPublication);
    assert.equal(pin.sha256['measurement.json'], manifestSha256);
    assert.equal(loadMeasurement().measurement.runId, pin.expected.runId);
    assert.ok(readFileSync(join(repoRoot, pin.publication, 'report.md'), 'utf8').includes(`node ${pin.publicationScript} --check`));
    const lines = readFileSync(join(repoRoot, pin.publication, 'report.md'), 'utf8').split(/\r?\n/);
    for (const row of buildQualificationData().rows) {
        const line = Number(/#L(\d+)$/.exec(row.reportRowUrl)[1]);
        assert.ok(lines[line - 1].startsWith(`| [\`${row.id}\`](`), row.id);
    }
});

test('a newer Core publication must be adopted deliberately: none exists past the pin', () => {
    assert.deepEqual(findNewerCorePublications(), [], 'a newer Core publication exists under docs/qa/terminfo/: move site/qualification-source.json to it, or say why not');
});

test('a newer Core publication is detected, and unrelated reports are ignored', () => {
    const base = mkdtempSync(join(tmpdir(), 'nt-site-newer-'));
    try {
        const root = join(base, 'docs/qa/terminfo');
        mkdirSync(join(base, pin.publication), { recursive: true });
        cpSync(join(repoRoot, pin.publication, 'report.json'), join(base, pin.publication, 'report.json'));
        const report = JSON.parse(readFileSync(join(repoRoot, pin.publication, 'report.json'), 'utf8'));
        const later = structuredClone(report); later.measurement.startedAt = '2099-01-01T00:00:00.000Z';
        mkdirSync(join(root, '2099-core-results'), { recursive: true }); writeFileSync(join(root, '2099-core-results/report.json'), JSON.stringify(later));
        const earlier = structuredClone(report); earlier.measurement.startedAt = '2001-01-01T00:00:00.000Z';
        mkdirSync(join(root, '2001-core-results'), { recursive: true }); writeFileSync(join(root, '2001-core-results/report.json'), JSON.stringify(earlier));
        mkdirSync(join(root, 'other-kind'), { recursive: true }); writeFileSync(join(root, 'other-kind/report.json'), JSON.stringify({ policy: 'x', rows: [], measurement: { startedAt: '2099-01-01' } }));
        mkdirSync(join(root, 'broken'), { recursive: true }); writeFileSync(join(root, 'broken/report.json'), '{nope');
        mkdirSync(join(root, 'empty'), { recursive: true });
        assert.deepEqual(findNewerCorePublications({ base, pin }).map(p => p.publication), ['docs/qa/terminfo/2099-core-results']);
    } finally { rmSync(base, { recursive: true, force: true }); }
});

test('totals that disagree with the rows, or rows that go missing or repeat, are refused', () => {
    refuses(r => { r.totals.pass += 1; }, /pass total .* disagrees with its rows/);
    refuses(r => { r.totals.inconclusive += 1; }, /inconclusive total .* disagrees with its rows/);
    refuses(r => { r.rows.pop(); }, /79 rows; 80 frozen requirements are pinned/);
    refuses(r => { r.rows.push(structuredClone(r.rows[0])); }, /81 rows/);
    refuses(r => { r.rows[1].id = r.rows[0].id; }, /repeats a requirement id/);
    refuses(r => { r.rows[0].conclusion = 'passed'; }, /unknown outcome "passed"/);
    refuses(r => { r.rows[0].id = 'sgr.not-a-frozen-requirement'; }, /list different ids/);
});

test('a result that no longer matches the pinned expectation is refused, never displayed', () => {
    refuses(r => { r.rows[0].conclusion = 'fail'; r.totals.pass -= 1; r.totals.fail += 1; }, /79 pass rows; the source pin expects 80/);
    refuses(r => { r.rows[0].conclusion = 'inconclusive'; r.totals.pass -= 1; r.totals.inconclusive += 1; }, /79 pass rows/);
    refuses(r => { r.measurement.runId = 'f'.repeat(32); }, /different run/);
    refuses(r => { r.measurement.appRevision = 'a'.repeat(40); }, /different App revision/);
    refuses(r => { r.measurement.scriptsRevision = 'b'.repeat(40); }, /different scripts revision/);
    refuses(r => { delete r.measurement.startedAt; }, /no measurement time/);
    refuses(r => { r.mappingSha256 = '0'.repeat(64); }, /different requirements/);
    refuses(r => { r.schemaVersion = 2; }, /unknown schemaVersion/);
});

test('with the pin moved to match, the claim is derived from the rows rather than fixed text', () => {
    const base = scratch();
    try {
        const { pin: moved } = damaged(base, `${pin.publication}/report.json`, text => {
            const r = JSON.parse(text); r.rows[0].conclusion = 'inconclusive'; r.rows[1].conclusion = 'fail';
            r.totals = { pass: 78, fail: 1, inconclusive: 1 }; return JSON.stringify(r);
        }, 'report.json');
        Object.assign(moved.expected, { pass: 78, fail: 1, inconclusive: 1 });
        const data = buildQualificationData({ base, pin: moved });
        assert.equal(data.claim, '78 of 80 frozen Core requirements passed; 1 failed and 1 were inconclusive under the recorded macOS qualification configuration.');
        assert.deepEqual(data.totals, { pass: 78, fail: 1, inconclusive: 1, total: 80 });
    } finally { rmSync(base, { recursive: true, force: true }); }
});

test('an unpinned change, a missing file or malformed JSON stops the build', () => {
    refuses(r => { r.asOf = '2099-01-01'; }, /differs from the pinned digest/, { pinKey: null });
    refuses(text => `${text} `, /report\.md differs from the pinned digest/, { file: 'report.md', pinKey: null, json: false });
    refuses(text => `${text}\n`, /measurement manifest differs from the pinned digest/, { file: 'measurement.json', pinKey: null, json: false });
    refuses(text => text.replace('"rows"', '"rowz"'), /requirements differ from the pinned digest/, { file: 'docs/qa/terminfo/2026-11-11-core-results/requirements-v1.json', pinKey: null, json: false });
    refuses(() => '{ not json', /report .* is not valid JSON/, { json: false });
    refuses(text => text.split('\n').filter(l => !l.startsWith('| [`sgr.bold`](')).join('\n'), /sgr\.bold: expected exactly one row in report\.md, found 0/, { file: 'report.md', pinKey: 'report.md', json: false });
    const base = scratch();
    try {
        rmSync(join(base, pin.publication, 'report.json'));
        assert.throws(() => buildQualificationData({ base, pin }), /cannot read the report/);
    } finally { rmSync(base, { recursive: true, force: true }); }
    const empty = mkdtempSync(join(tmpdir(), 'nt-site-empty-'));
    try { assert.throws(() => buildQualificationData({ base: empty, pin }), QualificationDataError); } finally { rmSync(empty, { recursive: true, force: true }); }
});

test('an evidence archive that changed or lost its collector output is refused', () => {
    const base = scratch();
    try {
        const file = join(base, pin.publication, 'owned-run.zip'), original = readFileSync(file);
        writeFileSync(file, Buffer.concat([original, Buffer.from('x')]));
        assert.throws(() => buildQualificationData({ base, pin }), /owned-run archive differs from the report digest/);
        writeFileSync(file, original);
        // An archive that is internally consistent (digest in the report and manifest) but has no collector output.
        const archive = Buffer.from(zip({ 'other.json': Buffer.from('{}') }));
        writeFileSync(file, archive);
        const previous = JSON.parse(readFileSync(join(base, pin.publication, 'measurement.json'), 'utf8')).ownedArchiveSha256;
        const rewrite = text => text.replaceAll(previous, sha(archive));
        damaged(base, `${pin.publication}/measurement.json`, rewrite);
        const { pin: p } = damaged(base, `${pin.publication}/report.json`, rewrite, 'report.json');
        p.sha256['measurement.json'] = sha(readFileSync(join(base, pin.publication, 'measurement.json')));
        assert.throws(() => buildQualificationData({ base, pin: p }), /evidence archive has no raw\.json/);
    } finally { rmSync(base, { recursive: true, force: true }); }
});

test('an unknown observation layer is an error, and the layer vocabulary flattens every recorded shape', () => {
    refuses(r => { r.rows[0].observationLayers = ['telepathy']; }, /sgr\.bold: unknown observation layer "telepathy"/);
    refuses(r => { r.rows[0].observationLayers = []; }, /sgr\.bold: no observation layer recorded/);
    assert.deepEqual(layerNames([['stored cells', { name: 'x-effect', outcome: 'supported' }], 'query', { stored: 'supported', resolved: 'supported' }, 'query']), ['stored cells', 'x-effect', 'query', 'stored', 'resolved']);
    assert.deepEqual(methodGroupsFor(['owned-stored-and-resolved-cells', 'query'], 't').groups, ['cells', 'colors', 'replies']);
    const report = JSON.parse(readFileSync(join(repoRoot, pin.publication, 'report.json'), 'utf8'));
    for (const row of report.rows) assert.ok(methodGroupsFor(row.observationLayers, row.id).groups.length > 0, row.id);
});

test('nothing machine-specific or secret-shaped is published', () => {
    const data = buildQualificationData();
    assertPublishable(data);
    const text = JSON.stringify(data);
    assert.doesNotMatch(text, /\/Users\/|\/private\/|\/home\/|C:\\\\/);
    assert.ok(!('pid' in data.measurement) && !('launchProfile' in data.measurement));
    for (const bad of ['/Users/someone/work', 'C:\\\\Users\\\\someone', 'api_key: abc', '-----BEGIN PRIVATE KEY-----']) {
        assert.throws(() => assertPublishable({ note: bad }), /would publish/, bad);
    }
});

test('the source pin itself is validated', () => {
    const bad = (change, expected) => {
        const dir = mkdtempSync(join(tmpdir(), 'nt-site-pin-'));
        try {
            const changed = structuredClone(pin); change(changed);
            writeFileSync(join(dir, 'pin.json'), JSON.stringify(changed));
            assert.throws(() => loadPin(join(dir, 'pin.json')), expected);
        } finally { rmSync(dir, { recursive: true, force: true }); }
    };
    bad(p => { p.evidenceRef = 'main'; }, /full commit SHA/);
    bad(p => { p.sha256['report.json'] = 'abc'; }, /sha256 for report\.json/);
    bad(p => { p.expected.pass = 79; }, /do not add up/);
    bad(p => { p.expected.runId = 'x'; }, /expected run id/);
    bad(p => { p.repository = 'http://example.com/x'; }, /not a GitHub URL/);
    bad(p => { delete p.publication; }, /no publication/);
    assert.throws(() => loadPin(join(tmpdir(), 'nt-site-no-such-pin.json')), /cannot read the source pin/);
});

test('the page template carries no result of its own', () => {
    const page = readFileSync(join(here, '../../site/src/pages/qualification.astro'), 'utf8');
    assert.doesNotMatch(page, /\b80\b/, 'qualification.astro hard-codes the requirement count');
    for (const literal of [pin.expected.runId.slice(0, 8), pin.expected.appRevision.slice(0, 8), pin.expected.scriptsRevision.slice(0, 8), pin.publication, 'macOS 27'])
        assert.ok(!page.includes(literal), `qualification.astro hard-codes ${literal}`);
    assert.ok(page.includes("from '../data/ntilde-qualification.json'"));
    const ignored = readFileSync(join(here, '../../site/.gitignore'), 'utf8');
    assert.match(ignored, /^src\/data\/ntilde-qualification\.json$/m);
});

test('readZipEntry reads stored and deflated entries and refuses damaged or oversized ones', () => {
    const payload = Buffer.from('{"hello":"world"}'.repeat(50));
    const archive = Buffer.from(zip({ 'a.json': payload, 'b.json': Buffer.from('plain') }, { 'b.json': 0 }));
    assert.deepEqual(readZipEntry(archive, 'a.json'), payload);
    assert.equal(readZipEntry(archive, 'b.json').toString(), 'plain');
    assert.throws(() => readZipEntry(archive, 'missing.json'), /no missing\.json/);
    assert.throws(() => readZipEntry(archive, 'a.json', 10), /larger than 10 bytes/);
    assert.throws(() => readZipEntry(Buffer.from('not a zip at all, not even close'), 'a.json'), /no zip directory/);
    assert.throws(() => readZipEntry(archive.subarray(0, 30), 'a.json'), QualificationDataError);
    assert.ok(readdirSync(join(repoRoot, pin.publication)).includes('owned-run.zip'));
});

/** A minimal zip writer for the reader's tests. `methods[name]` of 0 stores; otherwise deflates. */
function zip(files, methods = {}) {
    const locals = [], central = []; let offset = 0;
    for (const [name, data] of Object.entries(files)) {
        const stored = methods[name] === 0, body = stored ? data : deflateRawSync(data), n = Buffer.from(name);
        const local = Buffer.alloc(30); local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(stored ? 0 : 8, 8);
        local.writeUInt32LE(body.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(n.length, 26);
        const head = Buffer.alloc(46); head.writeUInt32LE(0x02014b50, 0); head.writeUInt16LE(20, 4); head.writeUInt16LE(20, 6); head.writeUInt16LE(stored ? 0 : 8, 10);
        head.writeUInt32LE(body.length, 20); head.writeUInt32LE(data.length, 24); head.writeUInt16LE(n.length, 28); head.writeUInt32LE(offset, 42);
        locals.push(local, n, body); central.push(head, n); offset += 30 + n.length + body.length;
    }
    const directory = Buffer.concat(central), end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(central.length / 2, 8); end.writeUInt16LE(central.length / 2, 10);
    end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
    return Buffer.concat([...locals, directory, end]);
}
