/** Poll sequentially with one pending check, a finite deadline and optional attempt bound. */
export function pollCommonCore(
    check,
    {
        timeoutMs,
        intervalMs = 100,
        maxAttempts = Infinity,
        message = 'Timed out waiting for owned evidence',
        pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
    },
) {
    const deadline = performance.now() + timeoutMs;
    return new Promise((resolve, reject) => {
        let attempts = 0;
        const tick = async () => {
            try {
                if (await check()) {
                    resolve(true);
                    return;
                }
                attempts++;
                if (attempts >= maxAttempts || performance.now() >= deadline) {
                    reject(new Error(message));
                    return;
                }
                await pause(intervalMs);
                void tick();
            } catch (error) {
                reject(error);
            }
        };
        void tick();
    });
}
