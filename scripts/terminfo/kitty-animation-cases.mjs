// Planned protocol variants and the independent playback timeline only; importing performs no terminal I/O.
import { animationFrameInput, animationPngInput } from './kitty-animation-inputs.mjs';

/**
 * Suite versions: 1 is the three-case suite that retained runs still carry (#988); 2 adds PNG and root-frame composition (#1015) and is what full collections produce;
 * 3 (#1030) is the restore suite: the same animation state is read again after the App is quit and started again on the same persistent session, so it only runs in the
 * persistent-restore collection (a direct tab has nothing to reattach to).
 */
export const animationSuiteIds = Object.freeze({ 1: 'ntilde-owned-kitty-animation-v1', 2: 'ntilde-owned-kitty-animation-v2', 3: 'ntilde-owned-kitty-animation-restore-v1' });
export const currentAnimationVersion = 2;
export const animationSuiteId = animationSuiteIds[currentAnimationVersion];
export const animationAnchor = Object.freeze({ col: 3, row: 4 });
export const animationCellEnvelope = Object.freeze({ cols: 4, rows: 2 });
/** Every frame, root included, is shown for this many milliseconds (z=, and a=a r=1,z= for the root). */
export const animationHoldMs = 5000;
export const animationHoldCycle = Object.freeze(['root', 'second', 'third']);
/** Largest protocol-processing lag a read-back must tolerate when it brackets the moment a command took effect. */
export const animationAppliedMarginMs = 250;

