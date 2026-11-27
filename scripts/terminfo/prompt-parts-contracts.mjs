import { isDeepStrictEqual as same } from 'node:util';

export const suiteId = 'ntilde-owned-contour-prompt-parts-v1';
export const contractsVersion = 1;
export const caseIds = Object.freeze(['extensions.osc133-p']);
export const scope = Object.freeze({ id: suiteId, persistence: 'Off', deadlineMs: 90000 });
export const prefix = 'prompt-parts-';
export const fileStem = 'prompt-parts';
export const expectedCwd = id => '/qa-prompt-parts-' + id;
const nonce = id => 'NTILDE_PART_' + id;
const osc = s => '\x1b]' + s + '\x07';
const mark = s => osc('133;' + s);
const cwd = path => osc('7;file://' + path);
const top = '\x1b[1;1H\x1b[2K';
const clear = '\x1b[2J\x1b[H';
const tag = id => '\x1b[5;1H' + nonce(id) + '\x1b[H';
const pinned = '\x1b]133;P;Cwd=/tmp\x07X';
export const restore = id => mark('D;0') + mark('A') + cwd(expectedCwd(id)) + clear;
export function requireMetadata(ok, detail) {
    if (!ok) throw new Error('OSC 133 prompt parts: ' + detail);
}

export function planFor(id) {
    requireMetadata(/^[a-f\d]{32}$/.test(id), 'invalid run ID');
    const write = (stage, ...writes) => ({ stage, writes });
    return [
        { stage: 'start' },
        write('bind', restore(id) + tag(id)),
        write('p-setup', top),
        write('p-termless', pinned),
        { ...write('p-terminal', top, pinned), query: '\x1b[6n' },
        write('directory-control', osc('633;P;Cwd=' + expectedCwd(id) + '/control')),
        write('input', top + mark('A;click_events=2') + mark('P') + '$ ' + mark('B') + 'cmd' + '\x1b[22G'),
        write('right', '\x1b]133;P;k=r\x1b\\'),
        write('paint', 'RHS'),
        write('relocated', '\x1b[17G' + mark('P;k=r') + 'new'),
        write('continuation', '\x1b[2;4H' + mark('P;k=c') + 'PS2'),
        write('kitty', '\x1b[3;2H' + mark('A;k=s') + 'KIT'),
        write('erased', '\x1b[1;17H\x1b[3X'),
        write('repaint', mark('P;k=r') + 'rhs'),
        write('overwrite', '\x1b[1;16Hxy'),
        write('submitted', '\x1b[1;6H' + mark('C;cmdline_url=cmd')),
        write('orphan', mark('P;k=r')),
        write('next-cycle', mark('D;0') + top + mark('A') + '\x1b[11G' + mark('P;k=r') + 'R'),
        write('new-prompt', '\x1b[2;1H' + mark('A')),
        write('pre-reset', '\x1b[3;11H' + mark('P;k=s') + 'S'),
        write('reset', '\x1bc' + tag(id) + mark('P;k=r')),
        write('finish', restore(id)),
        { stage: 'seal' },
    ];
}

export function checkRestoredParts(state) {
    requireMetadata(same(state.metadata.buffer.promptParts, []), 'initial/final prompt parts did not verify');
}
export function checkFixture(stage, { screen, metadata }, id) {
    requireMetadata(screen.lines[4] === nonce(id) && screen.lines.slice(5).every(line => line === '')
        && metadata.buffer.cursorRow === screen.cursorRow && metadata.buffer.cursorCol === screen.cursorCol
        && Array.isArray(metadata.buffer.promptParts), 'missing pane binding, cursor or part observation at ' + stage);
}

export function deriveEffects(id, frames) {
    const reads = new Map(frames.filter(f => f.before).map(f => [f.stage, f.before]));
    const observations = Object.fromEntries([...reads].map(([stage, { metadata, status, screen }]) => [stage, {
        parts: metadata.buffer.promptParts, commandStart: metadata.buffer.commandStart, outputStart: metadata.buffer.outputStart,
        accepting: metadata.buffer.acceptingCommandInput, status: status.status, command: status.currentCommand,
        cwd: metadata.workingDirectory, cursor: [screen.cursorRow, screen.cursorCol],
    }]));
    const o = observations, base = expectedCwd(id), a = o.input.commandStart;
    const controlStages = ['input', 'right', 'paint', 'relocated', 'continuation', 'kitty', 'erased', 'repaint', 'overwrite'];
    const controls = a?.row === 0 && a.col === 2 && a.absoluteRow === 0 && !a.alternateScreen
        && o.input.accepting && o.input.command === null && o.input.outputStart === null
        && o['p-setup'].cwd === base && o['directory-control'].cwd === base + '/control'
        && o.submitted.status === 'running' && o.submitted.command === 'cmd' && !o.submitted.accepting
        && same(o.submitted.commandStart, a) && o.submitted.outputStart !== null
        && o['next-cycle'].commandStart === null && o['next-cycle'].outputStart === null
        && same(reads.get('input').screen, reads.get('right').screen)
        && reads.get('paint').screen.lines[0].slice(21, 24) === 'RHS'
        && reads.get('relocated').screen.lines[0].slice(16, 19) === 'new'
        && reads.get('continuation').screen.lines[1] === '   PS2'
        && reads.get('kitty').screen.lines[2] === ' KIT'
        && reads.get('erased').screen.lines[0].slice(16, 19) === '   '
        && reads.get('overwrite').screen.lines[0].slice(15, 17) === 'xy';

    const part = (kind, row, col) => ({ kind, row, col });
    const primary = part('i', 0, 0), right = part('r', 0, 21), moved = part('r', 0, 16), continuation = part('c', 1, 3);
    const expected = {
        'p-termless': [], 'p-terminal': [], input: [primary], right: [primary, right], paint: [primary, right],
        relocated: [primary, moved], continuation: [primary, moved, continuation], kitty: [primary, moved, continuation],
        erased: [primary, continuation], repaint: [primary, moved, continuation], overwrite: [primary, continuation],
        submitted: [], orphan: [], 'next-cycle': [part('r', 0, 10)], 'new-prompt': [], 'pre-reset': [part('s', 2, 10)], reset: [],
    };
    const matched = Object.entries(expected).every(([stage, parts]) => same(o[stage].parts, parts))
        && controlStages.every(stage => same(o[stage].commandStart, a) && o[stage].outputStart === null
            && o[stage].accepting && o[stage].command === null && o[stage].status === o.input.status && o[stage].cwd === base + '/control')
        && ['p-termless', 'p-terminal'].every(stage => o[stage].cwd === base);
    let outcome = 'inconclusive';
    if (controls) outcome = matched ? 'supported' : 'unsupported';
    return [{ caseId: caseIds[0], contractId: 'ntilde-osc133-active-prompt-part-v1', controls, outcome, observations }];
}
