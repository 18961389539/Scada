// Offline checks for search request ordering, IME input and retry behavior.
// Runs the production script in a small DOM harness, without a server or SCADA devices.
// Run: node tools/Test-MimicLibraryRequests.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

let passed = 0;
function check(message, condition) {
    assert.ok(condition, message);
    console.log('PASS ' + message);
    passed++;
}

function element(initial = {}) {
    const handlers = new Map();
    const attributes = new Map();
    const classes = new Set();
    return Object.assign({
        dataset: {}, value: '', textContent: '',
        addEventListener(type, fn) { handlers.set(type, fn); },
        dispatch(type, data = {}) {
            const event = { preventDefault() { this.defaultPrevented = true; }, ...data };
            handlers.get(type)?.(event);
            return event;
        },
        setAttribute(name, value) { attributes.set(name, value); },
        removeAttribute(name) { attributes.delete(name); },
        getAttribute(name) { return attributes.get(name); },
        classList: { add(name) { classes.add(name); }, remove(name) { classes.delete(name); },
            contains(name) { return classes.has(name); } }
    }, initial);
}

function harness() {
    const elements = new Map();
    const timers = new Map();
    const requests = [];
    const updates = [];
    const document = element({ getElementById: id => elements.get(id), importNode: node => node });
    const input = element();
    const sort = element({ value: 'modified' });
    const status = element();
    const page = element({ value: '1' });
    const form = element({ action: 'http://localhost/MimicEditor/MimicList',
        dataset: { loading: 'Loading', failed: 'Search failed' }, querySelector: () => page });
    const location = { href: form.action };
    const results = (query = '', currentPage = '1', currentSort = 'modified') => {
        const url = new URL(form.action);
        url.search = new URLSearchParams({ FileQuery: query, FileSort: currentSort, FilePage: currentPage });
        return element({ dataset: { query, page: currentPage, sort: currentSort, url: url.href },
            replaceWith(node) { elements.set('fileLibraryResults', node); updates.push(node); } });
    };
    for (const [id, node] of [['frmFileSearch', form], ['txtFileSearch', input], ['selFileSort', sort],
        ['fileLibraryStatus', status], ['fileLibraryResults', results()]]) elements.set(id, node);
    let timerID = 0;
    const context = {
        $: ready => ready(), document, location, URL, AbortController,
        setTimeout(fn) { timers.set(++timerID, fn); return timerID; },
        clearTimeout(id) { timers.delete(id); },
        history: { replaceState(_state, _title, url) { location.href = String(url); } },
        DOMParser: class { parseFromString(replacement) {
            return { getElementById: () => replacement };
        } },
        fetch(url, options) {
            // Deliberately do not reject on abort: revision handling must also discard late responses.
            let resolve, reject;
            const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
            requests.push({ url: new URL(url), signal: options.signal, resolve, reject });
            return promise;
        }
    };
    vm.runInNewContext(fs.readFileSync(path.resolve(__dirname,
        '../ScadaWeb/Mimics/PlgMimicEditor/wwwroot/plugins/MimicEditor/js/file-library.js'), 'utf8'), context);
    return { document, input, sort, status, form, page, location, requests, updates, timers, results,
        current: () => elements.get('fileLibraryResults'),
        tick() { const pending = [...timers.values()]; timers.clear(); pending.forEach(fn => fn()); },
        async respond(index, node, ok = true) {
            requests[index].resolve({ ok, status: ok ? 200 : 503, text: async () => node });
            await new Promise(resolve => setImmediate(resolve));
        } };
}

