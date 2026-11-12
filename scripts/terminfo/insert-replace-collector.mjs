import { insertReplaceContract, insertReplacePinnedFeeds, insertReplaceTerminalWrites, insertReplaceNoncePrefix,
    insertReplaceReset, insertReplaceFeeds, insertReplaceReads } from './insert-replace-contracts.mjs';
import { sgrAttrsCellAt } from './sgr-attrs-contracts.mjs';

const prefix = 'irm';
const at = (screens, role) => screens[insertReplaceContract.roles.indexOf(role)];

/** Serve the exact pinned termless feeds and parent-observed screens in order. */
export function insertReplaceTermlessContext(screens) {
    let fed = 0;
    const current = () => {
        const role = ['seed', 'home', 'enabled', 'inserted', 'disabled'][fed - 1];
        if (!role || !at(screens, role)) throw new Error('Pinned IRM read outside its fixture.');
        return at(screens, role);
    };
    return { get cols() { return at(screens, 'seed').cols; }, get fedCount() { return fed; },
        getScrollback: () => ({ screenLines: at(screens, 'seed').rows }),
        feed(bytes) {
            if (fed >= insertReplacePinnedFeeds.length || bytes !== insertReplacePinnedFeeds[fed]) throw new Error('Pinned IRM feed differs from the contract.');
            fed++;
        },
        getCursor: () => ({ x: current().cursorCol, y: current().cursorRow, visible: current().cursorVisible }),
        getCell: (row, col) => sgrAttrsCellAt(current(), row, col) };
}

/** Serve the separately collected terminal fixture and its projected cursor, not a PTY reply. */
export function insertReplaceTerminalContext(screen) {
    let written = 0;
    return { rows: screen.rows, cols: screen.cols, get writtenCount() { return written; },
        write(bytes) {
            if (written >= insertReplaceTerminalWrites.length || bytes !== insertReplaceTerminalWrites[written]) throw new Error('Pinned IRM terminal write differs from the contract.');
            written++;
        },
        queryCursorPosition() {
            if (written !== insertReplaceTerminalWrites.length) throw new Error('Pinned IRM terminal queried before its writes.');
            return Promise.resolve({ row: screen.cursorRow + 1, col: screen.cursorCol + 1 });
        } };
}

/** Collect serially and restore the original main-screen IRM and wrap state even on failure. */
export async function collectInsertReplace({ config, fence, owner, gradingModule }) {
    const adapter = gradingModule.ntildeInsertReplaceAdapter;
    if (!adapter || adapter.id !== 'ntilde-owned-insert-replace-grading-v1'
        || JSON.stringify(adapter.probeIds) !== JSON.stringify(['modes.insert-replace'])) throw new Error('Invalid IRM adapter identity.');
    const started = await owner(`${prefix}-start`);
    const original = started.screen?.bufferState;
    const margins = started.screen?.horizontalMargins;
    if (!original || original.alternateScreen !== false || typeof original.insertMode !== 'boolean' || typeof original.autoWrap !== 'boolean'
        || !margins || margins.leftRightMarginMode !== false || margins.leftMargin !== 0 || margins.rightMargin !== started.screen.cols - 1) {
        throw new Error('Cannot establish the original main-screen IRM, wrap and full-width margin state.');
    }
    const restore = `${insertReplaceReset}\x1b[?1049l\x1b[4${original.insertMode ? 'h' : 'l'}\x1b[?7${original.autoWrap ? 'h' : 'l'}`;
    let failed, result;
    try {
        const bind = await fence.write(`\x1b[?1049h${insertReplaceReset}${insertReplaceNoncePrefix}${config.runId}`);
        await owner(`${prefix}-bind`, bind);
        const feeds = [], barriers = [], reads = [], screens = [];
        for (const step of insertReplaceContract.plan) {
            if (step.feed) {
                feeds.push(step.feed);
                barriers.push(await fence.write(step.feed)); // serial: the next read depends on this write
            } else {
                const observed = await owner(`${prefix}-read`, barriers.at(-1));
                screens.push(observed.screen);
                reads.push({ kind: step.read, cursor: [observed.screen.cursorRow, observed.screen.cursorCol] });
            }
        }
        if (feeds.length !== insertReplaceFeeds().length || screens.length !== insertReplaceReads()) throw new Error('Incomplete IRM fixture.');
        const termless = insertReplaceTermlessContext(screens);
        const pinned = gradingModule.ntildeRunInsertReplaceTermless('modes.insert-replace', termless);
        if (pinned.observation?.outcome !== 'inconclusive' && termless.fedCount !== insertReplacePinnedFeeds.length) throw new Error('Incomplete pinned IRM replay.');
        const terminal = insertReplaceTerminalContext(at(screens, 'terminal'));
        const terminalPinned = await gradingModule.ntildeRunInsertReplaceTerminal('modes.insert-replace', terminal);
        if (terminal.writtenCount !== insertReplaceTerminalWrites.length) throw new Error('Incomplete pinned IRM terminal replay.');
        result = { schemaVersion: 1, adapterId: adapter.id, probeIds: [...adapter.probeIds],
            original: { insertMode: original.insertMode, autoWrap: original.autoWrap }, feeds, barriers, reads, pinned, terminalPinned,
            terminalCursorEvidence: 'parent-observed cursor projection; not a PTY cursor reply' };
    } catch (error) { failed = error; }
    try { await owner(`${prefix}-finish`, await fence.write(restore)); }
    catch (restoreError) {
        if (failed) throw new AggregateError([failed, restoreError], 'IRM collection and restoration failed.');
        throw restoreError;
    }
    if (failed) throw failed;
    return result;
}
