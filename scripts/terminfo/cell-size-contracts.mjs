// Independent OSC cell-size contract v1. iTerm2 ReportCellSize: height/width in
// logical points plus physical/logical scale. urxvt 9.29 command.C cellinfo:
// only '?' asks; width/height/font ascent in pixels, matching request terminator.
// The frozen probes instead require exactly TWO 1337 values and ask BARE 776.
// Those defects are preserved, never accommodated by changing the live reply.
export const cellSizeSuiteId = 'ntilde-owned-osc-cell-size-v1';
export const cellSizeEffectId = 'ntilde-osc-cell-size-effect-v1';
export const cellSizeVersion = 1;
export const cellSizePrefix = 'csz';
export const cellSizeNonce = 'NTILDE_CS_';
export const cellSizeIds = Object.freeze(['extensions.osc1337-cellsize', 'extensions.osc776-cell-size']);
const osc = (body, end = '\x07') => '\x1b]' + body + end;
export const cellSizePinnedRequests = Object.freeze([osc('1337;ReportCellSize'), osc('776')]);
const targets = ['1337;ReportCellSize', '776;?'];
const negatives = [
    ['1337;ReportCellSize;', '1337;ReportCellSize=?', '1337;ReportCellSize=17;8;2', '1337;reportcellsize'],
    ['776', '776;', '776;??', '776;?;1', '776;17;35;25'],
];
export const cellSizeContracts = Object.freeze(cellSizeIds.map((caseId, index) => ({ caseId,
    exchanges: [
        { id: 'positive-before', role: 'control', request: '\x1b[16t', protocol: 'csi' },
        { id: 'pinned', role: 'pinned', request: cellSizePinnedRequests[index], protocol: index === 0 ? 'iterm' : 'silence' },
        { id: 'documented-bel', role: 'required', request: osc(targets[index]), protocol: index === 0 ? 'iterm' : 'urxvt', terminator: '\x07' },
        { id: 'documented-st', role: 'required', request: osc(targets[index], '\x1b\\'), protocol: index === 0 ? 'iterm' : 'urxvt', terminator: '\x1b\\' },
        ...negatives[index].map((body, n) => ({ id: `unsupported-form-${n}`, role: 'required', request: osc(body), protocol: 'silence' })),
        { id: 'positive-after', role: 'control', request: '\x1b[16t', protocol: 'csi' },
    ],
})));

export function cellSizeGeometry(value, rows, cols) {
    const number = (key, min, max) => Number.isFinite(value?.[key]) && value[key] >= min && value[key] <= max;
    if (value?.viewRows !== rows || value.viewCols !== cols || !number('cellWidthDip', 1, 256)
        || !number('cellHeightDip', 1, 512) || !number('fontAscentDip', 1, value.cellHeightDip)
        || !number('renderScaling', 0.25, 8) || !number('backingScale', 0.25, 8)) throw new Error('Unavailable or inconsistent observed cell geometry.');
    return { viewRows: value.viewRows, viewCols: value.viewCols, cellWidthDip: value.cellWidthDip,
        cellHeightDip: value.cellHeightDip, fontAscentDip: value.fontAscentDip, renderScaling: value.renderScaling, backingScale: value.backingScale };
}

// The integer policy is explicit: nearest positive physical pixel, halfway up.
// No parser helper participates in this independently specified oracle.
export function cellSizeMatches(exchange, replies, geometry) {
    if (exchange.protocol === 'silence') return replies.length === 0;
    if (replies.length !== 1) return false;
    const [raw] = replies;
    const px = points => Math.max(1, Math.floor(points * geometry.renderScaling + 0.5));
    if (exchange.protocol === 'csi') return raw === `\x1b[6;${px(geometry.cellHeightDip)};${px(geometry.cellWidthDip)}t`;
    if (exchange.protocol === 'urxvt') return raw === `\x1b]776;${px(geometry.cellWidthDip)};${px(geometry.cellHeightDip)};${px(geometry.fontAscentDip)}${exchange.terminator}`;
    const match = /^\x1b\]1337;ReportCellSize=([0-9]+(?:\.[0-9]+)?);([0-9]+(?:\.[0-9]+)?);([0-9]+(?:\.[0-9]+)?)\x1b\\$/.exec(raw);
    return match !== null && [geometry.cellHeightDip * geometry.renderScaling / geometry.backingScale, geometry.cellWidthDip * geometry.renderScaling / geometry.backingScale, geometry.backingScale]
        .every((expected, index) => Math.abs(Number(match[index + 1]) - expected) <= Math.max(1, expected) * 1e-8);
}

/** Independent reproduction of frozen oscReplyResult, not an effect grade. */
export function replayCellSizePinned(index, raw) {
    const pattern = index === 0 ? /\x1b\]1337;ReportCellSize=[1-9][0-9]*(?:\.[0-9]+)?;[1-9][0-9]*(?:\.[0-9]+)?(?:\x07|\x1b\\)/
        : /\x1b\]776;[0-9]+;[0-9]+;[0-9]+(?:\x07|\x1b\\)/;
    const frame = pattern.exec(raw)?.[0];
    if (frame) return { pass: true, response: raw, observation: { outcome: 'supported', evidence: 'query' }, assertions: [{ kind: 'positive',
        expected: index === 0 ? 'Complete OSC 1337 ReportCellSize reply with two positive dimensions' : 'Complete OSC 776 cell-size reply', observed: frame }] };
    const prefix = index === 0 ? '\x1b]1337;ReportCellSize=' : '\x1b]776;';
    return { pass: false, response: raw, observation: { outcome: 'inconclusive', reason: raw.includes(prefix) ? 'invalid-reply' : 'no-response', evidence: 'query' } };
}