async function run() {
    const h = harness();
    const originalInput = h.input;
    h.input.value = 'Old';
    h.input.dispatch('input');
    check('Typing is debounced before any request', h.requests.length === 0 && h.timers.size === 1);
    h.tick();
    check('The active search marks results busy', h.requests.length === 1 && h.current().getAttribute('aria-busy') === 'true');
    h.input.value = 'New';
    h.input.dispatch('input');
    check('Editing immediately aborts the old request before the new debounce expires',
        h.requests[0].signal.aborted && h.requests.length === 1);
    await h.respond(0, h.results('Old'));
    check('A late old response cannot replace results during the debounce', h.updates.length === 0);
    h.tick();
    await h.respond(1, h.results('New'));
    check('The newest response updates results, URL and page state',
        h.updates.length === 1 && h.current().dataset.query === 'New' &&
        new URL(h.location.href).searchParams.get('FileQuery') === 'New' && h.page.value === '1');
    check('Updating results preserves the original search input',
        h.document.getElementById('txtFileSearch') === originalInput);

    h.input.value = 'First';
    h.form.dispatch('submit');
    h.input.value = 'Second';
    h.form.dispatch('submit');
    await h.respond(3, h.results('Second'));
    await h.respond(2, h.results('First'));
    check('Out-of-order concurrent responses retain the latest results', h.current().dataset.query === 'Second');

    const imeStart = h.requests.length;
    h.input.dispatch('compositionstart');
    h.input.value = 'shui';
    h.input.dispatch('input');
    h.form.dispatch('submit');
    h.tick();
    check('IME composition and its Enter key do not submit incomplete text', h.requests.length === imeStart);
    h.input.value = '水泵';
    h.input.dispatch('compositionend');
    h.input.dispatch('input');
    h.tick();
    check('Finishing Chinese input schedules exactly one search with committed text',
        h.requests.length === imeStart + 1 && h.requests.at(-1).url.searchParams.get('FileQuery') === '水泵');
    await h.respond(imeStart, h.results('水泵'));

    h.input.dispatch('keydown', { key: 'Escape', isComposing: true });
    check('Escape inside IME does not clear committed text', h.input.value === '水泵');
    h.input.dispatch('keydown', { key: 'Escape', isComposing: false });
    check('Escape outside IME clears the query and immediately searches page one',
        h.input.value === '' && h.requests.at(-1).url.searchParams.get('FileQuery') === '' &&
        h.requests.at(-1).url.searchParams.get('FilePage') === '1');
    await h.respond(h.requests.length - 1, h.results(''));

    const click = (href, modifiers = {}) => h.document.dispatch('click', {
        target: { closest: () => ({ href }) }, button: 0, ...modifiers
    });
    const nextUrl = h.form.action + '?FileQuery=&FileSort=modified&FilePage=2';
    const beforeModifierClick = h.requests.length;
    check('Modified pagination clicks preserve browser behavior',
        !click(nextUrl, { ctrlKey: true }).defaultPrevented && h.requests.length === beforeModifierClick);
    check('An unmodified pagination click is handled without navigation', click(nextUrl).defaultPrevented);
    check('Pagination requests retain the requested page', h.requests.at(-1).url.searchParams.get('FilePage') === '2');
    await h.respond(h.requests.length - 1, h.results('', '2'));
    check('The returned page is reflected in state', h.current().dataset.page === '2' && h.page.value === '2');
    h.input.value = 'Changed';
    h.input.dispatch('input');
    click(nextUrl);
    check('Clicking pagination after changing the search restarts on page one',
        h.requests.at(-1).url.searchParams.get('FileQuery') === 'Changed' &&
        h.requests.at(-1).url.searchParams.get('FilePage') === '1' && h.timers.size === 0);

    await h.respond(h.requests.length - 1, null, false);
    check('A failed request keeps prior results and exposes a retryable error',
        h.current().dataset.page === '2' && h.status.textContent === 'Search failed' &&
        h.status.classList.contains('text-danger') && !h.current().getAttribute('aria-busy'));
    h.form.dispatch('submit');
    await h.respond(h.requests.length - 1, h.results('Changed'));
    check('Retrying replaces the results and clears the error', h.current().dataset.query === 'Changed' &&
        h.status.textContent === '' && !h.status.classList.contains('text-danger'));

    h.sort.value = 'name';
    h.sort.dispatch('change');
    check('Changing sort retains the current query and restarts on page one',
        h.requests.at(-1).url.searchParams.get('FileQuery') === 'Changed' &&
        h.requests.at(-1).url.searchParams.get('FileSort') === 'name' &&
        h.requests.at(-1).url.searchParams.get('FilePage') === '1');
    await h.respond(h.requests.length - 1, null);
    check('A response without the file list exposes an error without deleting existing rows',
        h.current().dataset.query === 'Changed' && h.status.textContent === 'Search failed');

    console.log(`Completed: ${passed} offline file library request checks passed.`);
}

run().catch(error => { console.error(error); process.exitCode = 1; });
