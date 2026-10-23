// Exercise navigation events against isolated DOM geometry; no SCADA service or equipment is used.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.resolve(__dirname,
    '../ScadaWeb/Mimics/PlgMimicEditor/wwwroot/plugins/MimicEditor/js/canvas-navigation.js'), 'utf8');
let checks = 0;

function fixture({ readyState = 'complete', lang = 'zh-Hans', query = '' } = {}) {
    const listeners = new Map();
    const observers = [];
    const animationFrames = [];
    const attributes = {};
    const node = { title: '', closest: selector => selector === '.node-comp' ? node : null };
    const button = {
        disabled: true, parentElement: {}, setAttribute: (key, value) => attributes[key] = value,
        addEventListener: (type, callback) => listeners.set('button:' + type, callback)
    };
    const wrapper = {
        clientWidth: 500, clientHeight: 300, clientLeft: 2, clientTop: 2,
        scrollWidth: 4000, scrollHeight: 3000, scrollLeft: 200, scrollTop: 400,
        getBoundingClientRect: () => ({ left: 100, top: 80 }),
        querySelector: () => wrapper.canvas,
        contains: element => element.inWrapper !== false
    };
    const structure = {
        querySelectorAll: () => [node],
        addEventListener: (type, callback) => listeners.set('structure:' + type, callback)
    };
    const body = { matches: () => false, closest: () => null };
    const document = {
        readyState, documentElement: { lang },
        getElementById: id => ({ btnEditorLocate: button, divMimicWrapper: wrapper, divStructure: structure })[id],
        querySelector: () => document.modal,
        addEventListener: (type, callback) => listeners.set('document:' + type, callback)
    };
    const context = vm.createContext({
        document, Node: { ELEMENT_NODE: 1 }, selectedComponents: [], longAction: null, URL,
        window: {
            location: { href: 'http://localhost/MimicEdit/1' + query },
            history: { replaceState(state, title, url) { this.url = url.toString(); } }
        },
        MutationObserver: class {
            constructor(callback) { this.callback = callback; observers.push(this); }
            observe() {}
            disconnect() { this.disconnected = true; }
        },
        requestAnimationFrame: callback => animationFrames.push(callback)
    });
    vm.runInContext(source, context);
    function event(overrides = {}) {
        return Object.assign({
            target: body, key: 'f', preventDefault() { this.defaultPrevented = true; }
        }, overrides);
    }
    function element(rect, options = {}) {
        return Object.assign({
            nodeType: 1, getClientRects: () => [rect], getBoundingClientRect: () => rect
        }, options);
    }
    function choose(...elements) {
        context.selectedComponents = elements.map(dom => ({ dom: [dom], isSelected: true }));
        for (const observer of observers) observer.callback([]);
        while (animationFrames.length) animationFrames.shift()();
    }
    return {
        context, document, button, wrapper, node, attributes, event, element, choose, observers,
        trigger: (name, value = event()) => listeners.get(name)?.(value),
        check: (name, callback) => { callback(); checks++; console.log('PASS ' + name); }
    };
}

const f = fixture();
f.check('no selection disables the toolbar control', () => assert.equal(f.button.disabled, true));
f.check('localized accessible name and structure hint', () => {
    assert.equal(f.attributes['aria-label'], '定位选中组件（F）');
    assert.equal(f.node.title, '双击定位到画布');
});

const first = f.element({ left: 1300, right: 1500, top: 980, bottom: 1080 });
f.choose(first);
f.check('selection enables the control', () => assert.equal(f.button.disabled, false));
const selected = f.context.selectedComponents;
f.trigger('button:click');
f.check('button centers the rendered selection and accounts for scroll and viewport borders', () => {
    assert.equal(f.wrapper.scrollLeft, 1248);
    assert.equal(f.wrapper.scrollTop, 1198);
    assert.equal(f.context.selectedComponents, selected);
    assert.equal(selected[0].isSelected, true);
});

f.wrapper.scrollLeft = 0;
f.wrapper.scrollTop = 0;
// These viewport bounds describe a child inside a container at 200% CSS zoom.
f.choose(f.element({ left: 1702, right: 1902, top: 1082, bottom: 1182 }));
const key = f.event();
f.trigger('document:keydown', key);
f.check('F uses already rendered nested/zoomed bounds without scaling twice', () => {
    assert.equal(f.wrapper.scrollLeft, 1450);
    assert.equal(f.wrapper.scrollTop, 900);
    assert.equal(key.defaultPrevented, true);
});

f.wrapper.scrollLeft = 0;
f.wrapper.scrollTop = 0;
f.choose(
    f.element({ left: 302, right: 402, top: 282, bottom: 382 }),
    f.element({ left: 1302, right: 1502, top: 982, bottom: 1182 })
);
f.trigger('button:click');
f.check('multiple components center their union rather than the last item', () => {
    assert.equal(f.wrapper.scrollLeft, 550);
    assert.equal(f.wrapper.scrollTop, 500);
});

