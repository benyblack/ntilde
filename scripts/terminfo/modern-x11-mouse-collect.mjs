import { execFile, spawn } from 'node:child_process';
import { createHash, randomUUID, randomBytes } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { isDeepStrictEqual as equal, promisify } from 'node:util';
import { readLinuxOwnedChildFacts, readLinuxProcessFacts } from './linux-process-provider.mjs';
import { actionFence, actionPlan, ids, modePlan, need, replayMouse, x11SocketPath } from './modern-x11-mouse.mjs';

const runFile = promisify(execFile);
const stamp = () => process.hrtime.bigint().toString();
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const readJson = path => JSON.parse(readFileSync(path));
const writeJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { mode: 0o600 });
const mode = 0o700;
/** Hash the actual self-contained publish tree, including the runtime and native PTY library. */
function appFiles(executable) {
    const root = dirname(executable), files = {};
    const visit = path => {
        for (const name of readdirSync(path).toSorted()) {
            const entry = join(path, name), info = lstatSync(entry);
            need(info.uid === process.getuid() && (info.mode & 0o022) === 0, 'mutable or foreign App publish file');
            if (info.isDirectory()) visit(entry);
            else {
                need(info.isFile() && info.size < 64 * 1024 * 1024, 'unexpected App publish entry');
                files[relative(root, entry)] = hash(readFileSync(entry));
            }
        }
    };
    visit(root);
    need(['Ntilde', 'Ntilde.dll', 'librusty_pty.so', 'libcoreclr.so'].every(file => Object.hasOwn(files, file)), 'missing self-contained Linux App files');
    return files;
}

function ownChild(executable, argv, options, logs, label) {
    const child = spawn(executable, argv, options);
    const owned = { child, status: null };
    logs[label] = '';
    for (const pipe of [child.stdout, child.stderr]) pipe.on('data', bytes => {
        logs[label] = (logs[label] + bytes.toString()).slice(-16384);
    });
    owned.exit = new Promise(resolve => {
        child.once('error', error => { owned.status = { error: error.message }; resolve(owned.status); });
        child.once('exit', (code, signal) => { owned.status = { code, signal }; resolve(owned.status); });
    });
    return owned;
}

async function waitFor(check, message, aborted, timeout = 12000) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
        if (aborted?.()) throw new Error('interrupted owned fixture');
        const value = check();
        if (value) return value;
        await delay(30); // NOSONAR: serial readiness polling within the fixture deadline.
    }
    throw new Error(message);
}

async function stop(child) {
    if (!child) return null;
    if (!child.status) {
        child.child.kill('SIGTERM');
        await Promise.race([child.exit, delay(1400)]);
    }
    if (!child.status) {
        child.child.kill('SIGKILL');
        await Promise.race([child.exit, delay(1400)]);
    }
    need(child.status, 'owned direct process exit remains unverified');
    return child.status;
}

// FamilyWild, empty address/display, MIT-MAGIC-COOKIE-1. The controller never retains the cookie.
function authority(path) {
    const cookie = randomBytes(16);
    const protocol = Buffer.from('MIT-MAGIC-COOKIE-1');
    const data = Buffer.concat([Buffer.from([255, 255, 0, 0, 0, 0, 0, protocol.length]),
        protocol, Buffer.from([0, cookie.length]), cookie]);
    writeFileSync(path, data, { flag: 'wx', mode: 0o600 });
}

