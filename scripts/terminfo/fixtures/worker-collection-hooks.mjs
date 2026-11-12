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
for (const [file, name] of [['cells', 'Cells'], ['sgr', 'Sgr'], ['rgb', 'Rgb'], ['rendition', 'Rendition'], ['cursor', 'Cursor'], ['pixels-cases', 'Pixels'], ['color-cases', 'Colors'], ['history-cases', 'History'], ['resize-cases', 'Resize'], ['saved-cases', 'Saved'], ['metadata-cases', 'Metadata'], ['static-images-collector', 'StaticImages'], ['query-collector', 'Queries']]) {
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
code.set(new URL('scrollback-collector.mjs', root).href, log + `export async function collectScrollbackGrading() { record('Scrollback'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('charsets-collector.mjs', root).href, log + `export async function collectCharsetsGrading() { record('Charsets'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('erase-line-collector.mjs', root).href, log + `export async function collectEraseLineGrading() { record('EraseLine'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('scrollback-collector.mjs', root).href, log + `export async function collectScrollbackGrading() { record('Scrollback'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('char-edit-collector.mjs', root).href, log + `export async function collectCharEditGrading() { record('CharEdit'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('scrollback-collector.mjs', root).href, log + `export async function collectScrollbackGrading() { record('Scrollback'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('erase-display-collector.mjs', root).href, log + `export async function collectEraseDisplayGrading() { record('EraseDisplay'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('scrollback-collector.mjs', root).href, log + `export async function collectScrollbackGrading() { record('Scrollback'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('line-edit-collector.mjs', root).href, log + `export async function collectLineEditGrading() { record('LineEdit'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('saved-reset-collector.mjs', root).href, log + `export async function collectSavedResetGrading() { record('SavedReset'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('erase-attrs-region-collector.mjs', root).href, log + `export async function collectEraseAttrsRegionGrading() { record('EraseAttrsRegion'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('attr-rect-collector.mjs', root).href, log + `export async function collectAttrRectGrading() { record('AttrRect'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('cursor-qual-collector.mjs', root).href, log + `export async function collectCursorQualGrading() { record('CursorQual'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('erase-chars-history-collector.mjs', root).href, log + `export async function collectEraseCharsHistoryGrading() { record('EraseCharsHistory'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('selective-erase-collector.mjs', root).href, log + `export async function collectSelectiveEraseGrading() { record('SelectiveErase'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('rect-edit-collector.mjs', root).href, log + `export async function collectRectEditGrading() { record('RectEdit'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('col-edit-collector.mjs', root).href, log + `export async function collectColEditGrading() { record('ColEdit'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('rect-checksum-collector.mjs', root).href, log + `export async function collectRectChecksumGrading() { record('RectChecksum'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('input-collector.mjs', root).href, log + `export async function collectInput() { record('Input'); return { status: 'complete', controls: {}, restored: '', geometry: { rows: 12, cols: 120 } }; }`);
code.set(new URL('routed-input-collector.mjs', root).href, log + `export function collectRoutedInputPinned() { record('RoutedInput'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('lr-margins-collector.mjs', root).href, log + `export async function collectLrMarginsGrading() { record('LrMargins'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('core-gaps.mjs', root).href, log + `export async function collectCoreGaps() { record('CoreGaps'); return { schemaVersion: 1, cases: [] }; }`);
code.set(new URL('osc133-collector.mjs', root).href, log + `export async function collectOsc133() { record('Osc133'); return { status: 'complete', run: { schemaVersion: 1, adapterId: 'stub', probeIds: [], experiments: [], pinned: [] } }; }`);
code.set(new URL('color-reset-collector.mjs', root).href, log + `export async function collectColorResetGrading() { record('ColorReset'); return { schemaVersion: 1, adapterId: 'stub', probeIds: [], cases: [] }; }`);
code.set(new URL('core-text-cursor.mjs', root).href, log + `export async function collectTextCursor() { record('TextCursor'); return { schemaVersion: 1, cases: [] }; }`);
code.set(new URL('core-erase.mjs', root).href, log + `export async function collectErase() { record('CoreErase'); return { schemaVersion: 1, cases: [] }; }`);
code.set(new URL('core-tab-stops.mjs', root).href, log + `export async function collectTabStops() { record('TabStops'); return { schemaVersion: 1, cases: [] }; }`);
registerHooks({ load(url, context, next) {
    if (code.has(url)) return { format: 'module', source: code.get(url), shortCircuit: true };
    return next(url, context);
} });

code.set(new URL('core-sgr-raster.mjs', root).href, log + `export async function collectSgrRaster() { record('SgrRaster'); return { schemaVersion: 1, cases: [] }; }`);
code.set(new URL('core-wrap-collector.mjs', root).href, log + `export async function collectWrap() { record('CoreWrap'); return { schemaVersion: 1, cases: [] }; }`);
code.set(new URL('title-stack-collector.mjs', root).href, log + `export async function collectTitleStack() { record('TitleStack'); return { status: 'complete', run: { schemaVersion: 1, adapterId: 'stub', probeIds: [], experiments: [], pinned: [] } }; }`);
code.set(new URL('osc-title-collector.mjs', root).href, log + `export async function collectOscTitle() { record('OscTitle'); return { status: 'complete', run: { schemaVersion: 1, adapterId: 'stub', probeIds: [], experiments: [], pinned: [] } }; }`);
code.set(new URL('core-reverse-wide.mjs', root).href, log + `export async function collectReverseWide() { record('ReverseWide'); return { schemaVersion: 1, cases: [] }; }`);
code.set(new URL('osc78-collector.mjs', root).href, log + `export async function collectOsc78() { record('Osc78'); return { status: 'complete', run: { schemaVersion: 1, adapterId: 'stub', probeIds: [], experiments: [], pinned: [] } }; }`);
code.set(new URL('ascii-reflow-collector.mjs', root).href, log + `export async function collectReflowGrading() { record('Reflow'); return { status: 'complete', run: { schemaVersion: 1, adapterId: 'stub', probeIds: [], feeds: [], barriers: [], stages: [], reads: [], pinned: {} } }; }`);
