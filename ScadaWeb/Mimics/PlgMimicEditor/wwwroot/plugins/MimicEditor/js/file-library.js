// Search all saved mimics while keeping the search field and its focus in place.
$(() => {
    const form = document.getElementById('frmFileSearch');
    const input = document.getElementById('txtFileSearch');
    const sort = document.getElementById('selFileSort');
    const status = document.getElementById('fileLibraryStatus');
    if (!form || !input || !sort || !status) return;

    let timer;
    let controller;
    let revision = 0;
    let composing = false;

    function searchUrl() {
        const url = new URL(form.action, location.href);
        url.search = '';
        url.searchParams.set('FileQuery', input.value.trim());
        url.searchParams.set('FileSort', sort.value);
        url.searchParams.set('FilePage', '1');
        return url;
    }

    // Invalidate a request as soon as the input changes, before the debounce expires.
    function cancel() {
        clearTimeout(timer);
        revision++;
        controller?.abort();
        controller = null;
        document.getElementById('fileLibraryResults')?.removeAttribute('aria-busy');
        status.textContent = '';
    }

    async function load(url) {
        cancel();
        const requestRevision = revision;
        const requestController = new AbortController();
        controller = requestController;
        document.getElementById('fileLibraryResults')?.setAttribute('aria-busy', 'true');
        status.classList.remove('text-danger');
        status.textContent = form.dataset.loading;
        try {
            const response = await fetch(url, { signal: requestController.signal, headers: { 'X-Requested-With': 'XMLHttpRequest' } });
            if (!response.ok) throw new Error('HTTP ' + response.status);
            const html = await response.text();
            if (requestRevision !== revision) return;
            const parsed = new DOMParser().parseFromString(html, 'text/html');
            const replacement = parsed.getElementById('fileLibraryResults');
            if (!replacement) throw new Error('File list unavailable');
            document.getElementById('fileLibraryResults').replaceWith(document.importNode(replacement, true));
            const current = document.getElementById('fileLibraryResults');
            history.replaceState(null, '', current.dataset.url || url);
            const page = form.querySelector('[name="FilePage"]');
            if (page) page.value = current.dataset.page;
            status.textContent = '';
        } catch (error) {
            if (requestRevision !== revision || error.name === 'AbortError') return;
            status.classList.add('text-danger');
            status.textContent = form.dataset.failed;
        } finally {
            if (requestRevision === revision) {
                controller = null;
                document.getElementById('fileLibraryResults')?.removeAttribute('aria-busy');
            }
        }
    }

    input.addEventListener('input', () => {
        cancel();
        if (!composing) timer = setTimeout(() => load(searchUrl()), 300);
    });
    input.addEventListener('compositionstart', () => { composing = true; cancel(); });
    input.addEventListener('compositionend', () => { composing = false; cancel(); timer = setTimeout(() => load(searchUrl()), 300); });
    input.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !event.isComposing) {
            event.preventDefault();
            input.value = '';
            load(searchUrl());
        }
    });
    sort.addEventListener('change', () => load(searchUrl()));
    form.addEventListener('submit', event => { event.preventDefault(); if (!composing) load(searchUrl()); });
    document.addEventListener('click', event => {
        const link = event.target.closest('#fileLibraryResults .file-library-pages a');
        if (!link || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        const current = document.getElementById('fileLibraryResults');
        load(input.value.trim() !== current.dataset.query || sort.value !== current.dataset.sort ? searchUrl() : link.href);
    });
});
