import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { buildModernSummary } from '../../site/scripts/modern-summary.mjs';
import { repoRoot } from '../../site/scripts/qualification-data.mjs';

const pin = JSON.parse(readFileSync(path.join(repoRoot, 'site/modern-source.json'), 'utf8'));

function modified(change, { repin = true, updateMarkdown = true } = {}) {
  const base = mkdtempSync(path.join(tmpdir(), 'nt-modern-site-'));
  try {
    mkdirSync(path.join(base, 'site'));
    mkdirSync(path.join(base, pin.publication), { recursive: true });
    for (const name of ['report.json', 'report.md']) {
      cpSync(path.join(repoRoot, pin.publication, name), path.join(base, pin.publication, name));
    }
    const file = path.join(base, pin.publication, 'report.json');
    const report = JSON.parse(readFileSync(file, 'utf8'));
    change(report);
    const text = JSON.stringify(report);
    writeFileSync(file, text);
    const next = structuredClone(pin);
    if (repin) next.sha256['report.json'] = createHash('sha256').update(text).digest('hex');
    if (updateMarkdown) {
      const mdFile = path.join(base, pin.publication, 'report.md');
      const lines = readFileSync(mdFile, 'utf8').replaceAll('\r\n', '\n').split('\n');
      const { pass, fail, inconclusive } = report.totals;
      lines[2] = `**PASS · ${pass} pass, ${fail} fail, ${inconclusive} inconclusive across ${report.rows.length} frozen requirements.**`;
      const markdown = lines.join('\n');
      writeFileSync(mdFile, markdown);
      next.sha256['report.md'] = createHash('sha256').update(markdown).digest('hex');
    }
    writeFileSync(path.join(base, 'site/modern-source.json'), JSON.stringify(next));
    return buildModernSummary({ base });
  } finally {
    rmSync(base, { recursive: true, force: true });
  }
}

test('Modern summary exposes the pinned report totals and permanent evidence links', () => {
  const data = buildModernSummary();
  assert.deepEqual(data.totals, { pass: 67, fail: 0, inconclusive: 2, total: 69 });
  assert.equal(data.runId, '88d2b28d7569682a49ddbf49ec955d5f');
  assert.equal(data.measuredAt, '2026-11-25T10:54:36.430Z');
  assert.equal(data.reproduction, 'node scripts/terminfo-modern-results.mjs --check --clipboard-paste');
  assert.ok(data.reportUrl.includes(`/blob/${pin.evidenceRef}/${pin.publication}/report.md`));
  assert.equal(data.jsonUrl, data.reportUrl.replace(/\.md$/, '.json'));
});

test('changed publication bytes are refused before displaying a summary', () => {
  assert.throws(() => modified(report => { report.totals.pass++; }, { repin: false }), /differs from the pinned publication/);
});

test('a deliberately repinned report still must have consistent membership and totals', () => {
  assert.throws(() => modified(report => { report.totals.pass++; }), /pass count differs/);
  assert.throws(() => modified(report => { report.rows.pop(); }), /requirement count differs/);
  assert.throws(() => modified(report => { report.rows[1].id = report.rows[0].id; }), /duplicate requirement/);
  assert.throws(() => modified(report => { report.rows[0].conclusion = 'supported'; }), /unknown outcome/);
  assert.throws(() => modified(report => { report.baseline = 'core'; }), /not a Modern report/);
});

test('when deliberately repinned, changed valid conclusions change the summary rather than leaving stale totals', () => {
  const data = modified(report => {
    const row = report.rows.find(row => row.conclusion === 'pass');
    row.conclusion = 'inconclusive';
    report.totals.pass--;
    report.totals.inconclusive++;
  });
  assert.deepEqual(data.totals, { pass: 66, fail: 0, inconclusive: 3, total: 69 });
});

test('repinning only JSON cannot publish new totals beside a stale readable report', () => {
  assert.throws(() => modified(report => {
    report.rows.find(row => row.conclusion === 'pass').conclusion = 'inconclusive';
    report.totals.pass--;
    report.totals.inconclusive++;
  }, { updateMarkdown: false }), /Markdown summary disagrees/);
});
