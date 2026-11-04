// Planned protocol variants only; importing/building this plan performs no terminal I/O.
import { staticImageInput, staticSixel } from './static-images-inputs.mjs';

export const staticImageSuiteId = 'ntilde-owned-static-images-v1';
export const staticImageAnchor = Object.freeze({ col: 3, row: 4 });
export const staticImageCellEnvelope = Object.freeze({ cols: 4, rows: 2 });
const stage = (id, appearance, reply = null) => Object.freeze({ id, appearance, reply });
export const staticImageCases = Object.freeze([
    Object.freeze({ id: 'sixel.native-raster', geometry: 'native', sourceId: 'opaque',
        stages: Object.freeze([stage('blank', 'absent'), stage('show', 'present'), stage('clear', 'absent')]) }),
    Object.freeze({ id: 'iterm.explicit-stretch', geometry: 'stretch', sourceId: 'alpha',
        stages: Object.freeze([stage('blank', 'absent'), stage('show', 'present'), stage('clear', 'absent')]) }),
    Object.freeze({ id: 'iterm.default-aspect', geometry: 'aspect', sourceId: 'alpha',
        stages: Object.freeze([stage('blank', 'absent'), stage('show', 'present'), stage('clear', 'absent')]) }),
    Object.freeze({ id: 'kitty.numbered-lifecycle', geometry: 'aspect', sourceId: 'alpha',
        stages: Object.freeze([stage('blank', 'absent'), stage('store', 'absent', 'allocate'),
            stage('place', 'present', 'place'), stage('soft-delete', 'absent'),
            stage('redisplay', 'present', 'place'), stage('hard-delete', 'absent'),
            stage('missing-place', 'absent', 'missing')]) }),
    Object.freeze({ id: 'kitty.number-reuse', geometry: 'aspect', sourceId: 'alpha',
        stages: Object.freeze([stage('blank', 'absent'), stage('store-first', 'absent', 'allocate'),
            stage('store-second', 'absent', 'allocate'), stage('place-newest', 'reversed', 'place'),
            stage('delete-newest', 'absent'), stage('place-old-id', 'present', 'place-old'),
            stage('delete-old-id', 'absent')]) }),
]);

const uint = value => Number.isSafeInteger(value) && value >= 1 && value <= 0xffffffff;
const apc = params => '\x1b_G' + params + '\x1b\\';
const clear = '\x1b[2J\x1b[H';

/** Exact bounded bytes. Numbers/allocated ids come from the proved run and raw replies, never guesses. */
export function staticImageCommand(caseId, stageId, { number, firstId } = {}) {
    const spec = staticImageCases.find(item => item.id === caseId);
    if (!spec || !spec.stages.some(item => item.id === stageId)) throw new Error('Unknown static image stage.');
    if (stageId === 'blank') return '';
    if (stageId === 'clear') return clear;
    if (caseId === 'sixel.native-raster') return staticSixel;
    if (caseId.startsWith('iterm.')) {
        const preserve = caseId === 'iterm.explicit-stretch' ? ';preserveAspectRatio=0' : '';
        return '\x1b]1337;File=inline=1;width=4;height=2' + preserve + ':'
            + staticImageInput('alpha', 'png').bytes.toString('base64') + '\x1b\\';
    }
    if (!uint(number)) throw new Error('Static image number must be an explicit positive uint32.');
    if (stageId.startsWith('store')) {
        const source = stageId === 'store-second' ? 'reversed' : 'alpha';
        return apc(`a=t,f=32,s=16,v=16,t=d,I=${number};` + staticImageInput(source, 'rgba').bytes.toString('base64'));
    }
    if (['place', 'redisplay', 'place-newest', 'missing-place'].includes(stageId)) return apc(`a=p,I=${number},p=1,c=4,r=2,C=1`);
    if (stageId === 'soft-delete') return apc(`a=d,d=n,I=${number}`);
    if (['hard-delete', 'delete-newest'].includes(stageId)) return apc(`a=d,d=N,I=${number}`);
    if (!uint(firstId)) throw new Error('Old-image commands require the actual first allocated id.');
    if (stageId === 'place-old-id') return apc(`a=p,i=${firstId},p=1,c=4,r=2,C=1`);
    if (stageId === 'delete-old-id') return apc(`a=d,d=I,i=${firstId}`);
    throw new Error('Static image command is not implemented.');
}

/** Parse a single separately fenced reply. This verifies correlation, never freshness or pixels. */
export function staticKittyReply(raw, { number = null, placement = null, expectedId = null, allocation = false, seenIds = [], missing = false } = {}) {
    if ((number === null ? !uint(expectedId) || allocation : !uint(number)) || (placement !== null && !uint(placement)) || (expectedId !== null && !uint(expectedId))
        || !Array.isArray(seenIds) || seenIds.length > 2 || !seenIds.every(uint)
        || typeof raw !== 'string' || raw.length > 512) throw new Error('Invalid static Kitty reply context.');
    const match = /^\x1b_G([^;]+);([ -~]+)\x1b\\$/.exec(raw);
    if (!match) throw new Error('Malformed static Kitty reply.');
    const fields = {};
    for (const part of match[1].split(',')) {
        const field = /^(i|I|p)=([1-9][0-9]{0,9})$/.exec(part);
        if (!field || Object.hasOwn(fields, field[1]) || !uint(Number(field[2]))) throw new Error('Invalid Kitty reply fields.');
        fields[field[1]] = Number(field[2]);
    }
    if ((number === null ? fields.I !== undefined : fields.I !== number) || (!missing && !uint(fields.i)) || (placement === null ? fields.p !== undefined : fields.p !== placement)
        || (expectedId !== null && fields.i !== expectedId)
        || (allocation && (!uint(fields.i) || seenIds.includes(fields.i)))
        || (!missing && match[2] !== 'OK') || (missing && !/^ENOENT(?::|$)/.test(match[2]))) throw new Error('Uncorrelated static Kitty reply.');
    return { id: fields.i ?? null, number: fields.I, placement: fields.p ?? null, status: match[2], raw };
}