// appearance: 'absent', a frame whose solid color must fill the placement, or a composed frame (overRoot, overEdit) whose color the protocol's
// own blend formula gives (see kitty-animation-inputs.mjs).
// expect: the ordered correlated replies the stage's command produces ('placed' is the a=T reply, which echoes its placement id; a=d success is silent).
// relaunchAtMs: where in the running timeline the relaunch begins (the App is back about 2-3 s later, inside the second frame's window, so a playhead the new App restarted would
// show different frames from a restored one at the later reads).
// relaunch: the parent quits the App and starts it again on the same private root before this stage is admitted; the stage reads what the reattached pane shows.
// appearance 'timeline' (a relaunch stage whose frame depends on when the App came back): the frame the independent timeline gives for the capture span.
// clock: 'static' (no running animation expected to move), 'running' (elapsed time since a=a s=3 selects the frame),
// 'frozen' (stopped on the frame that was showing; it must outlast a hold the model says would have advanced it).
// targetMs: where in the running timeline the read aims, centered in a hold window so capture latency cannot straddle it.
const stage = (id, appearance, extra = {}) => Object.freeze({ id, appearance, clock: 'static', expect: Object.freeze([]), ...extra });
const replies = (...kinds) => Object.freeze(kinds);
const build = stage('build', 'root', { reply: 'build', expect: replies('placed', 'ok', 'ok', 'ok') });
const clear = stage('clear', 'absent', { reply: 'delete' });
const timedCases = Object.freeze([
    Object.freeze({ id: 'kitty.animation.stopped-hold', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('hold', 'root', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.playback', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        stage('second', 'second', { clock: 'running', targetMs: 7500 }),
        stage('third', 'third', { clock: 'running', targetMs: 12500 }),
        stage('loop', 'root', { clock: 'running', targetMs: 17500 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.stop-freeze', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        stage('second', 'second', { clock: 'running', targetMs: 6200 }),
        stage('stop', 'second', { clock: 'frozen', reply: 'stop', expect: replies('ok') }),
        stage('hold', 'second', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
]);
// PNG frames and the root frame as a base and an edit target, each shown by an explicit a=a,c= so no elapsed time is involved.
const compositionCase = Object.freeze({ id: 'kitty.animation.png-and-root-frames', stages: Object.freeze([stage('blank', 'absent'),
    stage('build', 'root', { reply: 'build', expect: replies('placed', 'ok') }),
    stage('show', 'overRoot', { reply: 'show', expect: replies('ok') }),
    stage('edit', 'third', { reply: 'edit', expect: replies('ok', 'ok') }),
    stage('compose', 'overEdit', { reply: 'compose', expect: replies('ok', 'ok') }), clear]) });
// The restore cases (#1030). Each reads, after a relaunch, state that only a restored snapshot can carry: a running playhead, a stopped one, and edited frames.
const reattach = (appearance, extra = {}) => stage('reattach', appearance, { relaunch: true, ...extra });
const restoreCases = Object.freeze([
    Object.freeze({ id: 'kitty.animation.restore-playing', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        reattach('timeline', { clock: 'running', relaunchAtMs: 4000 }),
        stage('third', 'third', { clock: 'running', targetMs: 12500 }),
        stage('loop', 'root', { clock: 'running', targetMs: 17500 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.restore-stopped', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000, expect: replies('ok') }),
        stage('second', 'second', { clock: 'running', targetMs: 6200 }),
        stage('stop', 'second', { clock: 'frozen', reply: 'stop', expect: replies('ok') }),
        reattach('second', { clock: 'frozen' }),
        stage('hold', 'second', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.restore-frames', stages: Object.freeze([stage('blank', 'absent'),
        stage('build', 'root', { reply: 'build', expect: replies('placed', 'ok') }),
        stage('show', 'overRoot', { reply: 'show', expect: replies('ok') }),
        stage('edit', 'third', { reply: 'edit', expect: replies('ok', 'ok') }),
        reattach('third'),
        stage('compose', 'overEdit', { reply: 'compose', expect: replies('ok', 'ok') }), clear]) }),
]);
export const animationCasesByVersion = Object.freeze({ 1: timedCases, 2: Object.freeze([...timedCases, compositionCase]), 3: restoreCases });
export const animationCases = animationCasesByVersion[currentAnimationVersion];

const uint = value => Number.isSafeInteger(value) && value >= 1 && value <= 0xffffffff;
const apc = params => '\x1b_G' + params + '\x1b\\';
const clearScreen = '\x1b[2J\x1b[H';
const frameData = frameId => animationFrameInput(frameId).bytes.toString('base64');
const pngData = pngId => animationPngInput(pngId).bytes.toString('base64');
const frameControls = 'f=32,s=16,v=16,t=d';

const compositionCaseId = 'kitty.animation.png-and-root-frames', restoreFramesCaseId = 'kitty.animation.restore-frames';
// Frame 2: the half-transparent green PNG over the root (c=1). Frame 3: the half-transparent red PNG over the root after it was edited blue (r=1).
const compositionCommands = Object.freeze({
    build: (imageId, root) => root + apc(`a=f,f=100,t=d,i=${imageId},c=1,z=${animationHoldMs};` + pngData('overRoot')),
    show: imageId => apc(`a=a,i=${imageId},c=2`),
    edit: imageId => apc(`a=f,${frameControls},i=${imageId},r=1;` + frameData('third')) + apc(`a=a,i=${imageId},c=1`),
    compose: imageId => apc(`a=f,f=100,t=d,i=${imageId},c=1,z=${animationHoldMs};` + pngData('overEdit')) + apc(`a=a,i=${imageId},c=3`),
});

/** Exact bounded bytes; the image id is parent-chosen per case, never guessed from a reply. */
export function animationCommand(caseId, stageId, { imageId } = {}, version = currentAnimationVersion) {
    const spec = animationCasesByVersion[version].find(item => item.id === caseId);
    const entry = spec?.stages.find(item => item.id === stageId);
    if (!entry) throw new Error('Unknown animation stage.');
    if (stageId === 'blank' || entry.relaunch || (entry.waitMs && !entry.reply)) return '';
    if (!uint(imageId)) throw new Error('Animation image id must be an explicit positive uint32.');
    const root = apc(`a=T,${frameControls},i=${imageId},p=1,c=4,r=2,C=1;` + frameData('root'));
    const composition = [compositionCaseId, restoreFramesCaseId].includes(caseId) ? compositionCommands[stageId] : undefined;
    if (composition) return composition(imageId, root);
    if (stageId === 'build') {
        const gap = `z=${animationHoldMs}`;
        return root
            + apc(`a=f,${frameControls},i=${imageId},${gap};` + frameData('second'))
            + apc(`a=f,${frameControls},i=${imageId},${gap};` + frameData('third'))
            + apc(`a=a,i=${imageId},r=1,${gap}`);
    }
    if (stageId === 'start') return apc(`a=a,i=${imageId},s=3`);
    if (stageId === 'stop') return apc(`a=a,i=${imageId},s=1`);
    if (stageId === 'clear') return apc(`a=d,d=I,i=${imageId}`) + clearScreen;
    return '';
}

/** The ordered correlated replies one stage's command produces; a=d success is silent. The a=T reply echoes the placement id it was given (p=1). */
export function animationExpectedReplies(stage, imageId) {
    if (!uint(imageId)) throw new Error('Animation image id must be an explicit positive uint32.');
    const ok = `\x1b_Gi=${imageId};OK\x1b\\`, placed = `\x1b_Gi=${imageId},p=1;OK\x1b\\`;
    return stage.expect.map(kind => (kind === 'placed' ? placed : ok));
}
