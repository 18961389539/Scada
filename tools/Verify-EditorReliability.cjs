// Regression checks for the real editor functions; no browser or running server is needed.
// Run from any directory: node tools/Verify-EditorReliability.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const editorDir = path.resolve(__dirname,
    '../ScadaWeb/Mimics/PlgMimicEditor/wwwroot/plugins/MimicEditor/js');
const sources = ['editor-common.js', 'mimic-edit.js'].map(name => ({
    name,
    source: fs.readFileSync(path.join(editorDir, name), 'utf8')
}));

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((resolvePromise, rejectPromise) => {
        resolve = resolvePromise;
        reject = rejectPromise;
    });
    return { promise, resolve, reject };
}

function response(ok, dto, status = ok ? 200 : 500) {
    return { ok, status, json: async () => dto };
}

function createEditor() {
    const calls = { requests: [], removed: [], events: [], toasts: [], clipboard: [] };
    const clock = { now: 42000 };
    const dependencies = {
        fetch: async () => response(true, { ok: true }),
        writeComponents: async () => undefined
    };
    const element = {
        prop() { return this; },
        data() { return this; }
    };

    class Mimic {
        constructor() {
            this.document = {};
            this.componentMap = new Map();
        }

        removeComponent(id) {
            calls.removed.push(id);
            const component = this.componentMap.get(id);
            this.componentMap.delete(id);
            return component;
        }
    }

    class EditorDate extends Date {
        static now() { return clock.now; }
    }

    const context = vm.createContext({
        console: { log() {}, error() {}, warn() {} },
        Date: EditorDate,
        Event: class { constructor(type) { this.type = type; } },
        CustomEvent: class {
            constructor(type, options) { this.type = type; this.detail = options?.detail; }
        },
        window: { dispatchEvent: event => calls.events.push(event.type) },
        // Skip the document-ready callback so startup cannot contact a server or start timers.
        $: () => element,
        rs: {
            mimic: {
                Mimic,
                Component: class {},
                UnitedRenderer: class {},
                MimicHelper: { areSiblings: () => true }
            }
        },
        fetch: async (url, options) => {
            const request = { url, method: options.method, body: options.body };
            calls.requests.push(request);
            return dependencies.fetch(request);
        },
        recordToast: (...args) => {
            calls.toasts.push(args);
            return element;
        },
        writeComponents: components => {
            calls.clipboard.push(Array.from(components, component => component.id));
            return dependencies.writeComponents(components);
        }
    });

    for (const { name, source } of sources) {
        vm.runInContext(source, context, { filename: path.join(editorDir, name) });
    }

    const run = source => vm.runInContext(source, context);
    run(`
        phrases = {
            postUpdateError: 'Update failed', mimicSaved: 'Saved',
            saveMimicError: 'Save failed', sameParentRequired: 'Same parent required',
            clipboardWriteError: 'Clipboard write failed'
        };
        structTree = { removeComponent() {}, selectMimic() {} };
        propGrid = {};
        showToast = recordToast;
        mimicClipboard.writeComponents = writeComponents;
        lastUpdateTime = 1234;
    `);

    return { run, calls, clock, dependencies };
}

const failures = [
    ['HTTP 500', async () => response(false, { ok: true }, 500)],
    ['HTTP 401', async () => response(false, { ok: true }, 401)],
    ['HTTP 200 with dto.ok=false', async () => response(true, { ok: false, msg: 'Rejected' })],
    ['HTTP 200 without dto.ok', async () => response(true, { msg: 'Missing result' })],
    ['HTTP 200 with a nonboolean dto.ok', async () => response(true, { ok: 'true' })],
    ['network exception', async () => { throw new Error('Connection closed'); }],
    ['invalid JSON', async () => ({
        ok: true, status: 200, json: async () => { throw new SyntaxError('Invalid JSON'); }
    })]
];

const tests = [];
function test(name, run) { tests.push({ name, run }); }

for (const [name, fetchFailure] of failures) {
    test(`postUpdate rejects ${name} without advancing its timestamp`, async () => {
        const editor = createEditor();
        editor.dependencies.fetch = fetchFailure;
        const result = await editor.run('postUpdate(new UpdateDto("test", []))');
        assert.equal(result, false, 'An unaccepted update must be reported as failed');
        assert.equal(editor.run('lastUpdateTime'), 1234,
            'A failed request must not count as a successful update or heartbeat');
        assert.equal(editor.calls.requests.length, 1);
        assert.ok(editor.calls.toasts.length > 0, 'Failure must remain visible to the user');
    });
}

test('postUpdate advances its timestamp only after the server accepts the change', async () => {
    const editor = createEditor();
    const reply = deferred();
    editor.dependencies.fetch = () => reply.promise;
    const pending = editor.run('postUpdate(new UpdateDto("test", []))');
    assert.equal(editor.run('lastUpdateTime'), 1234, 'Pending requests are not successful updates');
    reply.resolve(response(true, { ok: true }));
    assert.equal(await pending, true);
    assert.equal(editor.run('lastUpdateTime'), editor.clock.now);
});

