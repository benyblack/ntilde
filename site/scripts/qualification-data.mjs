/**
 * Builds the data behind the /qualification/ page from one pinned, committed
 * Core qualification publication under docs/qa/terminfo/.
 *
 * The page never carries its own list of requirements or its own score. Every
 * row, total and identity field is read from the publication's report.json and
 * checked against the digests and expectations in site/qualification-source.json.
 * A missing, changed or malformed source throws a QualificationDataError, so the
 * build fails instead of publishing a number nobody measured.
 *
 * Node 20 compatible (the Pages workflow runs it): only node:fs, node:path,
 * node:crypto and node:zlib.
 */

import { readdirSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { inflateRawSync } from 'node:zlib';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const siteRoot = path.resolve(here, '..');
export const repoRoot = path.resolve(siteRoot, '..');
export const pinPath = path.join(siteRoot, 'qualification-source.json');
export const outputPath = path.join(siteRoot, 'src', 'data', 'ntilde-qualification.json');

export class QualificationDataError extends Error {
  constructor(message) {
    super(`Qualification data: ${message}`);
    this.name = 'QualificationDataError';
  }
}

const check = (ok, message) => {
  if (!ok) throw new QualificationDataError(message);
};

const OUTCOMES = new Set(['pass', 'fail', 'inconclusive']);
const HEX64 = /^[a-f\d]{64}$/;
const HEX40 = /^[a-f\d]{40}$/;
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
/** Text files are pinned as LF whatever the checkout's line endings. */
const lfDigest = (bytes) => sha256(Buffer.from(bytes.toString('utf8').replaceAll('\r\n', '\n')));

function readBytes(base, relative, what) {
  try {
    return readFileSync(path.join(base, relative));
  } catch (error) {
    throw new QualificationDataError(`cannot read ${what} (${relative}): ${error.code ?? error.message}`);
  }
}

function readJson(base, relative, what) {
  const bytes = readBytes(base, relative, what);
  try {
    return { bytes, value: JSON.parse(bytes.toString('utf8')) };
  } catch {
    throw new QualificationDataError(`${what} (${relative}) is not valid JSON`);
  }
}

/**
 * One named entry of a zip, bounded, without extracting anything to disk.
 * Only what the retained owned-run archive uses: stored or deflated entries, no zip64.
 */
export function readZipEntry(archive, name, maxBytes = 8 * 1024 * 1024) {
  const eocd = (() => {
    for (let i = archive.length - 22; i >= Math.max(0, archive.length - 22 - 0xffff); i--) {
      if (archive.readUInt32LE(i) === 0x06054b50) return i;
    }
    return -1;
  })();
  check(eocd >= 0, 'evidence archive has no zip directory');
  const count = archive.readUInt16LE(eocd + 10);
  let offset = archive.readUInt32LE(eocd + 16);
  check(count !== 0xffff && offset !== 0xffffffff, 'zip64 evidence archives are not supported');
  let found;
  for (let i = 0; i < count; i++) {
    check(archive.readUInt32LE(offset) === 0x02014b50, 'evidence archive directory is damaged');
    const nameLength = archive.readUInt16LE(offset + 28);
    const entryName = archive.toString('utf8', offset + 46, offset + 46 + nameLength);
    if (entryName === name) {
      check(!found, `evidence archive lists ${name} twice`);
      found = {
        method: archive.readUInt16LE(offset + 10),
        compressed: archive.readUInt32LE(offset + 20),
        size: archive.readUInt32LE(offset + 24),
        local: archive.readUInt32LE(offset + 42),
      };
    }
    offset += 46 + nameLength + archive.readUInt16LE(offset + 30) + archive.readUInt16LE(offset + 32);
  }
  check(found, `evidence archive has no ${name}`);
  check(found.size <= maxBytes, `${name} in the evidence archive is larger than ${maxBytes} bytes`);
  check(archive.readUInt32LE(found.local) === 0x04034b50, 'evidence archive entry header is damaged');
  const start = found.local + 30 + archive.readUInt16LE(found.local + 26) + archive.readUInt16LE(found.local + 28);
  const raw = archive.subarray(start, start + found.compressed);
  check(raw.length === found.compressed, 'evidence archive is truncated');
  check(found.method === 0 || found.method === 8, `unsupported compression for ${name}`);
  const data = found.method === 0 ? raw : inflateRawSync(raw, { maxOutputLength: maxBytes });
  check(data.length === found.size, `${name} in the evidence archive has the wrong size`);
  return data;
}

// --- Vocabulary ------------------------------------------------------------------------------------

export const CATEGORY_LABELS = {
  sgr: 'Text styling (SGR)',
  cursor: 'Cursor',
  erase: 'Erasing',
  modes: 'Modes',
  scrollback: 'Scrolling and history',
  text: 'Text and tabs',
  unicode: 'Unicode',
  reset: 'Reset',
  charsets: 'Character sets',
  device: 'Device and environment',
  extensions: 'Extensions',
};

/** The observation method groups readers can filter by. */
export const METHOD_GROUPS = {
  cells: { label: 'Stored cells and cursor', detail: 'The App’s stored cells, cursor, margins or history' },
  colors: { label: 'Resolved colors', detail: 'Colors resolved from the stored cell state' },
  replies: { label: 'Terminal replies', detail: 'Replies the terminal sent back to a query or cursor report' },
  pixels: { label: 'Controlled pixels', detail: 'Pixels rendered from a controlled test fixture' },
  state: { label: 'App state readback', detail: 'Internal App, mode or parser state read back' },
  child: { label: 'Child-process output', detail: 'Output of the session’s own child process' },
};

/**
 * What each observation-layer name recorded in report.json means here. An unknown layer name
 * is an error rather than a guess: a future publication that adds one must say what it measures.
 */
const LAYER_GROUPS = {
  'owned-cells': ['cells'],
  'owned-stored-cells': ['cells'],
  'owned-stored-physical-cells': ['cells'],
  'owned-stored-and-resolved-cells': ['cells', 'colors'],
  'owned-resolved-colors': ['colors'],
  'owned-render-pixels': ['pixels'],
  'controlled pixels': ['pixels'],
  'decrqss-query': ['replies'],
  query: ['replies'],
  'cursor position report': ['replies'],
  behavior: ['replies'],
  'owned-mode-readback': ['state'],
  'parser-icon-state/title-independence': ['state'],
  'desktop-backend-reset-api/stored-state': ['state', 'cells'],
  'parent-journaled-state': ['state'],
  'stored-state/cursor/margins/history': ['cells'],
  'stored state': ['cells'],
  'physical cursor': ['cells'],
  'stored cells': ['cells'],
  'stored attributes': ['cells'],
  'stored physical cells and cursor': ['cells'],
  cursor: ['cells'],
  'actual scrollback': ['cells'],
  'margins and modes': ['state'],
  'active buffer': ['state'],
  'verified App and PTY geometry': ['state'],
  'actual-session-child-output': ['child'],
  // Named effect results: { name, outcome } and { stored, resolved }.
  'main-contents-and-cursor': ['cells'],
  'styles-active-on-x-cleared-on-y': ['cells', 'colors'],
  stored: ['cells'],
  resolved: ['colors'],
};

/** Flatten the mixed strings, arrays and objects of an observationLayers value to layer names. */
export function layerNames(layers, id = 'row') {
  const out = [];
  const visit = (value) => {
    if (typeof value === 'string') out.push(value);
    else if (Array.isArray(value)) value.forEach(visit);
    else if (value && typeof value === 'object') {
      if (typeof value.name === 'string') out.push(value.name);
      else {
        const keys = Object.keys(value);
        check(keys.length > 0, `${id}: empty observation layer`);
        out.push(...keys);
      }
    } else throw new QualificationDataError(`${id}: unreadable observation layer`);
  };
  visit(layers);
  return [...new Set(out)];
}

export function methodGroupsFor(layers, id) {
  const names = layerNames(layers, id);
  check(names.length > 0, `${id}: no observation layer recorded`);
  const groups = new Set();
  for (const name of names) {
    const mapped = LAYER_GROUPS[name];
    check(mapped, `${id}: unknown observation layer "${name}"; say what it measures in site/scripts/qualification-data.mjs`);
    mapped.forEach((group) => groups.add(group));
  }
  return { names, groups: Object.keys(METHOD_GROUPS).filter((group) => groups.has(group)) };
}

const joinList = (items) =>
  items.length < 2 ? (items[0] ?? '') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;

/** A short, derived statement of what the evidence for a row does not cover. */
export function limitationFor(row, names, groups) {
  const labels = groups.map((group) => METHOD_GROUPS[group].label.toLowerCase());
  const parts = [];
  if (groups.includes('pixels')) {
    parts.push(
      `Controlled pixels cover the measured configuration only, not every font, theme or renderer. Other evidence: ${joinList(labels.filter((l) => l !== 'controlled pixels')) || 'none'}.`,
    );
  } else {
    parts.push(`Based on ${joinList(labels)}; no rendered appearance is claimed.`);
  }
  if (row.pinnedProbe?.stockVerdict === 'manual-ungraded') {
    parts.push('The frozen requirement is a manual check upstream; this result is Ntilde’s own independent observation.');
  }
  if (names.includes('desktop-backend-reset-api/stored-state')) {
    parts.push('Qualifies Ntilde’s named desktop reset API, not an upstream App endpoint or RIS alone.');
  }
  if (names.includes('parser-icon-state/title-independence')) {
    parts.push('Parser icon state only; host icon presentation is not claimed.');
  }
  if (row.materialCatalogueChange === true) {
    parts.push('The upstream catalogue changed this criterion after the frozen catalogue; see the report row.');
  }
  return parts.join(' ');
}

// --- Source loading --------------------------------------------------------------------------------

export function loadPin(pinFile = pinPath) {
  let pin;
  try {
    pin = JSON.parse(readFileSync(pinFile, 'utf8'));
  } catch (error) {
    throw new QualificationDataError(`cannot read the source pin (${error.code ?? 'invalid JSON'})`);
  }
  check(pin.schemaVersion === 1, 'the source pin has an unknown schemaVersion');
  for (const key of ['repository', 'publication', 'publicationScript', 'requirements', 'evidenceRef']) {
    check(typeof pin[key] === 'string' && pin[key], `the source pin has no ${key}`);
  }
  check(/^https:\/\/github\.com\/[\w.-]+\/[\w.-]+$/.test(pin.repository), 'the source pin repository is not a GitHub URL');
  check(HEX40.test(pin.evidenceRef), 'the source pin evidenceRef is not a full commit SHA');
  for (const key of ['report.json', 'report.md', 'measurement.json', 'requirements']) {
    check(HEX64.test(pin.sha256?.[key] ?? ''), `the source pin has no valid sha256 for ${key}`);
  }
  const e = pin.expected ?? {};
  check(HEX40.test(e.appRevision ?? '') && HEX40.test(e.scriptsRevision ?? ''), 'the source pin has no expected revisions');
  check(/^[a-f\d]{32}$/.test(e.runId ?? ''), 'the source pin has no expected run id');
  for (const key of ['rows', 'pass', 'fail', 'inconclusive']) {
    check(Number.isInteger(e[key]) && e[key] >= 0, `the source pin expects no ${key} count`);
  }
  check(e.pass + e.fail + e.inconclusive === e.rows, 'the source pin expectations do not add up to its row count');
  return pin;
}

const permalink = (pin, relative) => `${pin.repository}/blob/${pin.evidenceRef}/${relative}`;

function verifyTotals(report, pin) {
  // Report shape and totals: recomputed from the rows, never trusted from a label.
  check(report.schemaVersion === 1, 'the report has an unknown schemaVersion');
  check(Array.isArray(report.rows), 'the report has no rows');
  check(report.rows.length === pin.expected.rows, `the report has ${report.rows.length} rows; ${pin.expected.rows} frozen requirements are pinned`);
  const ids = new Set(report.rows.map((row) => row.id));
  check(ids.size === report.rows.length, 'the report repeats a requirement id');
  const counts = { pass: 0, fail: 0, inconclusive: 0 };
  for (const row of report.rows) {
    check(OUTCOMES.has(row.conclusion), `${row.id}: unknown outcome "${row.conclusion}"`);
    counts[row.conclusion] += 1;
  }
  for (const outcome of OUTCOMES) {
    check(report.totals?.[outcome] === counts[outcome], `the report's ${outcome} total (${report.totals?.[outcome]}) disagrees with its rows (${counts[outcome]})`);
    check(counts[outcome] === pin.expected[outcome], `${counts[outcome]} ${outcome} rows; the source pin expects ${pin.expected[outcome]}`);
  }
  return { counts, total: report.rows.length, ids };
}

function verifyIdentity(report, measurementFile, pin) {
  // Identity.
  const m = report.measurement ?? {};
  check(m.runId === pin.expected.runId, 'the report is for a different run than the pinned one');
  check(m.appRevision === pin.expected.appRevision && HEX40.test(m.appRevision), 'the report names a different App revision than the pinned one');
  check(m.scriptsRevision === pin.expected.scriptsRevision && HEX40.test(m.scriptsRevision), 'the report names a different scripts revision than the pinned one');
  check(typeof m.startedAt === 'string' && !Number.isNaN(Date.parse(m.startedAt)), 'the report has no measurement time');
  check(m.geometry?.rows > 0 && m.geometry?.cols > 0, 'the report has no measured geometry');
  for (const key of ['executableSha256', 'assemblySha256', 'sourceSnapshotManifestSha256', 'ownedArchiveSha256', 'appSourceManifestSha256']) {
    check(HEX64.test(m[key] ?? ''), `the report has no valid ${key}`);
  }
  const mm = measurementFile.measurement ?? {};
  for (const key of ['runId', 'appRevision', 'scriptsRevision', 'receiptSchema', 'sourcePolicy']) {
    check(mm[key] === m[key], `the measurement manifest and report disagree on ${key}`);
  }
  check(measurementFile.ownedArchiveSha256 === m.ownedArchiveSha256, 'the measurement manifest and report disagree on the evidence archive');
  return m;
}

/** Platform and collector identity are recorded in the retained raw collector output: read them from the digest-checked archive instead of restating them. */
function readCollectorOutput(base, dir, m) {
  const archive = readBytes(base, `${dir}/owned-run.zip`, 'the retained owned-run archive');
  check(sha256(archive) === m.ownedArchiveSha256, 'the retained owned-run archive differs from the report digest');
  let raw;
  try {
    raw = JSON.parse(readZipEntry(archive, 'raw.json').toString('utf8'));
  } catch (error) {
    if (error instanceof QualificationDataError) throw error;
    throw new QualificationDataError('raw.json in the evidence archive is not valid JSON');
  }
  check(raw.runId === m.stockRunId, 'the raw collector output is for a different stock run than the report');
  check(raw.target?.id === 'ntilde' && raw.target?.os === 'macos' && typeof raw.target?.osVersion === 'string', 'the raw collector output does not record a macOS Ntilde target');
  check(/^[\w.+-]{1,40}$/.test(raw.target.osVersion), 'the recorded OS version is not a plain version string');
  return raw;
}

/** One page row from a report row and its frozen requirement. */
function buildRow({ row, entry, readableLines, pin }) {
  check(entry.siteRequirement === row.requirement, `${row.id}: the report criterion differs from the frozen requirement`);
  check(typeof entry.name === 'string' && entry.name, `${row.id}: the frozen requirements give it no name`);
  check((entry.url ?? '').startsWith('https://terminfo.dev/'), `${row.id}: the frozen requirements give it no terminfo.dev link`);
  check(typeof row.explanation === 'string' && row.explanation, `${row.id}: no observed behavior recorded`);
  check(Array.isArray(row.evidence) && row.evidence.length > 0, `${row.id}: no evidence recorded`);
  const { names: layers, groups } = methodGroupsFor(row.observationLayers, row.id);
  // The row's own line in the readable report, for a link that lands on it.
  const lines = readableLines.flatMap((line, index) => (line.startsWith(`| [\`${row.id}\`](`) ? [index + 1] : []));
  check(lines.length === 1, `${row.id}: expected exactly one row in report.md, found ${lines.length}`);
  const reportLink = permalink(pin, `${pin.publication}/report.md`);
  return {
    id: row.id,
    name: entry.name,
    category: entry.category,
    termInfoUrl: entry.url,
    outcome: row.conclusion,
    criterion: row.requirement,
    observed: row.explanation,
    methods: groups,
    layers,
    evidence: [...new Set(row.evidence.map((item) => `${item.file}#${item.caseId}`))],
    reportRowUrl: `${reportLink}?plain=1#L${lines[0]}`,
    limitation: limitationFor(row, layers, groups),
  };
}

/** Build the page data. `base` is the repository root; `pin` the parsed source pin. */
export function buildQualificationData({ base = repoRoot, pin = loadPin() } = {}) {
  const dir = pin.publication;
  const reportFile = `${dir}/report.json`;
  const { bytes: reportBytes, value: report } = readJson(base, reportFile, 'the report');
  check(lfDigest(reportBytes) === pin.sha256['report.json'], `${reportFile} differs from the pinned digest; update site/qualification-source.json deliberately`);

  const readable = readBytes(base, `${dir}/report.md`, 'the readable report');
  check(lfDigest(readable) === pin.sha256['report.md'], `${dir}/report.md differs from the pinned digest`);
  const readableLines = readable.toString('utf8').split(/\r?\n/);

  const { bytes: measurementBytes, value: measurementFile } = readJson(base, `${dir}/measurement.json`, 'the measurement manifest');
  check(lfDigest(measurementBytes) === pin.sha256['measurement.json'], 'the measurement manifest differs from the pinned digest');
  const { bytes: requirementsBytes, value: requirements } = readJson(base, pin.requirements, 'the frozen requirements');
  check(lfDigest(requirementsBytes) === pin.sha256.requirements, 'the frozen requirements differ from the pinned digest');
  check(report.mappingSha256 === pin.sha256.requirements, 'the report was derived from different requirements than the pinned ones');

  const { counts, total, ids } = verifyTotals(report, pin);

  const m = verifyIdentity(report, measurementFile, pin);

  const raw = readCollectorOutput(base, dir, m);

  // Requirement names and URLs come from the frozen requirements, matched to the report row by row.
  check(Array.isArray(requirements.rows), 'the frozen requirements have no rows');
  const names = new Map(requirements.rows.map((entry) => [entry.id, entry]));
  check(names.size === requirements.rows.length, 'the frozen requirements repeat an id');
  check(names.size === ids.size && [...ids].every((id) => names.has(id)), 'the report and the frozen requirements list different ids');

  const methodCounts = Object.fromEntries(Object.keys(METHOD_GROUPS).map((group) => [group, 0]));
  const categoryCounts = new Map();
  const rows = report.rows.map((row) => {
    const built = buildRow({ row, entry: names.get(row.id), readableLines, pin });
    built.methods.forEach((group) => (methodCounts[group] += 1));
    categoryCounts.set(built.category, (categoryCounts.get(built.category) ?? 0) + 1);
    return built;
  });

  check(typeof report.scope === 'string' && report.scope.trim(), 'the report records no scope');
  const outcomeText =
    counts.pass === total
      ? `${counts.pass} of ${total} frozen Core requirements passed`
      : `${counts.pass} of ${total} frozen Core requirements passed; ${counts.fail} failed and ${counts.inconclusive} were inconclusive`;
  const attempts = Array.isArray(report.attempts) ? report.attempts : [];

  const data = {
    schemaVersion: 1,
    claim: `${outcomeText} under the recorded macOS qualification configuration.`,
    totals: { ...counts, total },
    source: {
      repository: pin.repository,
      publication: dir,
      publicationScript: pin.publicationScript,
      evidenceRef: pin.evidenceRef,
      reportJsonSha256: pin.sha256['report.json'],
      measurementJsonSha256: pin.sha256['measurement.json'],
      requirementsSha256: pin.sha256.requirements,
      links: {
        report: permalink(pin, `${dir}/report.md`),
        reportJson: permalink(pin, `${dir}/report.json`),
        measurement: permalink(pin, `${dir}/measurement.json`),
        sourceManifest: permalink(pin, `${dir}/app-source-manifest.json`),
        requirements: permalink(pin, pin.requirements),
        script: permalink(pin, pin.publicationScript),
        appCommit: `${pin.repository}/commit/${m.appRevision}`,
        scriptsCommit: `${pin.repository}/commit/${m.scriptsRevision}`,
      },
    },
    measurement: {
      runId: m.runId,
      stockRunId: m.stockRunId,
      startedAt: m.startedAt,
      asOf: report.asOf,
      appRevision: m.appRevision,
      scriptsRevision: m.scriptsRevision,
      receiptSchema: m.receiptSchema,
      sourcePolicy: m.sourcePolicy,
      executableSha256: m.executableSha256,
      assemblySha256: m.assemblySha256,
      sourceSnapshotManifestSha256: m.sourceSnapshotManifestSha256,
      ownedArchiveSha256: m.ownedArchiveSha256,
      geometry: { rows: m.geometry.rows, cols: m.geometry.cols },
      term: typeof m.child?.term === 'string' ? m.child.term : null,
      platform: { os: 'macOS', version: raw.target.osVersion },
      appVersionLabel: m.appVersion ?? null,
      appVersionSource: m.appVersionSource ?? null,
      rawIdentity: m.rawIdentity ?? null,
      suiteComplete: m.suiteComplete ?? null,
      collectorAdapter: raw.collectorAdapter?.id ?? null,
      collectorAdapterSha256: raw.collectorAdapter?.bundleSha256 ?? null,
    },
    scope: report.scope,
    attribution: typeof requirements.sourceAttribution === 'string' ? requirements.sourceAttribution : '',
    upstream: {
      revision: requirements.upstreamRevision ?? null,
      frozenCatalogueRevision: requirements.frozenCatalogueRevision ?? null,
    },
    attempts: {
      superseded: attempts.filter((a) => a.status === 'superseded').length,
      complete: attempts.filter((a) => a.status === 'complete').length,
    },
    reproduction: {
      command: `node ${pin.publicationScript} --check`,
      sourceCommand: `node ${pin.publicationScript} --assert-current-source`,
    },
    categories: [...categoryCounts].map(([id, count]) => ({ id, label: CATEGORY_LABELS[id] ?? id, count })),
    methods: Object.entries(METHOD_GROUPS).map(([id, group]) => ({ id, label: group.label, detail: group.detail, count: methodCounts[id] })),
    rows,
  };
  assertPublishable(data);
  return data;
}

/** Nothing machine-specific or secret-shaped leaves in the generated data. */
export function assertPublishable(data) {
  const text = JSON.stringify(data);
  const patterns = [
    [/\/(?:Users|home|private|var|tmp|root)\//, 'a local filesystem path'],
    [/[A-Za-z]:\\\\/, 'a Windows path'],
    [/(?:token|secret|password|api[_-]?key|session[_-]?id)"?\s*[:=]/i, 'a credential-shaped field'],
    [/BEGIN [A-Z ]*PRIVATE KEY/, 'a private key'],
  ];
  for (const [pattern, what] of patterns) check(!pattern.test(text), `the generated data would publish ${what}`);
}

/**
 * Core publications under docs/qa/terminfo/ that were measured after the pinned one. The page
 * claims one frozen run, so a newer publication must be adopted (or set aside) on purpose: the
 * self-test fails while one exists and the pin has not moved. A publication is recognised by the
 * pinned report's own policy and row count, so other kinds of report are ignored.
 */
export function findNewerCorePublications({ base = repoRoot, pin = loadPin() } = {}) {
  const { value: pinned } = readJson(base, `${pin.publication}/report.json`, 'the pinned report');
  const pinnedAt = Date.parse(pinned.measurement?.startedAt);
  check(!Number.isNaN(pinnedAt), 'the pinned report has no measurement time');
  const root = path.join(base, 'docs', 'qa', 'terminfo');
  const newer = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (!entry.isDirectory() || `docs/qa/terminfo/${entry.name}` === pin.publication) continue;
    let report;
    try {
      report = JSON.parse(readFileSync(path.join(root, entry.name, 'report.json'), 'utf8'));
    } catch {
      continue;
    }
    const startedAt = Date.parse(report?.measurement?.startedAt);
    if (report?.policy === pinned.policy && Array.isArray(report.rows) && report.rows.length === pinned.rows.length && startedAt > pinnedAt) {
      newer.push({ publication: `docs/qa/terminfo/${entry.name}`, startedAt: report.measurement.startedAt, totals: report.totals });
    }
  }
  return newer.sort((a, b) => Date.parse(a.startedAt) - Date.parse(b.startedAt));
}