async function launchDisplay(run) {
    const auth = join(run.scratch, 'Xauthority');
    authority(auth);
    const args = ['-displayfd', '3', '-screen', '0', '1280x800x24', '-dpi', '96', '-nolisten', 'tcp', '-noreset', '-auth', auth];
    run.lease.assertHeld();
    run.lease.noteLaunchAttempt();
    run.display = ownChild('/usr/bin/Xvfb', args, { env: run.env, stdio: ['ignore', 'pipe', 'pipe', 'pipe'] }, run.logs, 'Xvfb');
    const stream = run.display.child.stdio[3];
    let result = '';
    stream.on('data', chunk => { result += chunk.toString(); });
    stream.on('error', error => { run.logs.displayFdError = error.message; });
    const number = await waitFor(() => {
        need(!run.display.status && result.length <= 8, 'Xvfb exited or returned an invalid display number');
        return /^(\d{1,4})\n$/.exec(result)?.[1];
    }, 'Xvfb did not report its allocated display', run.aborted);
    const display = ':' + number;
    run.xSocket = x11SocketPath(display);
    const socket = lstatSync(run.xSocket, { bigint: true });
    need(socket.isSocket() && socket.uid === BigInt(process.getuid()), 'wrong display socket identity');
    const server = readLinuxProcessFacts(run.display.child.pid);
    need(server.ppid === process.pid && server.executable === realpathSync('/usr/bin/Xvfb')
        && equal(server.argv.slice(1), args), 'wrong Xvfb launch identity');
    Object.assign(run.env, { DISPLAY: display, XAUTHORITY: auth });
    run.record.display = { name: run.env.DISPLAY, socket: run.xSocket, socketInode: socket.ino.toString(), server,
        executableSha256: hash(readFileSync(server.executable)) };
}

function profileSettings(run) {
    const profileId = randomUUID();
    const Arguments = run.workerArgs.map(arg => '"' + arg + '"').join(' ');
    return {
        Profiles: [{ Id: profileId, Name: run.profile, Command: run.python, Arguments, StartingDirectory: run.scratch, Type: 0 }],
        DefaultProfileId: profileId, SessionPersistence: 'Off', ShellExitPolicy: 'Never', PaneClosePolicy: 'Always',
        AlternateScroll: false, AutomaticUpdateChecks: false, QuakeModeEnabled: false,
        CommandAssistEnabled: false, CommandAssistShellIntegrationEnabled: false, CommandAssistPowerShellIntegrationEnabled: false,
        AgentAccessObserveEnabled: true, AgentAccessActEnabled: false, AllowOsc52ClipboardWrite: run.clipboard && !run.paste,
    };
}

function native(run, command) {
    return runFile(run.python, ['-I', '-S', '-B', join(run.sources, 'modern-x11-mouse-native.py'), JSON.stringify(command)],
        { env: run.env, encoding: 'utf8', timeout: 5000, maxBuffer: 512 * 1024 }).then(result => JSON.parse(result.stdout));
}

function peer(run, socket, pid, request) { return native(run, { kind: 'rpc', socket, pid, request }); }
function worker(run, request) { return peer(run, run.config.socket, run.child.pid, request); }
function x11(run, request) { return native(run, { serverPid: run.display.child.pid, appPid: run.app.child.pid, ...request }); }

function checkLineage(run, restoring = false) {
    need((restoring || !run.aborted()) && !run.app.status && !run.display.status, 'owned fixture interrupted or an original process exited');
    run.lease.assertHeld();
    const snapshot = readLinuxOwnedChildFacts({ parentPid: run.app.child.pid, childPid: run.child.pid, uid: process.getuid(),
        executable: run.python, argv: [run.python, ...run.workerArgs], runId: run.config.runId });
    const argv = snapshot.parent.argv;
    need(snapshot.parent.ppid === process.pid && snapshot.parent.executable === run.executable && equal(argv, run.appArgv)
        && snapshot.child.pty.descriptors[0].path === run.child.tty, 'wrong direct App or PTY');
    if (run.record.lineage) need(equal(run.record.lineage, snapshot), 'original App or PTY identity changed');
    return snapshot;
}

async function listPane(run) {
    const path = join(run.appData, 'agent.sock');
    const descriptor = readJson(join(run.appData, 'agent-endpoint.json'));
    need(descriptor.version === 1 && descriptor.pid === run.app.child.pid && descriptor.endpoint === path, 'wrong private agent endpoint');
    run.agent = path;
    const { sessions } = await agent(run, 'listSessions');
    const pane = sessions?.[0];
    need(sessions?.length === 1 && pane.kind === 'local' && pane.isActive === true && !pane.windowless
        && pane.profileName === run.profile && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/.test(pane.paneId), 'wrong singleton local pane');
    const { paneId, profileName, rows, cols, kind, isActive } = pane;
    return { paneId, profileName, rows, cols, kind, isActive };
}