for (const [name, fetchFailure] of failures) {
    test(`postUpdates retains the failed head for ${name} and saves once after recovery`, async () => {
        const editor = createEditor();
        editor.run(`
            pushChangesNoHistory({ sequence: 1 });
            pushChangesNoHistory({ sequence: 2 });
            pushChangesNoHistory({ sequence: 3 });
        `);
        const originalQueue = Array.from(editor.run('updateQueue.slice()'));
        const originalBodies = originalQueue.map(update => update.json);
        editor.dependencies.fetch = fetchFailure;
        await editor.run('save()');
        assert.equal(editor.calls.requests.length, 0, 'Save must wait for queued updates');

        for (let attempt = 1; attempt <= 2; attempt++) {
            await editor.run('postUpdates()');
            const retainedQueue = Array.from(editor.run('updateQueue'));
            assert.equal(retainedQueue.length, originalQueue.length,
                'A failed update must not be dropped or drain the queue');
            retainedQueue.forEach((update, index) => assert.equal(update, originalQueue[index],
                'Failure must retain the same update objects in their original order'));
            assert.deepEqual(editor.calls.requests.map(request => request.body),
                Array(attempt).fill(originalBodies[0]), 'Retries must stop at the failed head');
            assert.equal(editor.run('queueEmptyFlags.saveRequired'), true,
                'The deferred save must remain pending while updates are failing');
            assert.equal(editor.run('mimicModified'), false,
                'A rejected update must not be marked as accepted');
            assert.equal(editor.run('updateInProgress'), false,
                'Failure must release the in-progress state so recovery can retry');
            assert.equal(editor.calls.events.filter(type => type.startsWith('mimic-save')).length, 0,
                'No save event may fire on update failure');
        }

        editor.dependencies.fetch = async () => response(true, { ok: true });
        editor.clock.now += 1000;
        await editor.run('postUpdates()');
        assert.deepEqual(editor.calls.requests.slice(2, 5).map(request => request.body),
            originalBodies, 'Recovery must submit every queued update in its original order');
        assert.ok(editor.calls.requests.slice(0, 5).every(request =>
            request.url.endsWith('/UpdateMimic')), 'All updates must finish before SaveMimic');
        assert.ok(editor.calls.requests[5].url.includes('/SaveMimic?'),
            'SaveMimic must follow the last accepted update');
        assert.equal(editor.run('updateQueue.length'), 0);
        assert.equal(editor.run('queueEmptyFlags.saveRequired'), false);
        assert.equal(editor.run('mimicModified'), false);
        assert.deepEqual(editor.calls.events.filter(type => type.startsWith('mimic-save')),
            ['mimic-saved']);

        await editor.run('postUpdates()');
        assert.equal(editor.calls.requests.length, 6,
            'An empty queue must not repeat a completed deferred save');
    });
}

test('an in-flight update serializes later updates and defers saving until both finish', async () => {
    const editor = createEditor();
    const reply = deferred();
    editor.dependencies.fetch = () => editor.calls.requests.length === 1
        ? reply.promise : response(true, { ok: true });
    editor.run('pushChangesNoHistory({ sequence: 1 })');
    const firstBody = editor.run('updateQueue[0].json');
    const posting = editor.run('postUpdates()');
    assert.equal(editor.run('updateQueue.length'), 0,
        'This case must exercise the empty queue while its head is still in flight');
    assert.equal(editor.run('updateInProgress'), true);

    await editor.run('save()');
    assert.equal(editor.run('queueEmptyFlags.saveRequired'), true,
        'Save must wait for the update that has already left the queue');
    editor.run('pushChangesNoHistory({ sequence: 2 })');
    const secondBody = editor.run('updateQueue[0].json');
    await editor.run('postUpdates()');
    assert.equal(editor.calls.requests.length, 1,
        'A concurrent flush must not submit another update or save before the first update finishes');

    reply.resolve(response(true, { ok: true }));
    await posting;
    assert.deepEqual(editor.calls.requests.slice(0, 2).map(request => request.body),
        [firstBody, secondBody]);
    assert.ok(editor.calls.requests[2].url.includes('/SaveMimic?'));
    assert.equal(editor.calls.requests.length, 3);
    assert.equal(editor.run('updateQueue.length'), 0);
    assert.equal(editor.run('updateInProgress'), false);
    assert.equal(editor.run('mimicModified'), false);
});

