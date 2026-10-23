// Periodically writes unsaved edits to a server-side draft, so a browser crash or a closed
// tab does not throw away half an hour of work. The draft is only offered for restore on the
// next open, never applied silently.
$(() => {
    if (typeof mimicKey === 'undefined' || !mimicKey) {
        return;
    }

    const intervalMs = 30000;
    const endpoint = rootPath + 'Api/MimicEditor/Updater/AutoSaveMimic?key=' + encodeURIComponent(mimicKey);
    let busy = false;

    function isDirty() {
        const modified = typeof mimicModified !== 'undefined' && mimicModified;
        const queued = typeof updateQueue !== 'undefined' && Array.isArray(updateQueue) && updateQueue.length > 0;
        const pending = typeof updateInProgress !== 'undefined' && updateInProgress;
        return !!modified || queued || !!pending;
    }

    async function tick() {
        if (busy || document.hidden || !isDirty()) {
            return;
        }

        busy = true;

        try {
            const response = await fetch(endpoint, { method: 'POST' });

            if (response.ok) {
                await response.json();
            }
        } catch (error) {
            // Auto-save is best effort; the normal save path still reports its own errors.
        } finally {
            busy = false;
        }
    }

    setInterval(tick, intervalMs);

    // Closing the tab may not leave time for fetch; a beacon is the last chance.
    window.addEventListener('pagehide', () => {
        if (!isDirty() || typeof navigator.sendBeacon !== 'function') {
            return;
        }

        try {
            navigator.sendBeacon(endpoint);
        } catch (error) {
            // Best effort.
        }
    });
});
