// Versioned probe contracts for the ASCII-text and SGR qualification batch.
// Every byte below mirrors the pinned terminfo.dev@4.0.0 probe definitions
// (bundle SHA-256 aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6);
// scripts/terminfo/grading-collector.mjs refuses derivation when the pinned
// bundle no longer contains these exact definitions.
export const asciiSgrContractsVersion = 1;
export const asciiSgrSuiteId = 'ntilde-owned-ascii-sgr-graded-v1';
export const asciiSgrNoncePrefix = 'NTILDE_AG_';
export const asciiSgrReset = '\x1b[?6l\x1b[r\x1b[4l\x1b[0m\x1b[2J\x1b[H';

/** Flag bits of the App readScreen attribute projection (see attributes.mjs). */
export const asciiSgrFlagBits = Object.freeze({ bold: 0x0001, inverse: 0x0002, italic: 0x0004, underline: 0x0008 });

/** Expand the row-major attribute runs of one observed screen to per-cell flags. */
export function expandScreenFlags(screen) {
    const cells = screen.rows * screen.cols;
    const runs = screen.attributeRuns ?? [];
    const all = Array(cells).fill(0);
    let offset = 0;
    for (const run of runs) {
        if (!Number.isSafeInteger(run?.count) || run.count < 1 || !Number.isSafeInteger(run.flags)
            || run.flags < 0 || run.flags > 65535 || offset + run.count > cells) {
            throw new Error('Invalid graded attribute runs.');
        }
        all.fill(run.flags, offset, offset + run.count);
        offset += run.count;
    }
    if (offset !== cells) throw new Error('Incomplete graded attribute runs.');
    return all;
}

/** Project one observed cell to the pinned getCell shape; the collector and
 * the grading replay must share this single projection. */
export const cellAt = (screen, row, col) => {
    const flags = expandScreenFlags(screen)[row * screen.cols + col] ?? 0;
    return { char: (screen.lines[row] ?? ' ')[col] ?? ' ', flags,
        bold: (flags & asciiSgrFlagBits.bold) !== 0, italic: (flags & asciiSgrFlagBits.italic) !== 0 };
};

/** One read group: the pinned probe reads the screen once after a feed. */
const read = kind => ({ read: kind });
const feed = bytes => ({ feed: bytes });

/** The four pinned cases. `setup` is the driver's controlled precondition; the
 * pinned termless probes then replay their exact fixture bytes themselves. */