async function agent(run, method, params = {}) {
    const response = await peer(run, run.agent, run.app.child.pid, { v: 1, id: 1, method, params });
    need(response.v === 1 && response.id === 1 && response.result && !response.error, 'unacknowledged private agent read');
    return response.result;
}

async function verifyMarker(run, pane) {
    const screen = await agent(run, 'readScreen', { paneId: pane.paneId });
    need(screen.lines?.[0] === run.config.marker && screen.rows === pane.rows && screen.cols === pane.cols, 'owned PTY marker is outside the expected primary pane');
    return screen.lines[0];
}

async function launchPane(run) {
    const appArgv = [run.executable, '--ntilde-terminfo-owner=' + run.config.runId];
    if (!run.clipboard) appArgv.push('--ntilde-terminfo-private-clipboard=' + run.config.runId);
    run.appArgv = appArgv;
    const settings = profileSettings(run);
    run.record.settings = settings;
    writeJson(join(run.appData, 'settings.json'), settings);
    run.app = ownChild(run.executable, appArgv.slice(1), { cwd: dirname(run.executable), env: run.env, stdio: ['ignore', 'pipe', 'pipe'] }, run.logs, 'App');
    await waitFor(() => {
        need(!run.app.status, 'owned App exited before worker admission');
        return existsSync(run.config.ready) && existsSync(join(run.appData, 'agent-endpoint.json'));
    }, 'owned App pane or worker did not start', run.aborted);
    run.child = readJson(run.config.ready);
    run.record.lineage = checkLineage(run);
    const pane = await listPane(run);
    const marker = await verifyMarker(run, pane);
    const geometry = await x11(run, { kind: 'admit' });
    const { window, xtestVersion, serverPid, appPid, focus, root } = geometry;
    need(window.id === focus && xtestVersion[0] === 2, 'owned X11 window admission failed');
    run.record.identity = { pane, marker, window, xtestVersion, serverPid, appPid, root, admitted: stamp() };
}

async function modes(run, key, plan = modePlan, restoring = false) {
    return exchangeModes(run, key, plan[key], restoring);
}

async function exchangeModes(run, key, { writeHex, replyHex, alternateReplyHex }, restoring) {
    checkLineage(run, restoring);
    const result = await worker(run, { kind: 'modes', writeHex, size: replyHex.length / 2 });
    if (result.replyHex !== replyHex && result.replyHex !== alternateReplyHex) {
        run.record.unexpectedModeExchange = { key, result };
        throw new Error('mode readback differs for ' + key);
    }
    return result;
}

async function recordAction(run, step, request, key, fence, prepare) {
    checkLineage(run);
    step.arm = await worker(run, { kind: 'arm', key });
    let failure;
    try {
        if (prepare) await prepare();
        step.native = await x11(run, { kind: 'action', window: run.record.identity.window, request, fence });
    } catch (error) { failure = error; }
    try { step.capture = await worker(run, { kind: 'seal', key }); }
    catch (error) {
        throw failure ? new AggregateError([failure, error], 'action and capture sealing failed') : error;
    }
    if (failure) throw failure;
}

async function recordFocus(run, step, key, fence, writeHex) {
    await recordAction(run, step, { kind: 'keyboard', chord: 'a' }, key, fence, async () => {
        step.checkpoint = await worker(run, { kind: 'checkpoint', key, writeHex });
        checkLineage(run);
        step.focus = await x11(run, { kind: 'focus-round-trip', window: run.record.identity.window });
    });
}

async function recordPhase(run, phase, sequence, id, kind) {
    phase.modes = await modes(run, kind === 'control' ? kind : id);
    for (const [index, request] of sequence.entries()) {
        const step = {};
        phase.steps.push(step);
        await recordAction(run, step, request, id + '/' + kind + '/' + index, actionFence(id, kind, index)); // NOSONAR: arm, drive and seal each exclusive observation window before the next.
    }
}

