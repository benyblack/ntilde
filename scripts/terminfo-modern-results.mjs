#!/usr/bin/env node
import { readFileSync, writeFileSync } from 'node:fs';
import { buildModernReport, readableModern, serializeModern, modernOutputPaths, modernRoot } from './terminfo/modern-results.mjs';

import { buildModernCursorPublication, modernCursorOutputPaths } from './terminfo/modern-cursor-results.mjs';

import { buildModernModesPublication, modernModesOutputPaths, readableModernModes } from './terminfo/modern-modes-results.mjs';

const args = process.argv.slice(2);
const publications = {
    baseline: [buildModernReport, modernOutputPaths, readableModern],
    '--cursor': [buildModernCursorPublication, modernCursorOutputPaths, readableModern],
    '--modes': [buildModernModesPublication, modernModesOutputPaths, readableModernModes],
};
const selected = args.length === 1 ? 'baseline' : args[1];
if (!['--check', '--write'].includes(args[0])
    || (args.length !== 1 && !(args.length === 2 && ['--cursor', '--modes'].includes(selected)))) {
    console.error('Usage: node scripts/terminfo-modern-results.mjs --check|--write [--cursor|--modes]');
    process.exitCode = 2;
} else {
    const [build, outputPaths, readable] = publications[selected];
    const report = build(), paths = outputPaths(modernRoot);
    for (const [kind, expected] of [['json', serializeModern(report)], ['markdown', readable(report)]]) {
        if (args[0] === '--write') writeFileSync(paths[kind], expected);
        else if (readFileSync(paths[kind], 'utf8').replaceAll('\r\n', '\n') !== expected) throw new Error('Modern publication differs: ' + paths[kind]);
    }
    console.log(`Modern: ${report.totals.pass} pass / ${report.totals.fail} fail / ${report.totals.inconclusive} inconclusive (69 frozen criteria; owned evidence reassessment).`);
}
