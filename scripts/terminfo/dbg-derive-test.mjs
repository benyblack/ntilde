import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { adaptReflowCollector } from './ascii-reflow-grading-collector.mjs';
import { adaptOscTitleCollector } from './osc-title-grading-collector.mjs';
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const { adaptCollector } = await import('./collector.mjs');
const names = ['grading', 'sgr-colors', 'sgr-rgb', 'sgr-attrs', 'sgr-palette', 'sgr-truecolor', 'sgr-underline', 'sgr-underline-color', 'sgr-reset',
    'sgr-closure', 'erase-line', 'char-edit', 'erase-display', 'line-edit', 'charsets', 'scrollback', 'saved-reset', 'erase-attrs-region', 'attr-rect',
    'cursor-qual', 'erase-chars-history', 'selective-erase', 'rect-edit', 'col-edit', 'rect-checksum', 'lr-margins', 'title-stack'];
const layers = await Promise.all(names.map(name => import(name === 'grading' ? './grading-collector.mjs' : './' + name + '-grading-collector.mjs')
    .then(module => Object.values(module).find(value => typeof value === 'function' && value.name.startsWith('adapt')))));
console.log('layer names:', layers.map(f => f.name).join(','));
let text = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
for (const adapt of [...layers, adaptOscTitleCollector, adaptReflowCollector]) text = adapt(text);
const file = new URL(`../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/ntilde-rfl-dbg-${process.pid}.mjs`, import.meta.url);
writeFileSync(file, text);
try { const module = await import(file.href); console.log('imported ok, probeIds:', module.ntildeReflowAdapter.probeIds); }
catch (error) { writeFileSync('/tmp/broken-derived.mjs', text); console.log('IMPORT FAILED:', error.message); console.log(error.stack.split('\n').slice(0, 6).join('\n')); }
rmSync(file, { force: true });