f.wrapper.scrollLeft = 0;
f.wrapper.scrollTop = 0;
f.choose(f.element({ left: -100, right: -50, top: 5000, bottom: 5100 }));
f.trigger('button:click');
f.check('scrolling clamps to the canvas range', () => {
    assert.equal(f.wrapper.scrollLeft, 0);
    assert.equal(f.wrapper.scrollTop, 2700);
});

for (const [name, properties] of [
    ['Ctrl', { ctrlKey: true }], ['Meta', { metaKey: true }],
    ['Alt', { altKey: true }], ['Shift', { shiftKey: true }],
    ['IME', { isComposing: true }], ['handled event', { defaultPrevented: true }],
    ['another key', { key: 'g' }],
    ['form input', { target: { matches: () => true, closest: () => null } }],
    ['editable descendant', { target: { matches: () => false, closest: () => ({}) } }]
]) {
    f.wrapper.scrollLeft = 123;
    f.wrapper.scrollTop = 456;
    f.trigger('document:keydown', f.event(properties));
    f.check(name + ' leaves canvas scrolling unchanged', () => {
        assert.equal(f.wrapper.scrollLeft, 123);
        assert.equal(f.wrapper.scrollTop, 456);
    });
}

for (const [name, apply, reset] of [
    ['open modal', () => f.document.modal = {}, () => f.document.modal = null],
    ['active drag/place operation', () => f.context.longAction = {}, () => f.context.longAction = null]
]) {
    apply();
    f.trigger('document:keydown');
    f.check(name + ' suppresses F navigation', () => {
        assert.equal(f.wrapper.scrollLeft, 123);
        assert.equal(f.wrapper.scrollTop, 456);
    });
    reset();
}

f.choose(f.element({ left: 1300, right: 1500, top: 980, bottom: 1080 }));
const doubleClickSelection = f.context.selectedComponents;
f.trigger('structure:dblclick', f.event({ target: f.node }));
f.check('structure double-click reuses the existing selection', () => {
    assert.equal(f.context.selectedComponents, doubleClickSelection);
    assert.equal(f.wrapper.scrollLeft, 1171);
    assert.equal(f.wrapper.scrollTop, 1254);
});

f.choose(f.element({ left: 0, right: 0, top: 0, bottom: 0 }, { getClientRects: () => [] }));
f.trigger('button:click');
f.check('hidden component does not move the viewport', () => assert.equal(f.wrapper.scrollLeft, 1171));
f.choose();
f.check('clearing selection disables the control again', () => assert.equal(f.button.disabled, true));
const noSelectionKey = f.event();
f.trigger('document:keydown', noSelectionKey);
f.check('F without a target retains its default behavior', () => assert.equal(noSelectionKey.defaultPrevented, undefined));

const loading = fixture({ readyState: 'loading', lang: 'en' });
loading.trigger('document:DOMContentLoaded');
loading.check('initialization waits for a head-loaded DOM and uses English labels', () => {
    assert.equal(loading.attributes['aria-label'], 'Center selected components (F)');
    assert.equal(loading.node.title, 'Double-click to locate on the canvas');
});

const requested = fixture({ query: '?componentID=11&other=value#details' });
const requestedElement = requested.element({ left: 1300, right: 1500, top: 980, bottom: 1080 });
const requestedComponent = { id: 11, dom: [requestedElement] };
requested.context.mimic = { componentMap: new Map([[11, requestedComponent]]) };
requested.context.selectComponent = component => requested.context.selectedComponents = [component];
requested.wrapper.canvas = { style: {} };
requested.choose();
requested.check('query navigation waits until the saved canvas zoom is applied', () => {
    assert.equal(requested.context.selectedComponents.length, 0);
});
requested.wrapper.canvas.style.zoom = '1.5';
requested.choose();
requested.check('query selects and centers the component once after loading', () => {
    assert.equal(requested.context.selectedComponents[0], requestedComponent);
    assert.equal(requested.wrapper.scrollLeft, 1248);
    assert.equal(requested.context.window.history.url, 'http://localhost/MimicEdit/1?other=value#details');
    assert(requested.observers[1].disconnected);
});
requested.choose();
requested.check('later redraws do not restore the consumed query selection', () => {
    assert.equal(requested.context.selectedComponents.length, 0);
});
const unknown = fixture({ query: '?componentID=999' });
unknown.context.mimic = { componentMap: new Map() };
unknown.wrapper.canvas = { style: { zoom: '1' } };
unknown.choose();
unknown.check('an unknown component query is safely consumed', () => {
    assert.equal(unknown.context.selectedComponents.length, 0);
    assert.equal(unknown.context.window.history.url, 'http://localhost/MimicEdit/1');
});
console.log(`${checks} canvas navigation checks passed`);
