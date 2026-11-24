#!/usr/bin/env node
import { readFileSync, writeFileSync } from 'node:fs';
import { buildModernReport, readableModern, serializeModern, modernOutputPaths, modernRoot } from './terminfo/modern-results.mjs';

import { buildModernCursorPublication, modernCursorOutputPaths } from './terminfo/modern-cursor-results.mjs';

import { buildModernModesPublication, modernModesOutputPaths, readableModernModes } from './terminfo/modern-modes-results.mjs';

import { buildModernBlinkPublication, modernBlinkOutputPaths, readableModernBlink } from './terminfo/modern-blink-results.mjs';

import { buildModernViewportPublication, modernViewportOutputPaths, readableModernViewport } from './terminfo/modern-viewport-results.mjs';
import { buildModernOscTitlesPublication, modernOscTitlesOutputPaths, readableModernOscTitles } from './terminfo/modern-osc-titles-results.mjs';

import { buildModernOscCwdPublication, modernOscCwdOutputPaths, readableModernOscCwd } from './terminfo/modern-osc7-cwd-results.mjs';

import { buildModernChildEnvironmentPublication, modernChildEnvironmentOutputPaths, readableModernChildEnvironment } from './terminfo/modern-child-environment-results.mjs';

const args = process.argv.slice(2);
const publications = {
    baseline: [buildModernReport, modernOutputPaths, readableModern],
    '--cursor': [buildModernCursorPublication, modernCursorOutputPaths, readableModern],
    '--modes': [buildModernModesPublication, modernModesOutputPaths, readableModernModes],
    '--blink': [buildModernBlinkPublication, modernBlinkOutputPaths, readableModernBlink],
    '--viewport': [buildModernViewportPublication, modernViewportOutputPaths, readableModernViewport],
    '--child-environment': [buildModernChildEnvironmentPublication, modernChildEnvironmentOutputPaths, readableModernChildEnvironment],
    '--osc7-cwd': [buildModernOscCwdPublication, modernOscCwdOutputPaths, readableModernOscCwd],
    '--osc-titles': [buildModernOscTitlesPublication, modernOscTitlesOutputPaths, readableModernOscTitles],
};
const selected = args.length === 1 ? 'baseline' : args[1];
if (!['--check', '--write'].includes(args[0])
    || (args.length !== 1 && !(args.length === 2 && Object.hasOwn(publications, selected) && selected.startsWith('--')))) {
    console.error('Usage: node scripts/terminfo-modern-results.mjs --check|--write [--cursor|--modes|--blink|--viewport|--osc-titles|--osc7-cwd|--child-environment]');
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
