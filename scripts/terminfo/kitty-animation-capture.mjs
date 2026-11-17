import { OwnedStaticImageObserver } from './static-images-capture.mjs';
import { animationSuite } from './kitty-animation.mjs';

/** The shared image-carrier observer (paired captures, raw fences, ownership admission) configured for the animation suite. */
export class OwnedAnimationObserver extends OwnedStaticImageObserver {
    constructor(options) { super({ ...options, suite: animationSuite }); }
}
