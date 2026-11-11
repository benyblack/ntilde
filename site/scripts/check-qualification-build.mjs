#!/usr/bin/env node
/**
 * Checks the built /qualification/ page. `npm run build` runs it afterwards, so a deploy cannot publish a
 * page that fails it; `npm run check:build` repeats it on an existing dist/: that the page shows exactly
 * the pinned publication's rows, totals and claim, that every link resolves, and that nothing
 * machine-specific reached the HTML. Exits non-zero on the first failed check group.
 *
 * Internal links are resolved against dist/. External links are not fetched (a build must not
 * depend on the network); they are limited to the hosts this page is meant to link to.
 */

import { existsSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.resolve(here, '..', 'dist');
const dataFile = path.resolve(here, '..', 'src', 'data', 'ntilde-qualification.json');
const ALLOWED_HOSTS = new Set(['github.com', 'terminfo.dev']);

const failures = [];
const check = (ok, message) => {
  if (!ok) failures.push(message);
};
const decode = (text) =>
  text
    .replaceAll('&#39;', "'")
    .replaceAll('&#x27;', "'")
    .replaceAll('&quot;', '"')
    .replaceAll('&lt;', '<')
    .replaceAll('&gt;', '>')
    .replaceAll('&amp;', '&');

const pageFile = path.join(dist, 'qualification', 'index.html');
if (!existsSync(pageFile) || !existsSync(dataFile)) {
  console.error('[check-qualification-build] build the site first (npm run build): dist/qualification/index.html or the generated data is missing');
  process.exit(1);
}
const html = readFileSync(pageFile, 'utf8');
const data = JSON.parse(readFileSync(dataFile, 'utf8'));
const text = decode(html);

// Rows: exactly the pinned publication's, once each, in order.
const rowIds = [...html.matchAll(/<tr[^>]*\sid="case-([^"]+)"/g)].map((match) => decode(match[1]));
check(rowIds.length === data.totals.total, `page has ${rowIds.length} requirement rows; the publication has ${data.totals.total}`);
check(new Set(rowIds).size === rowIds.length, 'page repeats a requirement id');
check(JSON.stringify(rowIds) === JSON.stringify(data.rows.map((row) => row.id)), 'page rows differ from the publication rows');
const outcomes = { pass: 0, fail: 0, inconclusive: 0 };
for (const match of html.matchAll(/<tr[^>]*\sdata-outcome="([^"]+)"/g)) outcomes[match[1]] += 1;
for (const key of Object.keys(outcomes)) check(outcomes[key] === data.totals[key], `page rows show ${outcomes[key]} ${key}; the publication has ${data.totals[key]}`);

// Claim, totals and source identity.
const headings = [...html.matchAll(/<h1[^>]*>([\s\S]*?)<\/h1>/g)].map((match) => decode(match[1]).trim());
check(headings.length === 1 && headings[0] === data.claim, `the page's h1 is not the claim sentence: ${JSON.stringify(headings)}`);
const tiles = /aria-label="Result totals"[^>]*>([\s\S]*?)<\/dl>/.exec(html)?.[1] ?? '';
const shown = [...tiles.matchAll(/<dd[^>]*>\s*(\d+)\s*<\/dd>/g)].map((match) => Number(match[1]));
const wanted = [data.totals.pass, data.totals.fail, data.totals.inconclusive, data.totals.total];
check(JSON.stringify(shown) === JSON.stringify(wanted), `the page's total tiles show ${JSON.stringify(shown)}; the publication has ${JSON.stringify(wanted)}`);
check(text.includes(data.measurement.runId), 'the page does not name the measured run');
check(text.includes(data.source.publication), 'the page does not name its source publication');
check(text.includes(data.measurement.appRevision) && text.includes(data.measurement.scriptsRevision), 'the page does not show the App and scripts revisions');
check(text.includes('operator-supplied, not independently'), 'the page does not say the version label is unverified');
check(text.includes(data.scope), 'the page does not show the report scope');
check(text.includes(data.reproduction.command), 'the page does not show the reproduction command');

// Privacy: nothing machine-specific or secret-shaped.
for (const [pattern, what] of [
  [/\/(?:Users|home|private\/var|tmp)\/[\w.-]/, 'a local path'],
  [/[A-Za-z]:\\[\w.-]/, 'a Windows path'],
  [/localhost|127\.0\.0\.1/, 'a loopback address'],
]) {
  check(!pattern.test(text), `page contains ${what}`);
}

// Links.
// The same rule as astro.config.mjs: unset or the workflow's `__default__` sentinel means the default
// base; an empty value means no prefix.
const configured = process.env.ASTRO_BASE;
const base = (configured === undefined || configured === '__default__' ? '/ntilde' : configured).replace(/\/$/, '');
const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((match) => decode(match[1])));
const hrefsIn = (markup) => [...markup.matchAll(/<a\s[^>]*?href="([^"]*)"/g)].map((match) => decode(match[1]));
const hrefs = hrefsIn(html);
// Only the page's own links must be pinned to the evidence commit; the shared footer links to main.
const mainHrefs = new Set(hrefsIn(html.slice(html.indexOf('<main'), html.indexOf('</main>'))));
check(hrefs.length > data.totals.total * 2, 'page has suspiciously few links');
const internal = (target) => {
  const clean = target.split('#')[0].split('?')[0];
  const rel = clean.startsWith(`${base}/`) || clean === base ? clean.slice(base.length) : clean;
  const file = path.join(dist, rel);
  return existsSync(file) && (statSync(file).isFile() || existsSync(path.join(file, 'index.html')));
};
for (const href of new Set(hrefs)) {
  if (href.startsWith('#')) {
    check(ids.has(decodeURIComponent(href.slice(1))), `in-page link ${href} has no target`);
  } else if (href.startsWith('https://')) {
    const host = new URL(href).hostname;
    check(ALLOWED_HOSTS.has(host), `external link to unexpected host: ${href}`);
    if (host === 'github.com' && href.includes('/blob/') && mainHrefs.has(href)) {
      check(new RegExp(`/blob/${data.source.evidenceRef}/`).test(href) || href.includes('/beorn/terminfo.dev/'), `evidence link is not pinned to the evidence commit: ${href}`);
    }
  } else if (href.startsWith('/')) {
    check(internal(href), `internal link does not resolve in dist: ${href}`);
  } else if (!href.startsWith('mailto:')) {
    check(false, `unexpected link form: ${href}`);
  }
}

if (failures.length) {
  console.error(`[check-qualification-build] ${failures.length} problem(s):`);
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log(`[check-qualification-build] ok: ${rowIds.length} rows, ${new Set(hrefs).size} distinct links, claim and identity present`);
