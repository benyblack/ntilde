// Planned protocol variants and the independent playback timeline only; importing performs no terminal I/O.
import { animationFrameInput } from './kitty-animation-inputs.mjs';

export const animationSuiteId = 'ntilde-owned-kitty-animation-v1';
export const animationAnchor = Object.freeze({ col: 3, row: 4 });
export const animationCellEnvelope = Object.freeze({ cols: 4, rows: 2 });
/** Every frame, root included, is shown for this many milliseconds (z=, and a=a r=1,z= for the root). */
export const animationHoldMs = 5000;
export const animationHoldCycle = Object.freeze(['root', 'second', 'third']);
/** Largest protocol-processing lag a read-back must tolerate when it brackets the moment a command took effect. */
export const animationAppliedMarginMs = 250;

// appearance: 'absent' or the frame whose solid color must fill the placement.
// clock: 'static' (no running animation expected to move), 'running' (elapsed time since a=a s=3 selects the frame),
// 'frozen' (stopped on the frame that was showing; it must outlast a hold the model says would have advanced it).
// targetMs: where in the running timeline the read aims, centered in a hold window so capture latency cannot straddle it.
const stage = (id, appearance, extra = {}) => Object.freeze({ id, appearance, clock: 'static', ...extra });
const build = stage('build', 'root', { reply: 'build' });
const clear = stage('clear', 'absent', { reply: 'delete' });
export const animationCases = Object.freeze([
    Object.freeze({ id: 'kitty.animation.stopped-hold', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('hold', 'root', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.playback', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000 }),
        stage('second', 'second', { clock: 'running', targetMs: 7500 }),
        stage('third', 'third', { clock: 'running', targetMs: 12500 }),
        stage('loop', 'root', { clock: 'running', targetMs: 17500 }), clear]) }),
    Object.freeze({ id: 'kitty.animation.stop-freeze', stages: Object.freeze([stage('blank', 'absent'), build,
        stage('start', 'root', { clock: 'running', reply: 'start', targetMs: 1000 }),
        stage('second', 'second', { clock: 'running', targetMs: 6200 }),
        stage('stop', 'second', { clock: 'frozen', reply: 'stop' }),
        stage('hold', 'second', { clock: 'frozen', waitMs: animationHoldMs + 1000 }), clear]) }),
]);

const uint = value => Number.isSafeInteger(value) && value >= 1 && value <= 0xffffffff;
const apc = params => '\x1b_G' + params + '\x1b\\';
const clearScreen = '\x1b[2J\x1b[H';
const frameData = frameId => animationFrameInput(frameId).bytes.toString('base64');

/** Exact bounded bytes; the image id is parent-chosen per case, never guessed from a reply. */
export function animationCommand(caseId, stageId, { imageId } = {}) {
    const spec = animationCases.find(item => item.id === caseId);
    const entry = spec?.stages.find(item => item.id === stageId);
    if (!entry) throw new Error('Unknown animation stage.');
    if (stageId === 'blank' || (entry.waitMs && !entry.reply)) return '';
    if (!uint(imageId)) throw new Error('Animation image id must be an explicit positive uint32.');
    if (stageId === 'build') {
        const gap = `z=${animationHoldMs}`;
        return apc(`a=T,f=32,s=16,v=16,t=d,i=${imageId},p=1,c=4,r=2,C=1;` + frameData('root'))
            + apc(`a=f,f=32,s=16,v=16,t=d,i=${imageId},${gap};` + frameData('second'))
            + apc(`a=f,f=32,s=16,v=16,t=d,i=${imageId},${gap};` + frameData('third'))
            + apc(`a=a,i=${imageId},r=1,${gap}`);
    }
    if (stageId === 'start') return apc(`a=a,i=${imageId},s=3`);
    if (stageId === 'stop') return apc(`a=a,i=${imageId},s=1`);
    if (stageId === 'clear') return apc(`a=d,d=I,i=${imageId}`) + clearScreen;
    return '';
}

/** The ordered correlated replies one stage's command produces; a=d success is silent. The a=T reply echoes the placement id it was given (p=1). */
export function animationExpectedReplies(stageId, imageId) {
    if (!uint(imageId)) throw new Error('Animation image id must be an explicit positive uint32.');
    const ok = `\x1b_Gi=${imageId};OK\x1b\\`;
    if (stageId === 'build') return [`\x1b_Gi=${imageId},p=1;OK\x1b\\`, ok, ok, ok];
    if (['start', 'stop'].includes(stageId)) return [ok];
    return [];
}
