#!/usr/bin/env node
/**
 * Writes site/src/data/ntilde-qualification.json, the data behind /qualification/,
 * from the publication pinned in site/qualification-source.json.
 *
 * Runs before `astro dev`, `astro build` and `astro check` (see the hooks in
 * package.json). The output is generated and git-ignored: the page has no copy of
 * the results to drift. A missing or changed source fails the build.
 */

import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { buildQualificationData, outputPath, QualificationDataError, repoRoot } from './qualification-data.mjs';

try {
  const data = buildQualificationData();
  mkdirSync(path.dirname(outputPath), { recursive: true });
  writeFileSync(outputPath, `${JSON.stringify(data, null, 2)}\n`);
  console.log(
    `[qualification] ${data.totals.pass}/${data.totals.total} pass, ${data.totals.fail} fail, ${data.totals.inconclusive} inconclusive ` +
      `from ${data.source.publication} (run ${data.measurement.runId.slice(0, 8)}) -> ${path.relative(repoRoot, outputPath)}`,
  );
} catch (error) {
  if (!(error instanceof QualificationDataError)) throw error;
  console.error(`[qualification] ${error.message}`);
  process.exit(1);
}
