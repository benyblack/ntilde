import { compressExpected, matchesExpected } from './editing-expectations.mjs';
import { snapshotHash } from './agent-observer.mjs';

/** Keep historical editing packing unchanged; this suite explicitly observes more mode combinations. */
export function compressSavedExpected(state) {
    const value = compressExpected(state);
    Object.assign(value.bufferState, { autoWrap: state.autoWrap, insertMode: state.insertMode, pendingWrap: state.pendingWrap });
    return { ...value, cursorMode: structuredClone(state.cursorMode) };
}
export function matchesSavedExpected(actual, expected, attributes) {
    return matchesExpected(actual, expected, attributes) && snapshotHash(actual.cursorMode) === snapshotHash(expected.cursorMode);
}
