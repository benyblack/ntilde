import { writeFileSync, linkSync, unlinkSync } from 'node:fs';
import { join, basename } from 'node:path';
import { randomBytes } from 'node:crypto';
/** Publish complete private JSON atomically, refusing to overwrite an existing record. */
export function publishCommonCoreJson(directory, name, value) {
    if (basename(name) !== name || !name.endsWith('.json')) throw new Error('Invalid immutable JSON record name.');
    const content = JSON.stringify(value, null, 2) + '\n';
    const temporary = join(directory, '.record-' + randomBytes(16).toString('hex') + '.tmp');
    writeFileSync(temporary, content, { flag: 'wx', mode: 0o600 });
    try {
        linkSync(temporary, join(directory, name));
    } finally {
        unlinkSync(temporary);
    }
}