export const asciiSgrContracts = Object.freeze([
    Object.freeze({
        caseId: 'text.basic', probeId: 'text.basic', pinnedPath: 'termless',
        pinnedCriterion: 'Hello appears after a measured empty control',
        pinnedNote: 'Control already contained Hello',
        terminalFixture: { writes: ['\x1b[1;1H\x1b[2K', 'Hello'], demand: 'rendered Hello text', minimum: { rows: 1, cols: 6 } },
        plan: Object.freeze([feed(asciiSgrReset), feed('\x1b[2J\x1b[H'), read('text'), feed('Hello'), read('text')]),
        grade(screens) {
            const before = screens[0].lines.join('\n');
            const after = screens[1].lines.join('\n');
            if (before.includes('Hello')) return { outcome: 'inconclusive', note: 'Control already contained Hello' };
            return { outcome: after.includes('Hello') ? 'supported' : 'unsupported' };
        },
        observed(screens) {
            return { before: screens[0].lines[0], after: screens[1].lines[0], cursor: [screens[1].cursorRow, screens[1].cursorCol] };
        },
    }),
    Object.freeze({
        caseId: 'text.overwrite', probeId: 'text.overwrite', pinnedPath: 'termless',
        pinnedCriterion: 'C overwrites A while B remains',
        pinnedNote: 'AB control was not measured',
        terminalFixture: { writes: ['\x1b[1;1H\x1b[2K', 'AB', '\x1b[1;2H', 'X'], demand: 'overwritten B cell', minimum: { rows: 1, cols: 3 } },
        plan: Object.freeze([feed(asciiSgrReset), feed('AB'), read('cells'), feed('\x1b[1GC'), read('cells')]),
        grade(screens) {
            const before = [cellAt(screens[0], 0, 0), cellAt(screens[0], 0, 1)];
            const after = [cellAt(screens[1], 0, 0), cellAt(screens[1], 0, 1)];
            if (before[0].char !== 'A' || before[1].char !== 'B') return { outcome: 'inconclusive', note: 'AB control was not measured' };
            return { outcome: after[0].char === 'C' && after[1].char === 'B' ? 'supported' : 'unsupported' };
        },
        observed(screens) {
            return { before: [cellAt(screens[0], 0, 0), cellAt(screens[0], 0, 1)],
                after: [cellAt(screens[1], 0, 0), cellAt(screens[1], 0, 1)] };
        },
    }),
    Object.freeze({
        caseId: 'sgr.bold', probeId: 'sgr.bold', pinnedPath: 'termless',
        pinnedCriterion: 'attribute applied to the rendered X cell',
        pinnedNote: 'No rendered X cell to evaluate',
        terminalFixture: { writes: ['\x1b[1;1H\x1b[2K', '\x1b[1mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
        plan: Object.freeze([feed(asciiSgrReset), feed('\x1b[1mX'), read('cells')]),
        grade(screens) {
            const cell = cellAt(screens[0], 0, 0);
            if (cell.char !== 'X') return { outcome: 'inconclusive', note: 'No rendered X cell to evaluate' };
            return { outcome: cell.bold ? 'supported' : 'unsupported' };
        },
        observed(screens) {
            const cell = cellAt(screens[0], 0, 0);
            return { char: cell.char, bold: cell.bold, flags: cell.flags };
        },
        appearanceScope: 'stored bold flag only; glyph appearance is a separate claim (see docs/qa/terminfo/bold-glyphs.md)',
    }),
    Object.freeze({
        caseId: 'sgr.italic', probeId: 'sgr.italic', pinnedPath: 'termless',
        pinnedCriterion: 'attribute applied to the rendered X cell',
        pinnedNote: 'No rendered X cell to evaluate',
        terminalFixture: { writes: ['\x1b[1;1H\x1b[2K', '\x1b[3mX\x1b[0m'], demand: 'consumed (cursor advance does not verify SGR styling)', minimum: { rows: 1, cols: 2 } },
        plan: Object.freeze([feed(asciiSgrReset), feed('\x1b[3mX'), read('cells')]),
        grade(screens) {
            const cell = cellAt(screens[0], 0, 0);
            if (cell.char !== 'X') return { outcome: 'inconclusive', note: 'No rendered X cell to evaluate' };
            return { outcome: cell.italic ? 'supported' : 'unsupported' };
        },
        observed(screens) {
            const cell = cellAt(screens[0], 0, 0);
            return { char: cell.char, italic: cell.italic, flags: cell.flags };
        },
        appearanceScope: 'stored italic flag only; glyph appearance is a separate claim (see docs/qa/terminfo/bold-glyphs.md)',
    }),
]);

export const asciiSgrCaseIds = Object.freeze(asciiSgrContracts.map(contract => contract.caseId));

/** The exact feed schedule (setup plus pinned fixture bytes) for one case. */
export function contractFeeds(contract) {
    return contract.plan.filter(step => step.feed).map(step => step.feed);
}

/** The number of read groups the pinned probe performs for one case. */
export function contractReads(contract) {
    return contract.plan.filter(step => step.read).length;
}

/** Reject any recorded feed list that is not byte-identical to the contract. */
export function validateRecordedFeeds(contract, feeds) {
    const expected = contractFeeds(contract);
    if (!Array.isArray(feeds) || feeds.length !== expected.length
        || feeds.some((value, index) => value !== expected[index])) {
        throw new Error(`Recorded ${contract.caseId} feeds do not match the pinned contract.`);
    }
}

/** Independent contract grader: mirrors the pinned criteria without the bundle. */
export function gradeRecordedCase(contract, screens) {
    if (!Array.isArray(screens) || screens.length !== contractReads(contract)) {
        throw new Error(`Missing ${contract.caseId} read captures.`);
    }
    return contract.grade(screens);
}
