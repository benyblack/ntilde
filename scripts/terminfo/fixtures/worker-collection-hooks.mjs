// Test-only child preload: exercise worker orchestration without an App, PTY or production bypass.
import { readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
const root = new URL('../', import.meta.url);
const log = String.raw`import { appendFileSync } from 'node:fs'; const record = value => appendFileSync(process.env.NTILDE_WORKER_TEST_LOG, value + '\n');`;
const code = new Map();
code.set(new URL('ownership.mjs', root).href, log + `
    export function assertPrivateFile() { record('private-config'); }
    export async function verifyNtildeOutput() { record('proof'); return { tabTty: 'test-only' }; }
    export function processRecord() { return { tty: 'test-only' }; }
    export async function requestOwner(_, kind) { record(kind); }
`);
const outputSource = readFileSync(new URL('terminal-output.mjs', root), 'utf8');
const restorer = outputSource.slice(outputSource.indexOf('export function ownedTerminalRestorer('), outputSource.indexOf('// Darwin canonical-mode'));
code.set(new URL('terminal-output.mjs', root).href, log + restorer + `export function prepareOwnedOutput() { record('output-setup'); return () => record('output-restore'); } export function restoreOwnedTerminalState() { record('exact-original-mode'); }`);
code.set(new URL('terminal-fence.mjs', root).href, log + `export function terminalFence() { record('fence'); return { dispose() { record('dispose'); }, retireInput() { record('retire'); } }; }`);
for (const [file, name] of [['cells', 'Cells'], ['sgr', 'Sgr'], ['rgb', 'Rgb'], ['rendition', 'Rendition'], ['cursor', 'Cursor'], ['pixels-cases', 'Pixels'], ['color-cases', 'Colors'], ['history-cases', 'History'], ['resize-cases', 'Resize'], ['saved-cases', 'Saved'], ['metadata-cases', 'Metadata'], ['input-collector', 'Input'], ['static-images-collector', 'StaticImages'], ['query-collector', 'Queries']]) {
    code.set(new URL(file + '.mjs', root).href, log + `export async function collect${name}() { record('${name}'); }`);
}
code.set(new URL('editing-cases.mjs', root).href, log + `export async function collectErase() { record('Erase'); } export async function collectEditing() { record('Editing'); }`);
code.set(new URL('ascii-sgr-collector.mjs', root).href, log + `export async function collectAsciiSgrGrading() { record('Grading'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-colors-collector.mjs', root).href, log + `export async function collectSgrColorsGrading() { record('SgrColors'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-rgb-collector.mjs', root).href, log + `export async function collectSgrRgbGrading() { record('SgrRgb'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-attrs-collector.mjs', root).href, log + `export async function collectSgrAttrsGrading() { record('SgrAttrs'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-palette-collector.mjs', root).href, log + `export async function collectSgrPaletteGrading() { record('SgrPalette'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('truecolor-literal-finding-collector.mjs', root).href, log + `export async function collectTruecolorFindingGrading() { record('TruecolorFinding'); return { schemaVersion: 1, suiteId: 'stub', caseIds: [], cases: [] }; }`);
code.set(new URL('sgr-truecolor-collector.mjs', root).href, log + `export async function collectSgrTruecolorGrading() { record('SgrTruecolor'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-underline-collector.mjs', root).href, log + `export async function collectSgrUnderlineGrading() { record('SgrUnderline'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-underline-color-collector.mjs', root).href, log + `export async function collectSgrUnderlineColorGrading() { record('SgrUnderlineColor'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-underline-color-resolved-collector.mjs', root).href, log + `export async function collectUnderlineResolvedGrading() { record('UnderlineResolved'); return { schemaVersion: 1, suiteId: 'stub', caseIds: [], cases: [] }; }`);
code.set(new URL('sgr-reset-collector.mjs', root).href, log + `export async function collectSgrResetGrading() { record('SgrReset'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('sgr-closure-collector.mjs', root).href, log + `export async function collectSgrClosureGrading() { record('SgrClosure'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
registerHooks({ load(url, context, next) {
    if (code.has(url)) return { format: 'module', source: code.get(url), shortCircuit: true };
    return next(url, context);
} });
