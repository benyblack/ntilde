import { isDeepStrictEqual as same } from 'node:util';

export const suiteId = 'ntilde-owned-osc633-metadata-v1';
export const contractsVersion = 1;
export const caseIds = Object.freeze(['extensions.osc633-e', 'extensions.osc633-p']);
export const scope = Object.freeze({ id: suiteId, persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'vscmeta-';

const osc = body => '\x1b]' + body + '\x07';
const mark = body => osc('633;' + body);
const cwd = path => osc('7;file://' + path);
const home = '\x1b[H', top = '\x1b[1;1H\x1b[2K';
const clear = '\x1b[2J' + home;
const begin = mark('A') + mark('B') + 'grid command';
const end = mark('D;0') + mark('A');
export const cpr = '\x1b[6n';

export const pinnedFeeds = Object.freeze([
    mark('E;ls -la;nonce123') + 'X',
    mark('P;Cwd=/tmp') + 'X',
]);
export const pinnedPaths = Object.freeze(Object.fromEntries(caseIds.map((caseId, index) => [caseId,
    Object.freeze({ termless: pinnedFeeds[index], terminal: Object.freeze([top, pinnedFeeds[index]]) }),
])));

export function requireMetadata(condition, detail) {
    if (!condition) throw new Error('OSC 633 metadata: ' + detail);
}

function paths(runId) {
    const root = '/qa-633-' + runId;
    return { baseline: root + '/baseline', control: root + '/osc7-control', first: root + '/east;one', second: String.raw`C:\QA-633-${runId}\x64;west=two` };
}
export const nonce = runId => 'NTILDE_633_' + runId;
export const restore = runId => end + cwd(paths(runId).baseline) + clear;

export function planFor(runId) {
    requireMetadata(/^[a-f\d]{32}$/.test(runId), 'invalid run identity');
    const { baseline, control, first, second } = paths(runId);
    const write = (stage, ...writes) => ({ stage, writes });
    const pinned = (stage, caseId) => [
        write(stage + '-setup', top),
        write(stage + '-termless', pinnedPaths[caseId].termless),
        { ...write(stage + '-terminal', ...pinnedPaths[caseId].terminal), query: cpr },
    ];
    return [
        { stage: 'start' },
        write('bind', end + cwd(baseline) + '\x1b[5;1H' + nonce(runId) + home),
        ...pinned('e', caseIds[0]),
        write('e-input', top + begin),
        write('e-reported', '\x1b]633;E;echo\\x20reported\\x3B\\\\part;nonce123\x1b\\'),
        write('e-running', mark('C;ignored-payload')),
        write('e-finished', end),
        write('e-no-report', top + begin + mark('C;ignored-payload')),
        write('e-no-report-finished', end),
        write('e-133-control', top + osc('133;A') + osc('133;B') + 'grid command' + osc('133;C;ZWNobyBjb250cm9s')),
        write('e-133-finished', osc('133;D;0') + osc('133;A')),
        ...pinned('p', caseIds[1]),
        write('p-baseline', cwd(baseline)),
        write('p-osc7-control', cwd(control)),
        write('p-first', mark('P;Cwd=' + first.replace(';', String.raw`\x3b`))),
        write('p-second', '\x1b]633;P;Cwd=' + second + '\x1b\\'),
        write('p-malformed', mark(String.raw`P;Cwd=/bad\x00`)),
        write('p-unknown', mark('P;IsWindows=True')),
        write('finish', restore(runId)),
        { stage: 'seal' },
    ];
}

export function expectedCwd(runId, stage) {
    return ({
        'p-baseline': paths(runId).baseline,
        'p-osc7-control': paths(runId).control,
        'p-first': paths(runId).first,
        'p-second': paths(runId).second,
    })[stage];
}

function outcome(controls, target) {
    if (!controls) return 'inconclusive';
    return target ? 'supported' : 'unsupported';
}
const row = (phase, state) => ({ phase, command: state.status.currentCommand, status: state.status.status,
    accepting: state.metadata.buffer.acceptingCommandInput, cwd: state.metadata.workingDirectory });

export function deriveEffects(runId, frames) {
    const observed = Object.fromEntries(frames.filter(frame => frame.before).map(frame => [frame.stage, row(frame.stage, frame.before)]));
    const read = stage => observed[stage];
    const idle = phase => ['awaitingInput', 'idle'].includes(read(phase).status) && read(phase).command === null;
    const submitted = phase => read(phase).status === 'running' && read(phase).accepting === false;
    const gridText = stage => frames.find(frame => frame.stage === stage).before.screen.lines[0];
    const eControls = ['e-input', 'e-running', 'e-no-report'].every(stage => gridText(stage) === 'grid command')
        && idle('e-input') && read('e-input').accepting && submitted('e-running')
        && submitted('e-no-report') && idle('e-finished') && idle('e-no-report-finished')
        && submitted('e-133-control') && read('e-133-control').command === 'echo control';
    const eStayed = same(frames.find(frame => frame.stage === 'e-input').before.screen,
        frames.find(frame => frame.stage === 'e-reported').before.screen);
    const expectedCommand = String.raw`echo reported;\part`;
    const eMatched = eStayed && idle('e-reported') && read('e-reported').accepting
        && read('e-running').command === expectedCommand && read('e-no-report').command === null;
    const pControls = ['p-baseline', 'p-osc7-control'].every(stage => read(stage).cwd === expectedCwd(runId, stage))
        && ['p-osc7-control', 'p-first', 'p-second', 'p-malformed', 'p-unknown'].every(idle);
    const pMatched = ['p-first', 'p-second'].every(stage => read(stage).cwd === expectedCwd(runId, stage))
        && ['p-malformed', 'p-unknown'].every(stage => read(stage).cwd === read('p-second').cwd);
    return [
        { caseId: caseIds[0], contractId: 'ntilde-osc633-e-cycle-metadata-v1', controls: eControls,
            outcome: outcome(eControls, eMatched), expectedCommand,
            observations: Object.values(observed).filter(value => value.phase.startsWith('e-') && !/-(?:setup|termless|terminal)$/.test(value.phase)) },
        { caseId: caseIds[1], contractId: 'ntilde-osc633-p-cwd-metadata-v1', controls: pControls,
            outcome: outcome(pControls, pMatched), expectedPaths: paths(runId),
            observations: Object.values(observed).filter(value => value.phase.startsWith('p-') && !/-(?:setup|termless|terminal)$/.test(value.phase)) },
    ];
}
