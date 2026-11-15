#!/usr/bin/env node
import { readFileSync, writeFileSync } from 'node:fs';
import { buildModernReport, readableModern, serializeModern, modernOutputPaths, modernRoot } from './terminfo/modern-results.mjs';

const args = process.argv.slice(2);
if (args.length !== 1 || !['--check', '--write'].includes(args[0])) {
    console.error('Usage: node scripts/terminfo-modern-results.mjs --check|--write');
    process.exitCode = 2;
} else {
    const report = buildModernReport(), paths = modernOutputPaths(modernRoot);
    for (const [kind, expected] of [['json', serializeModern(report)], ['markdown', readableModern(report)]]) {
        if (args[0] === '--write') writeFileSync(paths[kind], expected);
        else if (readFileSync(paths[kind], 'utf8').replaceAll('\r\n', '\n') !== expected) throw new Error('Modern publication differs: ' + paths[kind]);
    }
    console.log(`Modern: ${report.totals.pass} pass / ${report.totals.fail} fail / ${report.totals.inconclusive} inconclusive (69 frozen criteria; owned evidence reassessment).`);
}