async function recordCase(run, id) {
    const { record } = run;
    const planned = actionPlan(record.identity.window, id);
    const attempt = { id, control: { steps: [] }, target: { steps: [] } };
    record.cases.push(attempt);
    await recordPhase(run, attempt.control, planned.control, id, 'control');
    await recordPhase(run, attempt.target, planned.target, id, 'target');
}

async function observeFocus(run) {
    const focus = await import('./modern-x11-focus.mjs');
    const readModes = key => modes(run, key, focus.modePlan, key === 'restore');
    await focus.observeFocus(run.record, readModes, (...args) => recordFocus(run, ...args));
    return focus.replayFocus;
}

function clipboardState(run, peerPid, peerWindow) {
    return x11(run, { kind: 'clipboard-state', window: run.record.identity.window, peerPid, peerWindow });
}

async function checkClipboardPane(run, restoring) {
    if (run.paste && restoring) {
        // Permit selection restoration even if the raw PTY worker has failed.
        // clipboardState still verifies this X server, App window and GTK peer.
        run.lease.assertHeld();
        need(!run.app.status && !run.display.status, 'owned X11 clipboard fixture exited');
        return;
    }
    checkLineage(run, restoring);
    const pane = await listPane(run);
    need(equal(pane, run.record.identity.pane), 'wrong current clipboard pane');
    await verifyMarker(run, pane);
}

async function clipboardPeer(run, step, request, ownerPids, restoring) {
    if (!(run.paste && restoring)) checkLineage(run, restoring);
    const directory = join(run.scratch, 'gtk-' + run.clipboardPeers.length);
    mkdirSync(directory, { mode });
    const argv = ['-E', '-s', '-B', join(run.sources, 'peer-linux.py'), 'text', directory];
    const peer = ownChild(run.python, argv, { env: { ...run.env, CI: 'true', GDK_BACKEND: 'x11', GTK_A11Y: 'none' },
        stdio: ['pipe', 'pipe', 'pipe'] }, run.logs, 'GTK peer ' + run.clipboardPeers.length);
    run.clipboardPeers.push(peer);
    peer.child.stdin.on('error', error => { run.logs['GTK input ' + peer.child.pid] = error.message; });
    const aborted = restoring ? undefined : run.aborted;
    const ready = await waitFor(() => {
        need(!peer.status, 'GTK peer exited before admission');
        const path = join(directory, 'ready.json');
        return existsSync(path) && readJson(path);
    }, 'GTK peer did not make an X11 window', aborted, 2500);
    const facts = readLinuxProcessFacts(peer.child.pid);
    need(ready.pid === peer.child.pid && facts.ppid === process.pid && facts.executable === run.python
        && equal(facts.argv, [run.python, ...argv]) && facts.uids.every(uid => uid === run.record.uid), 'wrong GTK peer process');
    step.process = facts;
    await checkClipboardPane(run, restoring);
    const state = () => clipboardState(run, ready.pid, ready.window);
    step.before = await state();
    need(ownerPids.includes(step.before.ownerPid), 'clipboard owner is outside this phase');
    step.dispatched = stamp();
    peer.child.stdin.end(JSON.stringify(request) + '\n');
    step.result = await waitFor(() => {
        const path = join(directory, 'result.json');
        if (existsSync(path)) return readJson(path);
        need(!peer.status, 'GTK peer exited before its result');
    }, 'GTK peer did not finish the clipboard operation', aborted, 2500);
    need(!step.result.error, 'GTK text operation failed: ' + step.result.error);
    need(typeof step.result.text === 'string', 'GTK text operation returned no text');
    if (!(run.paste && restoring)) checkLineage(run, restoring);
    step.after = await state();
}