test('an in-flight save blocks duplicate saves and delays newly queued changes', async () => {
    const editor = createEditor();
    const reply = deferred();
    editor.dependencies.fetch = request => request.url.includes('/SaveMimic?')
        ? reply.promise : response(true, { ok: true });
    const saving = editor.run('save()');
    assert.equal(editor.run('saveInProgress'), true);
    await editor.run('save()');
    assert.equal(editor.calls.requests.length, 1, 'Repeated Save must not issue a duplicate request');

    editor.run('pushChangesNoHistory({ sequence: 1 })');
    await editor.run('postUpdates()');
    assert.equal(editor.calls.requests.length, 1,
        'New updates must wait until the active SaveMimic request finishes');
    assert.equal(editor.run('updateQueue.length'), 1);
    reply.resolve(response(true, { ok: true }));
    await saving;
    assert.equal(editor.run('saveInProgress'), false);
    assert.equal(editor.run('mimicModified'), true,
        'An older save must not mark edits created while it was pending as saved');
    await editor.run('postUpdates()');
    assert.equal(editor.calls.requests.length, 2);
    assert.ok(editor.calls.requests[1].url.endsWith('/UpdateMimic'));
    assert.equal(editor.run('updateQueue.length'), 0);
});

function selectComponent(editor) {
    editor.run(`
        selectedComponents = [{ id: 17, parentID: 0, isSelected: true }];
        mimic.componentMap.set(17, selectedComponents[0]);
    `);
}

test('cut retains the component when asynchronous copying fails', async () => {
    const editor = createEditor();
    const clipboard = deferred();
    editor.dependencies.writeComponents = () => clipboard.promise;
    selectComponent(editor);
    const pending = Promise.resolve(editor.run('cut()')).then(
        () => undefined,
        error => error // Both an explicit false result and a propagated clipboard failure are safe.
    );
    await Promise.resolve();
    assert.deepEqual(editor.calls.clipboard, [[17]]);
    assert.deepEqual(editor.calls.removed, [], 'Cut must wait for the asynchronous copy result');
    clipboard.reject(new Error('Clipboard permission denied'));
    await pending;
    assert.deepEqual(editor.calls.removed, [], 'A failed copy must not remove the original component');
    assert.equal(editor.run('mimic.componentMap.has(17)'), true);
    assert.equal(editor.run('selectedComponents.length'), 1);
    assert.equal(editor.run('updateQueue.length'), 0, 'Failed copying must not queue a removal');
});

test('cut deletes and queues the removal only after asynchronous copying succeeds', async () => {
    const editor = createEditor();
    const clipboard = deferred();
    editor.dependencies.writeComponents = () => clipboard.promise;
    selectComponent(editor);
    const pending = editor.run('cut()');
    await Promise.resolve();
    assert.deepEqual(editor.calls.removed, [], 'Copy completion must precede deletion');
    clipboard.resolve();
    await pending;
    assert.deepEqual(editor.calls.removed, [17]);
    assert.equal(editor.run('mimic.componentMap.has(17)'), false);
    assert.equal(editor.run('updateQueue.length'), 1);
    assert.equal(editor.run('updateQueue[0].changes[0].changeType'), 'RemoveComponent');
    assert.deepEqual(Array.from(editor.run('updateQueue[0].changes[0].getObjectIDs()')), [17]);
});

test('cut does not queue a removal when nothing is selected', async () => {
    const editor = createEditor();
    await editor.run('cut()');
    assert.deepEqual(editor.calls.clipboard, []);
    assert.deepEqual(editor.calls.removed, []);
    assert.equal(editor.run('updateQueue.length'), 0);
});

test('cut retains both components if selection changes while copying is pending', async () => {
    const editor = createEditor();
    const clipboard = deferred();
    editor.dependencies.writeComponents = () => clipboard.promise;
    selectComponent(editor);
    const pending = editor.run('cut()');
    editor.run(`
        selectedComponents.splice(0, 1, { id: 23, parentID: 0, isSelected: true });
        mimic.componentMap.set(23, selectedComponents[0]);
    `);
    clipboard.resolve();
    await pending;
    assert.deepEqual(editor.calls.clipboard, [[17]], 'Copy must retain the initial selection');
    assert.deepEqual(editor.calls.removed, [], 'The changed selection must not be deleted');
    assert.equal(editor.run('mimic.componentMap.has(17) && mimic.componentMap.has(23)'), true);
    assert.equal(editor.run('selectedComponents[0].id'), 23);
    assert.equal(editor.run('updateQueue.length'), 0);
});

(async () => {
    let failed = 0;
    for (const { name, run } of tests) {
        try {
            await run();
            console.log(`PASS | ${name}`);
        } catch (error) {
            failed++;
            console.error(`FAIL | ${name}\n${error.stack}`);
        }
    }
    console.log(`\nSUMMARY ${tests.length - failed}/${tests.length} passed`);
    if (failed > 0) process.exitCode = 1;
})().catch(error => {
    console.error(error.stack);
    process.exitCode = 2;
});
