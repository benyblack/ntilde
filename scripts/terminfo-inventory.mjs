#!/usr/bin/env node
// Generate or check the terminfo qualification inventory.
//   node scripts/terminfo-inventory.mjs --write --rederive   regenerate the JSON and the readable summary (runs every publication test)
//   node scripts/terminfo-inventory.mjs --check              offline: the committed files must match a fresh derivation
//   node scripts/terminfo-inventory.mjs --check --rederive   also re-run every suite's trusted rederivation and require it to pass
import { readFileSync, writeFileSync } from 'node:fs';
import { buildInventory, inventoryPaths, renderSummary, serialize, testDigest } from './terminfo/qualification-inventory.mjs';

const args = new Set(process.argv.slice(2));
const unknown = [...args].filter(arg => !['--write', '--check', '--rederive'].includes(arg));
if (unknown.length || args.has('--write') === args.has('--check') || (args.has('--write') && !args.has('--rederive'))) {
    console.error('usage: terminfo-inventory.mjs (--write --rederive | --check [--rederive])');
    process.exit(2);
}
const paths = inventoryPaths();
// An offline check re-derives everything except the (slow) rederivation runs, whose committed results it reuses; the
// bound test digest still has to match. `--rederive` runs the real thing.
const committedRederivations = args.has('--check') && !args.has('--rederive')
    ? new Map([...JSON.parse(readFileSync(paths.json, 'utf8')).suites, ...JSON.parse(readFileSync(paths.json, 'utf8')).findings].map(entry => [entry.suite, entry.rederivation]))
    : null;
const inventory = await buildInventory(committedRederivations ? { rederivation: suite => ({ ...(committedRederivations.get(suite.suite) ?? { status: 'not-run', command: null }), testSha256: testDigest(suite) }) } : { rederive: args.has('--rederive') });
if (args.has('--write')) {
    const failed = [...inventory.suites, ...inventory.findings].filter(suite => suite.rederivation.status !== 'passed');
    if (failed.length) {
        console.error(`Refusing to write: rederivation did not pass for ${failed.map(suite => suite.suite).join(', ')}.`);
        process.exit(1);
    }
    writeFileSync(paths.json, serialize(inventory));
    writeFileSync(paths.markdown, `${renderSummary(inventory)}\n`);
    console.log(`Wrote ${inventory.totals.uniqueBenchmarkIds} benchmark ids (${inventory.totals.suiteClaims} claims) to ${paths.json}.`);
} else {
    const committed = JSON.parse(readFileSync(paths.json, 'utf8'));
    const problems = [];
    if (JSON.stringify(inventory) !== JSON.stringify(committed)) problems.push(`${paths.json} differs from a fresh derivation.`);
    if (readFileSync(paths.markdown, 'utf8') !== `${renderSummary(committed)}\n`) problems.push(`${paths.markdown} is not the rendering of the committed JSON.`);
    if (args.has('--rederive')) {
        const failed = [...inventory.suites, ...inventory.findings].filter(suite => suite.rederivation.status !== 'passed');
        if (failed.length) problems.push(`rederivation failed for ${failed.map(suite => suite.suite).join(', ')}.`);
    }
    if (problems.length) { console.error(problems.join('\n')); process.exit(1); }
    console.log(`Inventory OK: ${inventory.totals.uniqueBenchmarkIds} benchmark ids, ${inventory.totals.suiteClaims} claims.`);
}
