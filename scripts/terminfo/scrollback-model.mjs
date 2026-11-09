// A deliberately tiny, independent model of the scrolling/history behavior the scrollback qualification
// contracts rely on. It interprets the exact byte strings a contract plan feeds, so every expected viewport,
// history, margin, cursor and active-screen state is derived from the source-known fixture bytes and never
// from terminal readback. It models only the control subset the pinned fixtures and their controls use and
// refuses (throws on) anything else, so an unmodeled sequence can never silently produce an expectation.
//
// Modeled: printable ASCII, CR, LF, CUP/HVP, ED 2 and ED 3, DECSTBM, SU, SD, RI, SGR reset, the origin,
// reverse-video and insert-mode resets, and the 1049 alternate screen (cursor saved on entry and restored on
// exit, alternate screen cleared on entry, no history while alternate). A line scrolled off the top of a
// full-height main-screen region enters history; a line scrolled out of any other region, or off the
// alternate screen, is discarded. Wrapping is not modeled: a write that would wrap is refused.

/** One blank row of the given width. */
const blankRow = cols => ' '.repeat(cols);

/** A fresh model of a `rows` x `cols` terminal: blank main and alternate screens, no history, full margins. */
export function createScrollbackModel(rows, cols) {
    if (!Number.isSafeInteger(rows) || rows < 3 || !Number.isSafeInteger(cols) || cols < 8) throw new Error('Invalid scrollback model geometry.');
    const blank = () => Array.from({ length: rows }, () => blankRow(cols));
    return { rows, cols, main: blank(), alt: blank(), alternate: false, saved: [0, 0], history: [], top: 0, bottom: rows - 1, row: 0, col: 0, blank };
}

/** The visible grid of the active screen. */
const grid = model => (model.alternate ? model.alt : model.main);

/** Scroll the margin region up by one line; only a full-height main-screen region keeps the displaced line. */
function scrollUp(model) {
    const screen = grid(model);
    const [gone] = screen.splice(model.top, 1);
    screen.splice(model.bottom, 0, blankRow(model.cols));
    if (!model.alternate && model.top === 0 && model.bottom === model.rows - 1) model.history.push(gone.trimEnd());
}

/** Scroll the margin region down by one line; the line pushed out of the bottom margin is discarded. */
function scrollDown(model) {
    const screen = grid(model);
    screen.splice(model.bottom, 1);
    screen.splice(model.top, 0, blankRow(model.cols));
}

/** Put printable text at the cursor without wrapping. */
function put(model, text) {
    const screen = grid(model);
    for (const char of text) {
        if (model.col >= model.cols) throw new Error('The scrollback model does not model wrapping.');
        const row = screen[model.row];
        screen[model.row] = row.slice(0, model.col) + char + row.slice(model.col + 1);
        model.col++;
    }
}

/** Apply one CSI sequence from the modeled subset. */
function csi(model, prefix, params, final) {
    const numbers = params === '' ? [] : params.split(';').map(value => (value === '' ? 0 : Number(value)));
    const first = numbers[0] ?? 0;
    const count = Math.max(1, first);
    if (prefix === '?') {
        if (final === 'l' && numbers.length === 1 && [5, 6].includes(first)) return;
        if (final === 'h' && first === 1049) {
            if (!model.alternate) { model.saved = [model.row, model.col]; model.alt = model.blank(); model.alternate = true; }
            return;
        }
        if (final === 'l' && first === 1049) {
            if (model.alternate) { model.alternate = false; [model.row, model.col] = model.saved; }
            return;
        }
        throw new Error(`The scrollback model does not model CSI ?${params}${final}.`);
    }
    switch (final) {
        case 'H': case 'f':
            model.row = Math.min(Math.max(1, numbers[0] || 1), model.rows) - 1;
            model.col = Math.min(Math.max(1, numbers[1] || 1), model.cols) - 1;
            return;
        case 'J':
            if (first === 2) { const screen = grid(model); for (let row = 0; row < model.rows; row++) screen[row] = blankRow(model.cols); return; }
            if (first === 3) { model.history = []; return; }
            break;
        case 'r': {
            const top = numbers[0] || 1, bottom = numbers[1] || model.rows;
            if (top < bottom && bottom <= model.rows) { model.top = top - 1; model.bottom = bottom - 1; model.row = 0; model.col = 0; return; }
            if (numbers.length === 0) { model.top = 0; model.bottom = model.rows - 1; model.row = 0; model.col = 0; return; }
            throw new Error('The scrollback model does not model invalid margins.');
        }
        case 'S': for (let step = 0; step < count; step++) scrollUp(model); return;
        case 'T': for (let step = 0; step < count; step++) scrollDown(model); return;
        case 'm': if (numbers.every(value => value === 0)) return; break;
        case 'l': if (first === 4 && numbers.length === 1) return; break;
        default: break;
    }
    throw new Error(`The scrollback model does not model CSI ${params}${final}.`);
}

/** Feed one byte string through the model. */
export function feedScrollbackModel(model, text) {
    const escape = String.fromCharCode(0x1b);
    const pattern = new RegExp(`${escape}\\[([?]?)([\\d;]*)([@-~])|${escape}M|([\\s\\S])`, 'y');
    let index = 0;
    while (index < text.length) {
        pattern.lastIndex = index;
        const match = pattern.exec(text);
        if (match === null) throw new Error('The scrollback model could not parse the byte string.');
        index = pattern.lastIndex;
        if (match[3] !== undefined) csi(model, match[1], match[2], match[3]);
        else if (match[0] === '\x1bM') {
            if (model.row === model.top) scrollDown(model);
            else if (model.row > 0) model.row--;
        } else {
            const char = match[4];
            if (char === '\r') model.col = 0;
            else if (char === '\n') {
                if (model.row === model.bottom) scrollUp(model);
                else if (model.row < model.rows - 1) model.row++;
            } else if (char >= ' ' && char <= '~') put(model, char);
            else throw new Error('The scrollback model does not model control characters beyond CR and LF.');
        }
    }
}

/** The state a read of the model would show: right-trimmed viewport rows, cursor, margins, active screen and main history. */
export function scrollbackModelState(model) {
    return { lines: grid(model).map(row => row.trimEnd()), cursor: [model.row, model.col], top: model.top, bottom: model.bottom,
        alternate: model.alternate, history: [...model.history] };
}

/** Run a plan's steps through a fresh model and return the state at every read, in order. `edit` may replace or drop a feed. */
export function modelPlanStates(plan, rows, cols, edit = (_index, bytes) => bytes) {
    const model = createScrollbackModel(rows, cols), states = [];
    let feed = 0;
    for (const step of plan) {
        if (step.feed !== undefined) feedScrollbackModel(model, edit(feed++, step.feed));
        else states.push(scrollbackModelState(model));
    }
    return states;
}
