#!/usr/bin/env node
/**
 * Copy the user guide into the Astro content collection so it ships with the
 * static build at /docs/.
 *
 * Source:  docs/user/*.md            (kept in git; GitHub renders them too)
 * Dest:    site/src/content/docs/    (generated, gitignored)
 *          site/src/docs-meta.json   (generated, gitignored)
 *
 * Each page gets frontmatter the docs route needs: its title (the first `#`
 * heading), a description (the first paragraph), and its place in the sidebar,
 * taken from the order and `##` sections of docs/user/README.md. Links between
 * pages are rewritten at render time by the remark plugin in astro.config.mjs,
 * which knows the deploy base path.
 *
 * The site publishes the guide as it is on `main`, which can run ahead of the
 * newest release. docs-meta.json records that release (the newest vX.Y.Z tag)
 * so every page can say which version the reader can download.
 *
 * Runs before `astro dev` and `astro build` (see `predev`/`prebuild` in
 * site/package.json); `npm run sync:docs` runs it by hand.
 */

import { promises as fs } from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const siteRoot = path.resolve(here, '..');
const repoRoot = path.resolve(siteRoot, '..');

const sourceDir = path.join(repoRoot, 'docs', 'user');
const destDir = path.join(siteRoot, 'src', 'content', 'docs');
const metaPath = path.join(siteRoot, 'src', 'docs-meta.json');

/** The first `# ` heading outside a code fence. */
function firstHeading(markdown) {
  let inFence = false;
  for (const line of markdown.split('\n')) {
    if (line.startsWith('```')) inFence = !inFence;
    if (!inFence && line.startsWith('# ')) return line.slice(2).trim();
  }
  return null;
}

/** The first prose paragraph after the title, as plain text, for <meta name="description">. */
function firstParagraph(markdown) {
  const blocks = markdown.split(/\n\s*\n/);
  for (const block of blocks) {
    const text = block.trim();
    if (!text || /^(#|```|\||[-*] |\d+\. |>)/.test(text)) continue;
    return text
      .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
      .replace(/[`*_]/g, '')
      .replace(/\s+/g, ' ')
      .slice(0, 300);
  }
  return '';
}

/**
 * Sidebar order and groups from README.md: each `## Section` heading starts a group,
 * and each `[label](page.md)` link under it is the next page.
 */
function readIndex(readme) {
  const order = new Map();
  let group = '';
  let n = 1;
  for (const line of readme.split('\n')) {
    const heading = /^## (.+)$/.exec(line);
    if (heading) {
      group = heading[1].trim();
      continue;
    }
    // Only the lists under a `##` section: the intro above them links to pages in passing.
    if (!group || !/^\s*[-*] /.test(line)) continue;
    for (const m of line.matchAll(/\]\(([\w-]+)\.md(?:#[^)]*)?\)/g)) {
      if (!order.has(m[1])) order.set(m[1], { group, order: n++ });
    }
  }
  return order;
}

/** The newest release tag (vX.Y.Z, no pre-release suffix), or null when the checkout has no tags. */
function latestRelease() {
  try {
    const out = execFileSync('git', ['tag', '--list', 'v*', '--sort=-v:refname'], {
      cwd: repoRoot,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    });
    return out.split('\n').find((t) => /^v\d+\.\d+\.\d+$/.test(t.trim()))?.trim() ?? null;
  } catch {
    return null;
  }
}

const yaml = (s) => JSON.stringify(s);

async function main() {
  const files = (await fs.readdir(sourceDir)).filter((f) => f.endsWith('.md')).sort();
  const readme = await fs.readFile(path.join(sourceDir, 'README.md'), 'utf8');
  const index = readIndex(readme);

  await fs.rm(destDir, { recursive: true, force: true });
  await fs.mkdir(destDir, { recursive: true });

  for (const file of files) {
    const name = file.slice(0, -3);
    const body = await fs.readFile(path.join(sourceDir, file), 'utf8');
    const isIndex = name === 'README';
    const place = isIndex ? { group: '', order: 0 } : index.get(name) ?? { group: 'More', order: 1000 };
    const title = firstHeading(body) ?? name;

    const frontmatter = [
      '---',
      `title: ${yaml(title)}`,
      `navTitle: ${yaml(isIndex ? 'Overview' : title)}`,
      `description: ${yaml(firstParagraph(body) || title)}`,
      `group: ${yaml(place.group)}`,
      `order: ${place.order}`,
      `source: ${yaml(file)}`,
      '---',
      '',
    ].join('\n');

    await fs.writeFile(path.join(destDir, isIndex ? 'index.md' : file), frontmatter + body);
  }

  const release = latestRelease();
  await fs.writeFile(metaPath, JSON.stringify({ latestRelease: release }, null, 2) + '\n');

  console.log(
    `[sync-docs] copied ${files.length} page(s) from ${path.relative(repoRoot, sourceDir)}/ -> ` +
      `${path.relative(repoRoot, destDir)}/ (latest release: ${release ?? 'none found'})`,
  );
}

try {
  await main();
} catch (err) {
  console.error('[sync-docs] failed:', err);
  process.exit(1);
}
