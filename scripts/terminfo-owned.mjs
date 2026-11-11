#!/usr/bin/env node
// Trusted BCL-only launcher. Capture measured source bytes before importing any of them.
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const coreGapsReferencePaths = Object.freeze({
    "core-icon-vt.cs": "src/Ntilde.VT/AnsiParser.Titles.cs",
    "core-icon-capture.cs": "src/Ntilde.App/AgentHost/AgentPaneMetadataCapture.cs",
    "core-icon-contract.cs": "src/Ntilde.AgentHost.Contracts/PaneMetadataContracts.cs",
    "core-icon-window.cs": "src/Ntilde.App/MainWindow.AgentMetadata.cs",
});
const privateClipboardReferencePaths = Object.freeze({
    "clipboard-app-program.cs": "src/Ntilde.App/Program.cs",
    "clipboard-app.cs": "src/Ntilde.App/App.axaml.cs",
    "clipboard-private-mode.cs": "src/Ntilde.App/Shell/Clipboard/PrivateClipboardQaMode.cs",
    "clipboard-disposable-fixture.cs": "src/Ntilde.App/Shell/Clipboard/DisposableClipboardFixture.cs",
    "clipboard-main-window.cs": "src/Ntilde.App/MainWindow.ClipboardFixture.cs",
    "clipboard-main-window-resize.cs": "src/Ntilde.App/MainWindow.AgentResize.cs",
    "clipboard-pane.cs": "src/Ntilde.App/Controls/TerminalPane.Clipboard.cs",
    "clipboard-pane-session.cs": "src/Ntilde.App/Controls/TerminalPane.Session.cs",
    "clipboard-pane-lifecycle.cs": "src/Ntilde.App/Controls/TerminalPane.Lifecycle.cs",
    "clipboard-pane-settings.cs": "src/Ntilde.App/Controls/TerminalPane.axaml.cs",
    "clipboard-settings.cs": "src/Ntilde.App/Shell/TerminalSettings.cs",
    "clipboard-agent-service.cs": "src/Ntilde.App/AgentHost/AgentHostService.cs",
    "clipboard-agent-fixture.cs": "src/Ntilde.App/AgentHost/AgentHostService.ClipboardFixture.cs",
    "clipboard-agent-executor.cs": "src/Ntilde.App/AgentHost/IAgentActionExecutor.cs",
    "clipboard-contract.cs": "src/Ntilde.AgentHost.Contracts/ClipboardFixture.cs",
    "clipboard-session-contract.cs": "src/Ntilde.AgentHost.Contracts/SessionContracts.cs",
    "clipboard-agent-registration.cs": "src/Ntilde.App/AgentHost/AgentSessionRegistration.cs",
    "clipboard-vt-osc.cs": "src/Ntilde.VT/AnsiParser.Osc.cs",
    "clipboard-vt-csi.cs": "src/Ntilde.VT/AnsiParser.Csi.cs",
    "clipboard-agent-registry.cs": "src/Ntilde.App/AgentHost/AgentSessionRegistry.cs",
    "clipboard-agent-protocol.cs": "src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs"
});
// SGR-RGB render-resolution reference inputs (policy v9): the App-side resolved-color
// projection this batch measures, captured as nonexecuted reference bytes. Files the
// clipboard scope already captures are not duplicated: one byte set per file.
const sgrRgbReferencePaths = Object.freeze({
    "sgr-rgb-resolved-color-snapshot.cs": "src/Ntilde.App/AgentHost/ResolvedColorSnapshot.cs",
    "sgr-rgb-draw-operation.cs": "src/Ntilde.App/Shell/TerminalDrawOperation.cs",
    "sgr-rgb-render-snapshots.cs": "src/Ntilde.VT/RenderSnapshots.cs"
});
/** Map admitted reference aliases or established script basenames without reading or executing them. */
function pathFor(root, file) {
    if (Object.hasOwn(coreGapsReferencePaths, file)) return join(root, coreGapsReferencePaths[file]);
    if (Object.hasOwn(privateClipboardReferencePaths, file)) return join(root, privateClipboardReferencePaths[file]);
    if (Object.hasOwn(sgrRgbReferencePaths, file)) return join(root, sgrRgbReferencePaths[file]);
    const relative = file === 'terminfo-owned-runner.mjs' ? file : 'terminfo/' + file;
    return join(root, 'scripts', relative);
}
const policy = 'readonly-owned-source-graph-v39';
function setDirectoryModes(root, mode) {
    for (const entry of readdirSync(root, { withFileTypes: true })) if (entry.isDirectory()) setDirectoryModes(join(root, entry.name), mode);
    chmodSync(root, mode);
}
function removeSnapshot(root) {
    if (!root || !existsSync(root)) return;
    if (!lstatSync(root).isDirectory() || lstatSync(root).isSymbolicLink()) { rmSync(root, { force: true }); return; }
    setDirectoryModes(root, 0o700);
    rmSync(root, { recursive: true, force: true });
}
// The scanner is the sole reviewed pure bootstrap dependency; import only these verified captured bytes.
const sourceScannerSha256 = '27737137fc22aa080b671153573a1f0741ba136fc7e35c0db39b6a3849fda6fd';
async function checkCapturedImports(sources) {
    const scanner = sources.get('source-imports.mjs');
    if (!scanner || digest(scanner) !== sourceScannerSha256) throw new Error('Missing or changed trusted source scanner.');
    const { literalImports } = await import('data:text/javascript;base64,' + scanner.toString('base64'));
    for (const [file, bytes] of sources) {
        if (!file.endsWith('.mjs')) continue;
        for (const target of literalImports(bytes.toString())) {
            if (/^node:[a-z][a-z0-9_/-]*$/.test(target)) continue;
            if (!/^\.\/(?:terminfo\/)?[a-z][a-z0-9-]*\.mjs$/.test(target) || !sources.has(target.split('/').at(-1)))
                throw new Error('Source dependency outside captured scope before import: ' + target);
        }
    }
}
/** Copy regular fixed-scope inputs into an exclusive read-only tree, hash its manifest and check literal dependencies before execution. */
async function capture(root) {
    const input = readFileSync(join(repo, 'scripts/terminfo/owned-source-inventory.json'));
    const inventory = JSON.parse(input), hashes = {}, sources = new Map();
    if (inventory.runnerSourceSha256 !== 'terminfo-owned-runner.mjs' || inventory.sourceInventorySha256 !== 'owned-source-inventory.json') throw new Error('Invalid source inventory.');
    for (const [key, file] of Object.entries(inventory)) {
        if (!/^[a-zA-Z][a-zA-Z0-9]*Sha256$/.test(key) || (!/^[a-z][a-z0-9-]*\.(?:mjs|json)$/.test(file)
            && !Object.hasOwn(coreGapsReferencePaths, file) && !Object.hasOwn(privateClipboardReferencePaths, file) && !Object.hasOwn(sgrRgbReferencePaths, file))) throw new Error('Invalid source inventory entry.');
        const source = pathFor(repo, file), info = lstatSync(source);
        if (!info.isFile() || info.isSymbolicLink()) throw new Error('Expected a regular source file.');
        const bytes = file === 'owned-source-inventory.json' ? input : readFileSync(source);
        const target = pathFor(root, file); mkdirSync(dirname(target), { recursive: true, mode: 0o700 });
        writeFileSync(target, bytes, { flag: 'wx', mode: 0o400 }); hashes[key] = digest(bytes); sources.set(file, bytes);
    }
    const manifest = Buffer.from(JSON.stringify({ schemaVersion: 1, policy, hashes }, null, 2) + '\n');
    writeFileSync(join(root, 'source-snapshot.json'), manifest, { flag: 'wx', mode: 0o400 });
    setDirectoryModes(root, 0o500);
    await checkCapturedImports(sources);
    return digest(manifest);
}
/** Import only the verified captured runner; restore environment/signal handlers and retire the snapshot on every exit. */
async function launch() {
    let root, interrupted;
    const note = signal => { interrupted ??= signal; process.env.NTILDE_TERMINFO_PENDING_SIGNAL = signal; };
    const onInt = () => note('SIGINT'), onTerm = () => note('SIGTERM');
    process.on('SIGINT', onInt); process.on('SIGTERM', onTerm);
    const names = ['NTILDE_TERMINFO_ORIGINAL_REPO', 'NTILDE_TERMINFO_SOURCE_SNAPSHOT', 'NTILDE_TERMINFO_SOURCE_SNAPSHOT_SHA256', 'NTILDE_TERMINFO_PENDING_SIGNAL'];
    const previous = names.map(name => [name, process.env[name]]);
    try {
        root = realpathSync(mkdtempSync(join(tmpdir(), 'ntilde-owned-sources-')));
        const sha256 = await capture(root);
        if (interrupted) throw new Error('Interrupted by ' + interrupted + '.');
        process.env.NTILDE_TERMINFO_ORIGINAL_REPO = repo;
        process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT = root;
        process.env.NTILDE_TERMINFO_SOURCE_SNAPSHOT_SHA256 = sha256;
        delete process.env.NTILDE_TERMINFO_PENDING_SIGNAL;
        await import(pathToFileURL(pathFor(root, 'terminfo-owned-runner.mjs')).href);
        if (interrupted) process.exitCode = 1;
    } finally {
        try { removeSnapshot(root); }
        finally {
            process.removeListener('SIGINT', onInt); process.removeListener('SIGTERM', onTerm);
            for (const [name, value] of previous) { if (value === undefined) delete process.env[name]; else process.env[name] = value; }
        }
    }
}

try { await launch(); }
catch (error) { console.error('[terminfo-owned] ' + error.message); process.exitCode = 1; }