async function observePaste(run) {
    const paste = await import('./modern-x11-clipboard-paste.mjs');
    await paste.observePaste(run.record, {
        peer: (...args) => clipboardPeer(run, ...args),
        mode: (key, plan, restoring) => exchangeModes(run, key, plan, restoring),
        action: (step, chord, key, tag) => recordAction(run, step, { kind: 'keyboard', chord }, key, tag),
    });
    return paste.replayPaste;
}

async function observeClipboard(run) {
    const clipboard = run.keyboard === '--osc52-clipboard'
        ? await import('./modern-x11-osc52-clipboard.mjs') : await import('./modern-x11-osc52-write.mjs');
    await clipboard.observeClipboard(run.record, {
        peer: (...args) => clipboardPeer(run, ...args),
        key: step => recordAction(run, step, { kind: 'keyboard', chord: 'a' }, clipboard.id, 'zh'),
        write: plan => modes(run, 'osc52', { osc52: plan }),
        query: async (sample, owner, plan) => {
            checkLineage(run);
            const pane = await listPane(run);
            need(equal(pane, run.record.identity.pane), 'wrong current clipboard query pane');
            await verifyMarker(run, pane);
            const state = () => clipboardState(run, owner.peerPid, owner.peerWindow);
            sample.before = await state();
            need(sample.before.focus === run.record.identity.window.id
                && sample.before.ownerPid === owner.ownerPid && sample.before.ownerWindow === owner.ownerWindow,
            'clipboard seed lost ownership before OSC 52 query');
            sample.exchange = await worker(run, { kind: 'clipboard-query', ...plan });
            checkLineage(run);
            sample.after = await state();
        },
    });
    return clipboard.replayClipboard;
}

async function verifyRestoredPane(run) {
    const { record } = run;
    const pane = await listPane(run);
    need(equal(pane, record.identity.pane), 'pane changed during collection');
    Object.assign(record.restore, { paneId: pane.paneId, marker: await verifyMarker(run, pane) });
}

async function restoreClipboardPane(run) {
    let replay, failure;
    try {
        replay = await (run.paste ? observePaste(run) : observeClipboard(run));
        await verifyRestoredPane(run);
    } catch (error) { failure = error; }
    try {
        checkLineage(run, true);
        run.record.terminal = await worker(run, { kind: 'restore' });
        await waitFor(() => !existsSync('/proc/' + run.child.pid), 'direct worker did not exit and disappear', undefined, 2500);
        run.record.workerGoneAt = stamp();
    } catch (error) {
        throw failure ? new AggregateError([failure, error], 'clipboard run and PTY restoration failed') : error;
    }
    if (failure) throw failure;
    return replay;
}

async function observeRoutedInput(run) {
    const { record } = run;
    if (run.keyboard === '--focus-tracking') return observeFocus(run);
    if (run.keyboard) {
        const keypad = run.keyboard === '--application-keypad';
        const keyboard = keypad ? await import('./modern-x11-keypad.mjs') : await import('./modern-x11-keyboard.mjs');
        const readModes = key => modes(run, key, keyboard.modePlan, keypad && key === 'restore');
        await keyboard.observeKeyboard(record, readModes, (step, request, key, fence) => recordAction(run, step, request, key, fence));
        return keyboard.replayKeyboard;
    }
    record.incoming = await modes(run, 'incoming');
    for (const id of ids) await recordCase(run, id); // NOSONAR: one pane, with distinct sequenced mode changes and byte windows.
    record.restore = { modes: await modes(run, 'restore') };
    return replayMouse;
}

async function observe(run) {
    await launchDisplay(run);
    await launchPane(run);
    const { record } = run;
    let replay;
    if (run.clipboard) {
        replay = await restoreClipboardPane(run);
    } else {
        replay = await observeRoutedInput(run);
        await verifyRestoredPane(run);
        record.terminal = await worker(run, { kind: 'restore' });
        await waitFor(() => !existsSync('/proc/' + run.child.pid), 'direct worker did not exit and disappear', run.aborted, 2500);
        record.workerGoneAt = stamp();
    }
    need(equal(readLinuxProcessFacts(run.app.child.pid), record.lineage.parent), 'original App exited before worker cleanup was observed');
    record.results = replay(record);
    need(equal(record.appFiles, appFiles(run.executable)), 'App publish tree changed');
}

