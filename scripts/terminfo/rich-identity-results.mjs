import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';
import { cursorReportQuery, parseCursorReportFrame } from './cursor-report.mjs';
import { digest, pinnedJson } from './modern-inputs.mjs';
import { pinnedBundle } from './unicode-publication-inputs.mjs';
import { pinned176, pinned666 } from './rich-identity/pinned-callbacks.mjs';

const qa = fileURLToPath(new URL('../../docs/qa/terminfo/', import.meta.url));
const publication = join(qa, '2026-11-27-rich-identity');
const manifestSha = 'c674dc11c1f5de869271dae7360c7a7726514736a4fdc593a6e56272768dd189';
const bundle = join(qa, '2026-11-16-shell-integration-aggregates', 'terminfo-dev-4.0.0.bundle.mjs.gz');
const baselineSha = 'd4418484f935ac1a534eaf0b0368d2e5bb174974ae92f2c9c9a4ff2f8905a82b';
const richSha = '483c7e141c43ac483e8698afe608473680a4ac77c1e4921ea8416ddef2d0caa0';
const revision = /^[a-f0-9]{40}$/;
const sha256 = /^[a-f0-9]{64}$/;
const leaseId = /^[a-f0-9]{32}$/;
const appPhases = ['pinned176', 'pinned666', 'baseline', 'request176', 'reset176', 'unregistered666', 'request666', 'reset666', 'control', 'restore'];
const footPhases = ['baseline', 'pinned176', 'resetBel', 'request176', 'reset176'];
const vtePhases = ['baseline', 'pinned666', 'unregistered666', 'wrongTerminator', 'request666', 'query666', 'reset666'];
const key = 'vte.container.name';
const ids = {
    pinned176: 'extensions.osc176-app-id',
    pinned666: 'extensions.osc666-termprop',
};
const hex = text => Buffer.from(text, 'ascii').toString('hex');
const fromHex = text => {
    assert.match(text, /^(?:[a-f0-9]{2})*$/u, 'invalid retained hex');
    return Buffer.from(text, 'hex').toString('latin1');
};
const readJson = (directory, name) => JSON.parse(readFileSync(join(directory, name)));
const spec = {
    request176: '\x1b]176;terminfo-test\x1b\\',
    reset176: '\x1b]176;\x1b\\',
    unregistered666: '\x1b]666;test-prop=value\x1b\\',
    request666: '\x1b]666;vte.container.name=ntilde-qa\x1b\\',
    reset666: '\x1b]666;vte.container.name\x1b\\',
};

function span(text, start, end) {
    const first = text.indexOf(start);
    const last = text.indexOf(end, first + start.length);
    assert.ok(first >= 0 && last > first && !text.includes(start, first + start.length), 'pinned source span differs');
    return text.slice(first, last);
}

