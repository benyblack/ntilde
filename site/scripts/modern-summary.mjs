// A deliberately pinned publication summary for /compatibility/. This reads
// reviewed reports; the Modern publication tests independently rederive them.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { QualificationDataError, repoRoot } from './qualification-data.mjs';

const check = (ok, message) => {
  if (!ok) throw new QualificationDataError(`Modern summary: ${message}`);
};

export function buildModernSummary({ base = repoRoot } = {}) {
  const pin = JSON.parse(readFileSync(path.join(base, 'site/modern-source.json'), 'utf8'));
  check(pin.schemaVersion === 1, 'unknown source pin');
  check(/^docs\/qa\/terminfo\/[a-z0-9-]+$/.test(pin.publication), 'invalid publication path');
  check(/^[a-f0-9]{40}$/.test(pin.evidenceRef), 'evidence link must name a commit');
  const files = {};
  for (const name of ['report.json', 'report.md']) {
    const text = readFileSync(path.join(base, pin.publication, name), 'utf8').replaceAll('\r\n', '\n');
    check(createHash('sha256').update(text).digest('hex') === pin.sha256[name], `${name} differs from the pinned publication`);
    files[name] = text;
  }
  const report = JSON.parse(files['report.json']);
  check(report.schemaVersion === 15 && report.baseline === 'modern', 'source is not a Modern report');
  check(Array.isArray(report.rows) && report.rows.length === report.requirementsCount, 'requirement count differs from rows');
  check(new Set(report.rows.map(row => row.id)).size === report.rows.length, 'duplicate requirement');
  const totals = { pass: 0, fail: 0, inconclusive: 0 };
  for (const row of report.rows) {
    check(Object.hasOwn(totals, row.conclusion), 'unknown outcome');
    totals[row.conclusion] += 1;
  }
  for (const outcome of Object.keys(totals)) {
    check(totals[outcome] === report.totals[outcome], `${outcome} count differs from rows`);
  }
  const paste = report.rows.find(row => row.id === 'extensions.clipboard-paste');
  check(paste, 'missing clipboard paste requirement');
  const summary = `**${paste.conclusion.toUpperCase()} · ${totals.pass} pass, ${totals.fail} fail, ${totals.inconclusive} inconclusive across ${report.rows.length} frozen requirements.**`;
  check(files['report.md'].split('\n')[2] === summary, 'Markdown summary disagrees with JSON totals');
  const url = `https://github.com/benyblack/ntilde/blob/${pin.evidenceRef}`;
  return {
    totals: { ...totals, total: report.rows.length },
    reportUrl: `${url}/${pin.publication}/report.md`,
    jsonUrl: `${url}/${pin.publication}/report.json`,
    runId: report.clipboardPasteMeasurement.runId,
    measuredAt: report.clipboardPasteMeasurement.startedAt,
    reproduction: 'node scripts/terminfo-modern-results.mjs --check --clipboard-paste',
  };
}