function configure(run) {
    const home = join(run.scratch, 'home'), appData = join(run.scratch, 'appdata'), runtime = join(run.scratch, 'runtime');
    for (const path of [home, appData, runtime]) mkdirSync(path, { mode });
    const runId = run.lease.leaseId;
    run.paste = run.keyboard === '--clipboard-paste';
    run.clipboard = run.paste || run.keyboard === '--osc52-write' || run.keyboard === '--osc52-clipboard';
    let markerPrefix = run.keyboard ? 'x11-key-' : 'x11-mouse-';
    let profilePrefix = run.keyboard ? 'X11 Keyboard ' : 'X11 Mouse ';
    if (run.paste) {
        markerPrefix = 'x11-paste-';
        profilePrefix = 'X11 Clipboard Paste ';
    } else if (run.clipboard) {
        markerPrefix = 'x11-osc52-';
        profilePrefix = 'X11 OSC 52 ';
    }
    const marker = markerPrefix + runId;
    const config = { runId, marker, ownerPid: process.pid, socket: join(run.scratch, 'child.sock'), ready: join(run.scratch, 'child.json') };
    const configPath = join(run.scratch, 'worker.json');
    writeJson(configPath, config);
    const python = realpathSync('/usr/bin/python3');
    Object.assign(run, { appData, config, python, profile: profilePrefix + runId, clipboardPeers: [],
        workerArgs: ['-I', '-S', '-B', join(run.sources, 'modern-x11-mouse-worker.py'), configPath, '--ntilde-terminfo-worker=' + runId],
        env: { HOME: home, XDG_CONFIG_HOME: join(home, '.config'), XDG_DATA_HOME: join(home, '.local/share'), XDG_CACHE_HOME: join(home, '.cache'),
            XDG_RUNTIME_DIR: runtime, XDG_SESSION_TYPE: 'x11', NTILDE_APPDATA_ROOT: appData, TERMINFO_RUN_ID: runId, LANG: 'C.UTF-8', PATH: '/usr/bin:/bin' },
        logs: {}, record: { schemaVersion: 1, runId, coordinatorPid: process.pid, uid: process.getuid(), appFiles: appFiles(run.executable),
            nodeVersion: process.version, python, pythonSha256: hash(readFileSync(python)), appVersion: run.appVersion, cases: [] } });
}

export async function collect(run) {
    configure(run);
    let failure;
    try { await observe(run); }
    catch (error) { failure = error; }
    try {
        const peerExits = await Promise.allSettled(run.clipboardPeers.map(stop));
        const exits = await Promise.allSettled([stop(run.app), stop(run.display)]);
        const errors = [...peerExits, ...exits].filter(exit => exit.status === 'rejected').map(exit => exit.reason);
        if (errors.length) throw new AggregateError(errors, 'owned App or display exit remains unverified');
        const [app, display] = exits.map(exit => exit.value);
        const socketGone = !run.xSocket || !existsSync(run.xSocket);
        need(socketGone, 'allocated X11 display socket still exists');
        await run.lease.verifyCleanup();
        run.record.cleanup = { app, display, socketGone, at: stamp() };
        if (run.clipboard) run.record.cleanup.peers = peerExits.map((exit, index) => ({
            pid: run.clipboardPeers[index].child.pid, ...exit.value,
        }));
    } catch (error) {
        run.record.cleanupError = error.message;
        failure = failure ? new AggregateError([failure, error], 'collection and cleanup failed') : error;
    }
    if (failure) run.record.failure = failure instanceof AggregateError ? failure.errors.map(error => error.message) : [failure.message];
    writeJson(join(run.output, 'observation.json'), run.record);
    writeJson(join(run.output, 'process-output.json'), run.logs);
    if (failure) throw failure;
    return run.record.results;
}
