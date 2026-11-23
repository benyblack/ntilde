#!/usr/bin/env node
import { readFileSync, writeFileSync } from 'node:fs';
import { buildModernReport, readableModern, serializeModern, modernOutputPaths, modernRoot } from './terminfo/modern-results.mjs';

import { buildModernCursorPublication, modernCursorOutputPaths } from './terminfo/modern-cursor-results.mjs';

const args = process.argv.slice(2);
const cursor = args[1] === '--cursor';
if (args.length !== (cursor ? 2 : 1) || !['--check', '--write'].includes(args[0])) {
    console.error('Usage: node scripts/terminfo-modern-results.mjs --check|--write [--cursor]');
    process.exitCode = 2;
} else {
    const report = cursor ? buildModernCursorPublication() : buildModernReport();
    const paths = cursor ? modernCursorOutputPaths(modernRoot) : modernOutputPaths(modernRoot);
    for (const [kind, expected] of [['json', serializeModern(report)], ['markdown', readableModern(report)]]) {
        if (args[0] === '--write') writeFileSync(paths[kind], expected);
        else if (readFileSync(paths[kind], 'utf8').replaceAll('\r\n', '\n') !== expected) throw new Error('Modern publication differs: ' + paths[kind]);
    }
    console.log(`Modern: ${report.totals.pass} pass / ${report.totals.fail} fail / ${report.totals.inconclusive} inconclusive (69 frozen criteria; owned evidence reassessment).`);
}