export function probes(archive = readFileSync(bundle)) {
    const bytes = gunzipSync(archive, { maxOutputLength: 1_000_000 });
    assert.equal(digest(bytes), pinnedBundle, 'pinned probe bundle digest');
    const text = bytes.toString('utf8');
    const helpers = span(text, 'function probe(id, termless, term, termObservationEvidence) {', '\nfunction readHyperlinkMetadata(')
        + '\n' + span(text, 'function unmeasuredCellResult(', '\nfunction sgrProbe(');
    const extracts = Object.entries(ids).map(([phase, id]) => {
        const definition = span(text, '    probe("' + id + '",', ',\n    probe("').trimStart();
        return 'export const ' + phase + ' = ' + definition + ';';
    });
    const header = '// Extracted from the digest-pinned terminfo.dev@4.0.0 bundle. Verified byte for byte during replay.\n';
    const module = readFileSync(new URL('./rich-identity/pinned-callbacks.mjs', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
    assert.equal(module, header + helpers + '\n' + extracts.join('\n') + '\n', 'pinned callback module differs');
    return { pinned176, pinned666 };
}

export async function replayPinned(probe, writes, reply) {
    let consumed = 0;
    const result = await probe.term({
        write(sequence) {
            assert.equal(sequence, writes[consumed++], 'exact pinned write order');
        },
        queryCursorPosition() {
            assert.equal(writes[consumed++], cursorReportQuery, 'pinned cursor query');
            return reply;
        },
    });
    assert.equal(consumed, writes.length, 'unused pinned exchange bytes');
    return structuredClone(result);
}

function windows(tree) {
    const own = tree.type === 'con' && (tree.shell || Number.isInteger(tree.window)) ? [tree] : [];
    return own.concat([...tree.nodes, ...tree.floating_nodes].flatMap(windows));
}

function sameWindow(sample, terminal, admission) {
    const matches = windows(sample.tree).filter(window => window.id === admission.id || window.pid === terminal.pid);
    assert.equal(matches.length, 1, 'missing or ambiguous disposable window');
    const [window] = matches;
    for (const field of ['id', 'pid', 'shell', 'window']) assert.equal(window[field], admission[field], 'disposable window changed');
    assert.equal(window.pid, terminal.pid, 'wrong window process');
    assert.ok(Object.hasOwn(window, 'app_id'), 'Sway app-ID field unavailable');
    return window;
}

function exchange(sample, writes, previousEnd, cursor, expectedColor = '') {
    const { exchange: wire } = sample;
    assert.equal(wire.stale, null, 'stale PTY input before write');
    assert.deepEqual(wire.writes, writes.map(hex), 'changed PTY writes');
    const start = BigInt(wire.start);
    const end = BigInt(wire.end);
    assert.ok(start > previousEnd && end > start && end - start <= 2_100_000_000n, 'invalid PTY window timing');
    assert.ok(wire.chunks.length > 0 && wire.chunks.length <= 12, 'missing or oversized PTY capture');
    let last = start;
    let raw = '';
    for (const chunk of wire.chunks) {
        const at = BigInt(chunk.at);
        assert.ok(at >= last && at <= end && chunk.hex.length > 0, 'invalid PTY chunk timing');
        last = at;
        raw += fromHex(chunk.hex);
    }
    assert.ok(raw.startsWith(expectedColor), 'foreground state control failed');
    const { barrier, reply } = parseCursorReportFrame(raw.slice(expectedColor.length)) ?? {};
    assert.ok(barrier, 'missing independent primary DA delivery fence');
    if (cursor) assert.deepEqual([reply?.row, reply?.col], cursor, 'cursor position control failed');
    return { end, reply, barrier, raw: hex(raw) };
}

function terminalRecord(run, name, phases) {
    const record = run[name];
    const { terminal, child, admission, termios, tty } = record;
    assert.deepEqual(Object.keys(record.samples), phases, 'wrong ' + name + ' phase membership');
    assert.equal(terminal.parent, run.coordinator.pid, 'wrong terminal parent');
    assert.equal(child.parent, terminal.pid, 'wrong PTY child parent');
    assert.equal(terminal.uid, child.uid);
    assert.equal(child.session, child.pid);
    assert.equal(child.argv[3], '/sources/pty_child.py');
    assert.equal(child.argv[5], name + '-' + run.runId);
    assert.equal(child.argv[6], String(run.coordinator.pid));
    assert.match(tty.path, /^\/dev\/pts\/\d+$/u);
    assert.ok(tty.inode > 0 && tty.device > 0, 'missing device identity');
    assert.ok(record.ptyGone && record.workerSocketGone && !windows(record.afterTree).some(window => window.pid === terminal.pid),
        'missing PTY or window cleanup');
    assert.equal(run.exits[name].pid, terminal.pid);
    const normalExits = name === 'foot' ? [0] : [0, -15];
    assert.ok(normalExits.includes(run.exits[name].returncode), 'terminal did not shut down normally');
    assert.deepEqual(termios.before, termios.after, 'PTY settings were not restored');
    assert.ok(termios.before.length === 7 && termios.before[6].length > 0, 'empty PTY settings');
    assert.equal(admission.pid, terminal.pid);
    assert.match(terminal.sha256, sha256);
    return record;
}

function verifyX11State(window, sample, app) {
    assert.equal(window.app_id, null, 'unexpected native app-ID on XWayland window');
    assert.ok(window.window > 0 && window.window_properties.class === app.admission.window_properties.class);
    assert.ok(sample.xprop.startsWith('_NET_WM_PID(CARDINAL) = ' + app.terminal.pid + '\n'), 'X11 window PID differs');
    assert.equal(sample.xprop, app.samples.baseline.xprop, 'X11 host state changed');
}

function appPhase(app, phase, sample, writes, previousEnd) {
    const window = sameWindow(sample, app.terminal, app.admission);
    const { pane } = app;
    assert.deepEqual([sample.pane.paneId, sample.pane.rows, sample.pane.cols], [pane.paneId, pane.rows, pane.cols],
        'pane identity or geometry drifted');
    assert.deepEqual([sample.pane.width, sample.pane.height], [app.arranged.width, app.arranged.height], 'App viewport size drifted');
    let cursor = null;
    let expectedColor = '';
    if (!Object.hasOwn(ids, phase)) {
        const control = phase === 'control';
        const title = control ? 'identity-control' : 'identity-baseline';
        const foreground = control ? '6565/4343/2121' : '1212/3434/5656';
        expectedColor = '\x1b]10;rgb:' + foreground + '\x1b\\';
        cursor = control ? [4, 7] : [3, 5];
        assert.equal(sample.pane.oscTitle, title, 'raw title state changed');
        assert.ok(sample.pane.tabText.includes(title), 'tab state observation failed');
    }
    const wire = exchange(sample, writes, previousEnd, cursor, expectedColor);
    if (Object.hasOwn(spec, phase)) assert.equal(fromHex(sample.exchange.writes[0]), spec[phase], 'wrong effect fixture');
    if (window.shell === 'xwayland') verifyX11State(window, sample, app);
    const summary = { cpr: wire.reply?.raw ?? null, da: wire.barrier, rawHex: wire.raw, pane: sample.pane,
        appId: window.app_id, shell: window.shell, x11: sample.xprop ?? null };
    return { wire, summary };
}

async function verifyApp(run, plan, pinned) {
    const app = terminalRecord(run, 'app', appPhases);
    const { pane, markerScreen, terminal } = app;
    assert.ok(pane.kind === 'local' && pane.isActive && pane.profileName === 'OSC identity QA'
        && pane.rows >= 4 && pane.cols >= 7, 'expected singleton local pane');
    assert.deepEqual([app.arranged.width, app.arranged.height], [1280, 800], 'missing compositor-sized App viewport');
    assert.deepEqual([app.arranged.paneId, app.arranged.rows, app.arranged.cols], [pane.paneId, pane.rows, pane.cols],
        'arranged App pane geometry differs');
    assert.equal(markerScreen.lines[1], 'app-' + run.runId);
    assert.deepEqual([markerScreen.rows, markerScreen.cols], [pane.rows, pane.cols]);
    assert.equal(terminal.argv.at(-1), '--ntilde-terminfo-owner=' + run.runId);
    assert.ok(terminal.exe.endsWith('/sidecar/Ntilde'));
    for (const name of ['Ntilde', 'Ntilde.dll', 'libcoreclr.so', 'librusty_pty.so']) assert.match(app.files[name], sha256);
    assert.equal(terminal.sha256, app.files.Ntilde, 'mapped executable differs from publish tree');

    let previousEnd = 0n;
    const replies = {};
    const samples = Object.fromEntries(appPhases.map(phase => {
        const sample = app.samples[phase];
        const { wire, summary } = appPhase(app, phase, sample, plan.app[phase], previousEnd);
        previousEnd = wire.end;
        replies[phase] = wire.reply;
        return [phase, summary];
    }));
    const pinnedResults = await Promise.all(Object.entries(pinned).map(async ([phase, probe]) => {
        assert.equal(plan.app[phase].at(-1), '\x1b[c', 'pinned exchange needs independent DA');
        const writes = app.samples[phase].exchange.writes.slice(0, -1).map(fromHex);
        return [phase, await replayPinned(probe, writes, replies[phase])];
    }));
    return { pinned: Object.fromEntries(pinnedResults), samples };
}

function verifyFoot(run, plan) {
    const { samples, terminal, admission } = terminalRecord(run, 'foot', footPhases);
    assert.equal(terminal.exe, '/usr/bin/foot');
    assert.ok(terminal.argv.includes('--app-id=qa.foot.baseline'));
    assert.equal(admission.shell, 'xdg_shell');
    assert.equal(admission.app_id, 'qa.foot.baseline');
    let previousEnd = 0n;
    return Object.fromEntries(footPhases.map(phase => {
        const sample = samples[phase];
        const window = sameWindow(sample, terminal, admission);
        const wire = exchange(sample, plan.foot[phase], previousEnd, phase === 'baseline' ? [3, 5] : [1, 1]);
        previousEnd = wire.end;
        if (phase === 'pinned176') assert.deepEqual(plan.foot[phase].slice(0, 3), plan.app[phase].slice(0, 3));
        if (Object.hasOwn(spec, phase)) assert.equal(fromHex(sample.exchange.writes[0]), spec[phase]);
        const requested = ['pinned176', 'request176'].includes(phase);
        assert.equal(window.app_id, requested ? 'terminfo-test' : 'qa.foot.baseline', 'foot compositor positive control failed');
        return [phase, { appId: window.app_id, rawHex: wire.raw }];
    }));
}

function verifyVte(run, plan) {
    const { version, registered, samples } = run.vte;
    assert.deepEqual(version, [0, 80, 1], 'VTE parser contract version');
    assert.deepEqual(registered, [
        'vte.container.name', 'vte.container.runtime', 'vte.container.uid', 'vte.cwd', 'vte.cwf',
        'vte.icon.color', 'vte.icon.image', 'vte.progress.hint', 'vte.progress.value',
        'vte.shell.postexec', 'vte.shell.precmd', 'vte.shell.preexec', 'xterm.title',
    ], 'VTE property registration differs');
    assert.deepEqual(Object.keys(samples), vtePhases, 'wrong VTE reference phases');
    for (const phase of vtePhases) {
        const sample = samples[phase];
        assert.equal(sample.feed, hex(plan.vte[phase]), 'reference feed changed');
        if (Object.hasOwn(spec, phase)) assert.equal(fromHex(sample.feed), spec[phase]);
        if (phase === 'pinned666') assert.equal(fromHex(sample.feed), plan.app[phase][1]);
        if (phase === 'wrongTerminator') assert.equal(fromHex(sample.feed), spec.request666.slice(0, -2) + '\x07');
        const set = ['request666', 'query666'].includes(phase);
        assert.equal(sample.containerName, set ? 'ntilde-qa' : null, 'VTE string state control failed');
        assert.deepEqual(sample.changes, ['request666', 'reset666'].includes(phase) ? [key] : [], 'VTE property signal control failed');
    }
    return { version, registered, samples };
}

function verifyLease(container, audit, runId) {
    assert.match(container.id, sha256);
    assert.match(container.image, /^sha256:[a-f0-9]{64}$/);
    assert.equal(container.runId, runId);
    assert.equal(container.name, 'ntilde-identity-' + runId);
    assert.equal(container.status, 'exited');
    assert.equal(container.exitCode, 0);
    assert.equal(container.isolation.network, 'none');
    assert.equal(container.isolation.user, '1000:1000');
    assert.deepEqual(container.isolation.mounts.map(mount => mount.destination).toSorted((a, b) => a.localeCompare(b)),
        ['/inputs/sidecar.tar', '/sources']);
    assert.ok(container.isolation.mounts.every(mount => mount.writable === false));
    assert.deepEqual(audit.map(event => event.event), ['acquired', 'launch-attempt', 'cleanup-verified', 'released'], 'incomplete desktop lease cleanup');
    const [acquired, launch, cleanup, release] = audit;
    assert.ok(audit.every(event => event.lease === runId && event.pid === acquired.pid));
    assert.equal(acquired.entrypoint, 'terminfo-rich-identity-owned.mjs');
    assert.equal(cleanup.by, 'owned-container-removed');
    assert.equal(release.launch, 'attempted');
    const times = [acquired.at, launch.at, container.startedAt, container.removedAt, cleanup.at, release.at].map(Date.parse);
    assert.ok(times.every((time, i) => Number.isFinite(time) && (i === 0 || time >= times[i - 1])), 'container cleanup outside desktop lease');
}

function sourceInputs(directory) {
    const identity = readJson(directory, 'identity.json');
    assert.match(identity.scriptsRevision, revision);
    assert.match(identity.appSourceRevision, revision);
    assert.match(identity.appSourceArchiveSha256, sha256);
    assert.match(identity.sidecarSha256, sha256);
    const names = ['Dockerfile', 'collect.py', 'desktop-lock.mjs', 'plan.json', 'pty_child.py', 'terminfo-rich-identity-owned.mjs', 'vte_reference.py'];
    const source = join(directory, 'sources');
    assert.deepEqual(readdirSync(source).toSorted((a, b) => a.localeCompare(b, 'en')),
        names.toSorted((a, b) => a.localeCompare(b, 'en')));
    assert.equal(Object.keys(identity.files).length, names.length);
    for (const name of names) assert.equal(identity.files[name], digest(readFileSync(join(source, name))), 'collector source binding ' + name);
    return { identity, plan: readJson(source, 'plan.json') };
}

export async function verifyRun(directory) {
    const { identity, plan } = sourceInputs(directory);
    const run = readJson(directory, 'observation.json');
    const container = readJson(directory, 'container.json');
    const audit = readJson(directory, 'desktop-lease.json');
    assert.equal(run.schemaVersion, 1);
    assert.ok(!Object.hasOwn(run, 'error') && run.fixtureRemoved === true, 'failed or incomplete collection');
    assert.match(run.runId, leaseId);
    verifyLease(container, audit, run.runId);
    assert.equal(run.compositor.exe, '/usr/bin/sway');
    assert.match(run.compositor.sha256, sha256);
    assert.equal(run.compositor.parent, run.coordinator.pid);
    assert.equal(run.exits.sway.pid, run.compositor.pid);
    assert.equal(run.exits.sway.returncode, 0);
    assert.equal(run.exits.vte.returncode, 0);
    assert.equal(run.sockets.length, 3);
    assert.deepEqual(Object.keys(run.socketsGone), run.sockets.map(socket => socket.path));
    assert.ok(Object.values(run.socketsGone).every(value => value === true), 'display sockets remain');
    const foot = verifyFoot(run, plan);
    const app = await verifyApp(run, plan, probes());
    assert.deepEqual(run.vte, readJson(directory, 'vte.json'), 'VTE observer bytes differ from recorded projection');
    const vte = verifyVte(run, plan);
    const appRevision = run.app.version.split('+')[1];
    assert.match(appRevision, revision);
    assert.equal(appRevision, identity.appSourceRevision, 'App embedded revision differs from verified source archive');
    const baseId = app.samples.baseline.appId;
    const native = app.samples.baseline.shell === 'xdg_shell' && typeof baseId === 'string' && baseId !== 'terminfo-test';
    const xwayland = app.samples.baseline.shell === 'xwayland';
    const measuredIds = [app.samples.pinned176.appId, app.samples.request176.appId, app.samples.reset176.appId];
    let appIdEffect = 'inconclusive';
    if (xwayland) appIdEffect = 'inapplicable';
    if (native && measuredIds.every(value => value === baseId)) appIdEffect = 'unsupported';
    if (native && measuredIds[0] === 'terminfo-test' && measuredIds[1] === 'terminfo-test' && measuredIds[2] === baseId) appIdEffect = 'supported';
    return { runId: run.runId, appRevision, appVersion: run.app.version, appExecutableSha256: run.app.files.Ntilde,
        appAssemblySha256: run.app.files['Ntilde.dll'], environment: run.environment, ...identity, container, lease: audit,
        app, foot, vte, appIdEffect, appWindow: { id: run.app.admission.id, pid: run.app.terminal.pid, xid: run.app.admission.window },
        footWindow: { id: run.foot.admission.id, pid: run.foot.terminal.pid } };
}

export async function withMeasurement(callback) {
    const manifest = pinnedJson(join(publication, 'manifest.json'), manifestSha);
    const directory = mkdtempSync(join(tmpdir(), 'ntilde-rich-identity-'));
    try {
        extractOwnedArchive(publication, manifest, directory, join(publication, 'owned-run.zip'));
        return await callback(directory, manifest);
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}

export async function buildPublication() {
    const measured = await withMeasurement(async (directory, manifest) => {
        const run = await verifyRun(directory);
        assert.equal(manifest.schemaVersion, 1);
        for (const field of ['runId', 'appRevision', 'appSourceArchiveSha256', 'scriptsRevision']) assert.equal(manifest[field], run[field]);
        return run;
    });
    const baseline = pinnedJson(join(qa, '2026-11-09-stock-baseline.json'), baselineSha);
    const rich = pinnedJson(join(qa, '2026-11-26-rich-tui', 'requirements-v1.json'), richSha);
    assert.equal(baseline.source.pinnedBundleSha256, pinnedBundle);
    const cases = Object.fromEntries(Object.entries(ids).map(([phase, id]) => {
        assert.equal(rich.rows.filter(row => row.id === id).length, 1);
        return [id, { pinned: measured.app.pinned[phase], stock: baseline.outcomes[id] }];
    }));
    Object.assign(cases[ids.pinned176], { contract: 'ntilde-wayland-osc176-app-id-v1', effect: measured.appIdEffect });
    Object.assign(cases[ids.pinned666], { contract: 'ntilde-vte-osc666-termprop-v1', fixtureEffect: 'inapplicable',
        registeredPropertyEffect: 'inconclusive', observerGap: 'Ntilde exposes no VTE termprop getter or equivalent registered property state.' });
    return { schemaVersion: 1, pinnedPackage: 'terminfo.dev@4.0.0', pinnedBundleSha256: pinnedBundle, cases, measured };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    const [flag, directory] = process.argv.slice(2);
    if (flag === '--verify-run' && directory) {
        console.log(JSON.stringify(await verifyRun(directory), null, 2));
    } else {
        assert.ok(['--write', '--check'].includes(flag) && !directory, 'expected --check, --write or --verify-run PATH');
        const report = JSON.stringify(await buildPublication(), null, 2) + '\n';
        const target = join(publication, 'report.json');
        if (flag === '--check') assert.equal(readFileSync(target, 'utf8').replaceAll('\r\n', '\n'), report);
        else writeFileSync(target, report);
    }
}
