import { OwnedStaticImageObserver } from './static-images-capture.mjs';
import { animationSuite, animationSuites } from './kitty-animation.mjs';

/** The shared image-carrier observer (paired captures, raw fences, ownership admission) configured for an animation suite version (the current one by default). */
export class OwnedAnimationObserver extends OwnedStaticImageObserver {
    constructor(options) { super({ ...options, suite: options.suite ?? animationSuite }); }
}
/** The observer of one suite version: the restore collection reads the version 3 suite. */
export const animationObserverOf = version => class extends OwnedAnimationObserver {
    constructor(options) { super({ ...options, suite: animationSuites[version] }); }
};
