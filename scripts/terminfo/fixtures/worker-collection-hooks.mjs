// Test-only child preload: exercise worker orchestration without an App, PTY or production bypass.
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
code.set(new URL('terminal-output.mjs', root).href, log + `export function prepareOwnedOutput() { record('output-setup'); return () => record('output-restore'); }`);
code.set(new URL('terminal-fence.mjs', root).href, log + `export function terminalFence() { record('fence'); return { dispose() { record('dispose'); } }; }`);
for (const [file, name] of [['cells', 'Cells'], ['sgr', 'Sgr'], ['rgb', 'Rgb'], ['rendition', 'Rendition'], ['cursor', 'Cursor'], ['pixels-cases', 'Pixels'], ['color-cases', 'Colors'], ['history-cases', 'History'], ['resize-cases', 'Resize'], ['saved-cases', 'Saved'], ['metadata-cases', 'Metadata'], ['input-collector', 'Input'], ['static-images-collector', 'StaticImages'], ['query-collector', 'Queries']]) {
    code.set(new URL(file + '.mjs', root).href, log + `export async function collect${name}() { record('${name}'); }`);
}
code.set(new URL('editing-cases.mjs', root).href, log + `export async function collectErase() { record('Erase'); } export async function collectEditing() { record('Editing'); }`);
registerHooks({ load(url, context, next) {
    if (code.has(url)) return { format: 'module', source: code.get(url), shortCircuit: true };
    return next(url, context);
} });
