// Run against the real mimic model/factories. No browser or third-party test package required.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const mimic = path.join(root, 'ScadaWeb/Mimics/PlgMimic/wwwroot/plugins/Mimic/js');
const industrial = path.join(root, 'ScadaWeb/Mimics/PlgMimBasicComp/wwwroot/plugins/MimBasicComp/js/industrial.js');
const scope = vm.createContext({
    console,
    document: {
        documentElement: {lang: 'zh-Hans'},
        createElement: () => ({
            classList: {add() {}, remove() {}, toggle() {}}, style: {}, id: '', textContent: '',
            setAttribute() {}, getBoundingClientRect: () => ({width: 120, height: 36})
        }),
        body: {append() {}}
    },
    window: {addEventListener() {}, innerWidth: 1280, innerHeight: 800},
    setInterval, clearInterval, setTimeout, clearTimeout
});
for (const name of ['mimic-common', 'mimic-descr', 'mimic-model', 'mimic-model-subtypes', 'mimic-factory', 'mimic-render']) {
    vm.runInContext(fs.readFileSync(path.join(mimic, name + '.js'), 'utf8'), scope, {filename: name + '.js'});
}
vm.runInContext(fs.readFileSync(industrial, 'utf8'), scope, {filename: 'industrial.js'});
vm.runInContext(fs.readFileSync(path.join(path.dirname(industrial), 'operations.js'), 'utf8'), scope, {filename: 'operations.js'});
vm.runInContext(fs.readFileSync(path.join(path.dirname(industrial), 'power-electrical.js'), 'utf8'), scope, {filename: 'power-electrical.js'});
const M = scope.rs.mimic;
assert.equal(M.IndustrialCatalog.length, 73);
for (const spec of M.IndustrialCatalog) {
    const factory = M.FactorySet.componentFactories.get(spec.type);
    assert(factory && M.RendererSet.componentRenderers.has(spec.type) && M.DescriptorSet.componentDescriptors.has(spec.type), spec.type + ' registered');
    const c = factory.createComponent();
    c.id = 37;
    c.properties.name = '中文设备';
    c.properties.location.x = 123;
    const restored = factory.createComponentFromSource(JSON.parse(JSON.stringify(c.toPlainObject())));
    assert.equal(restored.id, 37);
    assert.equal(restored.properties.id, 37);
    assert.equal(restored.properties.name, '中文设备');
    assert.equal(restored.properties.location.x, 123);
    for (const field of Object.keys(spec.defaults)) assert.deepEqual(restored.properties[field], c.properties[field], spec.type + '.' + field);
    assert(fs.existsSync(path.join(path.dirname(industrial), '../images/industrial-' + spec.key + '.svg')));
}
console.log('PASS 73 industrial component registrations, icons and save/reopen round trips (79 total palette types)');
assert.equal(M.Operations.faultDefinition('{"7":{"description":"过载","action":"检查负载"}}', 7).description, '过载');
assert.equal(M.Operations.faultDefinition('{}', 7), null);
assert.throws(() => M.Operations.faultDefinition('[]', 7));
assert.equal(M.Operations.faultDefinition('{"7":{"description":"缺少建议"}}', 7), null);
assert.equal(M.Operations.csvCell('=1+1'), '"\'=1+1"');
assert.equal(M.Operations.csvCell('泵"一'), '"泵""一"');
console.log('PASS Fault definition validation, unknown codes and spreadsheet-safe CSV fields');
const gauge = M.FactorySet.componentFactories.get('IndustrialGauge').createComponent();
gauge.bindings = {inCnlNum: 201};
const update = (val, stat) => {
    const args = {component: gauge, dataProvider: {getCurData: () => ({d: {val, stat}})}, propertyChanged: false};
    gauge.extraScript.dataUpdated(args);
    return args;
};
assert(update(72.5, 13).propertyChanged);
assert.equal(gauge.properties.value, 72.5);
assert.equal(gauge.customData.industrial.valid, true);
assert(update(NaN, 0).propertyChanged);
assert.equal(gauge.customData.industrial.valid, false);
assert.equal(gauge.properties.value, 72.5); // invalid data must never display as a real zero
assert(update(0, 13).propertyChanged);
assert.equal(gauge.properties.value, 0);
assert.equal(gauge.customData.industrial.valid, true);
console.log('PASS Live data, invalid quality and valid zero recovery');

// Inspect generated HTML from the real renderers without mocking their drawing logic.
function paint(type, props, customData, preview = true) {
    const c = M.FactorySet.componentFactories.get(type).createComponent();
    Object.assign(c.properties, props);
    c.customData = customData;
    let html = '';
    const target = {html: value => {html = value;}};
    M.RendererSet.componentRenderers.get(type).paint({children: () => target}, c, {editMode: preview});
    return html;
}
assert(paint('IndustrialProgress', {value: 150, minimum: 0, maximum: 100}).includes('width:100.00%'));
assert(paint('IndustrialLevel', {value: -10, minimum: 0, maximum: 100}).includes('height:0.00%'));
assert(paint('IndustrialGauge', {minimum: 5, maximum: 5}).includes('最大值必须大于最小值'));
const dangerous = '<img src=x onerror=alert(1)>';
assert(!paint('IndustrialDataTable', {labels: dangerous}, null).includes(dangerous));
assert(!paint('IndustrialAlarmList', {}, {industrial: {remote: {records: [{e: {cnlNum: 201}, ef: {descr: dangerous}}]}}}, false).includes(dangerous));
const trend = {timestamps: [0, 60000, 120000].map(ms => ({ms, ut: new Date(ms).toISOString()})), trends: [[{d: {val: 10, stat: 13}}, {d: {val: 0, stat: 0}}, {d: {val: 40, stat: 13}}]]};
const html = paint('IndustrialTrend', {}, {industrial: {remote: trend}}, false);
assert(html.includes('M10.00,85.60 M290.00,60.40'), 'bad-quality history must leave a gap');
assert(!html.includes('NaN'));
console.log('PASS Range clamps, invalid ranges, HTML escaping and trend gaps');

// Extended set: every new component must paint real markup, escaped and without NaN.
function paintRich(type, props, customData, context = {editMode: true}) {
    const c = M.FactorySet.componentFactories.get(type).createComponent();
    Object.assign(c.properties, props);
    c.customData = customData;
    let html = '';
    const node = {
        length: 0,
        html: value => { if (value !== undefined) html += String(value); return node; },
        find: () => node, not: () => node, attr: () => node, text: () => node, prop: () => node,
        val: () => '', is: () => false, each: () => node, on: () => node, off: () => node,
        addClass: () => node, removeClass: () => node, toggleClass: () => node,
        css: () => node, closest: () => node, children: () => node, append: () => node
    };
    M.RendererSet.componentRenderers.get(type).paint({children: () => node}, c, context);
    assert(html.length > 30, type + ' produced markup');
    assert(!html.includes('undefined') && !html.includes('NaN'), type + ' has no undefined/NaN');
    return html;
}
const extended = ['Tee', 'Elbow', 'DoubleArrow', 'Flange', 'Thermometer', 'PressureGauge', 'FlowMeter',
    'EnergyMeter', 'BarChart', 'PieChart', 'Checkbox', 'RadioGroup', 'ButtonGroup', 'AlarmBanner',
    'StatusBadge', 'KpiCard', 'HeatExchanger', 'Compressor', 'Boiler', 'Conveyor', 'Filter',
    'CoolingTower', 'GroupBox', 'Divider', 'EmergencyStop', 'KeySwitch', 'SelectorSwitch',
    'AlarmAckButton', 'StackLight', 'ControlValve', 'TrendMulti'];
for (const key of extended) paintRich('Industrial' + key, {});
assert(paintRich('IndustrialBarChart', {channels: '201,204,301', labels: '储罐,水泵,温度'}).includes('ic-bar-fill'));
assert(paintRich('IndustrialPieChart', {channels: '201,204,301', labels: '储罐,水泵,温度'}).includes('ic-legend'));
assert(paintRich('IndustrialCheckbox', {}).includes('ic-choice'));
assert(paintRich('IndustrialRadioGroup', {options: '低速=0;中速=1'}).includes('ic-radio'));
assert(paintRich('IndustrialButtonGroup', {options: '启动=1;停止=0'}).includes('ic-cmd-row'));
assert(paintRich('IndustrialGroupBox', {}).includes('ic-group-title'));
assert(paintRich('IndustrialDivider', {}).includes('ic-divider'));
assert(paintRich('IndustrialKpiCard', {value: 72, target: 90}).includes('ic-kpi-mark'));
assert(paintRich('IndustrialStatusBadge', {value: 5}).includes('ic-badge'));
assert(paintRich('IndustrialThermometer', {value: 150, minimum: 0, maximum: 100}).includes('height="52.00"'));
const banner = paintRich('IndustrialAlarmBanner', {channels: '201'},
    {industrial: {remote: [{cnlNum: 201, val: 95, stat: 12}]}}, {editMode: false});
assert(banner.includes('ic-banner-alarm'), 'alarm status must raise the banner');
assert(paintRich('IndustrialAlarmBanner', {channels: '201'},
    {industrial: {remote: [{cnlNum: 201, val: 10, stat: 13}]}}, {editMode: false}).includes('ic-banner-ok'));
assert(paintRich('IndustrialPressureGauge', {minimum: 0, maximum: 0}).includes('最大值必须大于最小值'));
const injected = paintRich('IndustrialDivider', {label: dangerous});
assert(!injected.includes(dangerous), 'group titles must be escaped');
assert(paintRich('IndustrialEmergencyStop', {}).includes('ic-estop'));
assert(paintRich('IndustrialKeySwitch', {}).includes('ic-keyswitch'));
assert(paintRich('IndustrialSelectorSwitch', {options: '手动=1;停止=0;自动=2'}).includes('ic-selector'));
assert(paintRich('IndustrialAlarmAckButton', {}).includes('data-field="event"'));
assert(paintRich('IndustrialControlValve', {value: 45}).includes('ic-value'));
assert(paintRich('IndustrialTrendMulti', {channels: '201,202', labels: '液位,流量'}).includes('ic-legend'));
assert(paintRich('IndustrialStackLight', {channels: '201,202,203'}).includes('ic-stack-green on'));
const stack = paintRich('IndustrialStackLight', {channels: '201'},
    {industrial: {remote: [{cnlNum: 201, val: 95, stat: 12}]}}, {editMode: false});
assert(stack.includes('ic-stack-red on'), 'alarm status must light the red lamp');
const eventRecord = {id: '7699416646178440758', e: {cnlNum: 201, ack: false, ackRequired: true}, ef: {descr: '高高限', time: '12:00', cnl: '液位'}};
const alarmContext = {editMode: false, controlRight: true, mainApi: {rootPath: '/scada/'}};
const alarmList = paintRich('IndustrialAlarmList', {archiveBit: 4}, {industrial: {remote: {records: [eventRecord]}}}, alarmContext);
assert(alarmList.includes('/scada/Main/EventAck?eventID=7699416646178440758&amp;archiveBit=4'), 'event ID must retain all digits and the configured application root');
assert(alarmList.includes('确认不代表恢复正常'));
assert(!paintRich('IndustrialAlarmList', {archiveBit: 4}, {industrial: {remote: {records: [eventRecord]}}}, {...alarmContext, controlRight: false}).includes('ic-ack-link'), 'read-only users have no acknowledgement action');
assert(!paintRich('IndustrialKpiCard', {value: 55}, {industrial: {valid: false}}, {editMode: false}).includes('>55.0<'), 'invalid KPI data must not look live');
console.log('PASS 31 extended components paint markup, alarm states and escaping');

// Tooltip: runtime replaces the native title with a multi-line bubble, editor keeps the title.
function fakeHost() {
    const attrs = {}, events = new Set();
    const host = {
        attrs, events, length: 1,
        attr: (name, value) => { if (value === undefined) return attrs[name]; attrs[name] = value; return host; },
        removeAttr: name => { delete attrs[name]; return host; },
        off: () => { events.clear(); return host; },
        on: name => { events.add(name); return host; }
    };
    host[0] = {getBoundingClientRect: () => ({left: 10, top: 40, width: 120, height: 60, bottom: 100})};
    return host;
}
const tooltip = M.IndustrialTooltip;
const tipComponent = M.FactorySet.componentFactories.get('IndustrialPump').createComponent();
tipComponent.id = 99;
const runtimeHost = fakeHost();
tipComponent.properties.tooltip = '水泵\n通道 204';
tooltip.bind(runtimeHost, tipComponent, {editMode: false});
assert.equal(runtimeHost.attrs.title, undefined, 'runtime must drop the native title');
assert.equal(tooltip.hosts.get(99).textContent, '水泵\n通道 204', 'tooltip keeps line breaks');
assert(runtimeHost.events.has('mouseenter.ictip') && runtimeHost.events.has('touchstart.ictip'));
assert.equal(runtimeHost.attrs['aria-describedby'], 'ic-tip-99');
const before = runtimeHost.events.size;
tooltip.bind(runtimeHost, tipComponent, {editMode: false});
assert.equal(runtimeHost.events.size, before, 'unchanged tooltip must not rebind');
const editorHost = fakeHost();
tooltip.bind(editorHost, tipComponent, {editMode: true});
assert(editorHost.events.size === 0, 'the editor canvas must not bind hover bubbles');
const emptyHost = fakeHost();
tipComponent.properties.tooltip = '   ';
tooltip.bind(emptyHost, tipComponent, {editMode: false});
assert.equal(emptyHost.attrs['aria-describedby'], undefined);
assert.equal(emptyHost.events.size, 0);
console.log('PASS Tooltip binding, native title replacement and rebind guard');
scope.window.industrialLanguage = 'en-GB';
vm.runInContext(fs.readFileSync(industrial, 'utf8'), scope);
vm.runInContext(fs.readFileSync(path.join(path.dirname(industrial), 'operations.js'), 'utf8'), scope);
vm.runInContext(fs.readFileSync(path.join(path.dirname(industrial), 'power-electrical.js'), 'utf8'), scope);
assert.equal(M.FactorySet.componentFactories.get('IndustrialCircuitBreaker').createProperties().label, 'Circuit breaker');
assert.equal(M.FactorySet.componentFactories.get('IndustrialGauge').createProperties().label, 'Gauge');
assert.equal(M.FactorySet.componentFactories.get('IndustrialSelect').createProperties().options, 'Off=0;On=1');
assert.equal(M.FactorySet.componentFactories.get('IndustrialNavBar').createProperties().label, 'Page navigation');
assert.equal(M.FactorySet.componentFactories.get('IndustrialTabs').createComponent().isContainer, true);
assert.equal(M.FactorySet.componentFactories.get('IndustrialCollapsiblePanel').createComponent().isContainer, true);
console.log('PASS Component defaults follow the selected language');

// Exercise the real asynchronous command flow, including delayed feedback and duplicate controls.
(async () => {
    const commands = M.IndustrialCommands;
    const c = M.FactorySet.componentFactories.get('IndustrialNumberInput').createComponent();
    c.bindings = {inCnlNum: 201, outCnlNum: 205};
    let ticks = 0, sends = 0, reads = 0;
    const messages = [];
    const context = {editMode: false, controlRight: true, mainApi: {
        rootPath: '/', sendCommand: (cnl, value, hex, data, callback) => { sends++; assert.equal(cnl, 205); callback({ok: true}); }
    }};
    const deps = {now: () => ticks, delay: async ms => {ticks += ms;}, timeout: 1800,
        request: async () => [{cnlNum: 201, val: ++reads === 1 ? 20 : 55, stat: 13}]};
    assert.equal(await commands.sendAndConfirm(c, context, 55, (text, ok) => messages.push({text, ok}), deps), true);
    assert.equal(sends, 1); assert.equal(reads, 2);
    assert.equal(messages.at(-1).ok, true);
    assert(messages.some(m => /awaiting|等待/.test(m.text)), 'accepted command must first wait for readback');

    ticks = 0; reads = 0;
    assert.equal(await commands.sendAndConfirm(c, context, 55, () => {}, {...deps,
        request: async () => {reads++; return [{cnlNum: 201, val: 55, stat: 0}];}}), false, 'invalid quality cannot confirm execution');
    assert(reads > 1);
    assert.equal(commands.isBusy(c, context), false, 'timeout must release the command lock');

    let finishSend;
    context.mainApi.sendCommand = (cnl, value, hex, data, callback) => {finishSend = callback;};
    ticks = 0;
    const first = commands.sendAndConfirm(c, context, 55, () => {}, {...deps, request: async () => [{cnlNum: 201, val: 55, stat: 13}]});
    assert.equal(commands.isBusy(c, context), true);
    const other = M.FactorySet.componentFactories.get('IndustrialSlider').createComponent();
    other.bindings = {outCnlNum: 205};
    assert.equal(await commands.sendAndConfirm(other, context, 56, () => {}, deps), false, 'another component cannot send to the pending output');
    finishSend({ok: true});
    assert.equal(await first, true);
    assert.equal(commands.isBusy(c, context), false);
    assert.equal(await commands.sendAndConfirm(c, {...context, controlRight: false}, 55, () => {}, deps), false);
    assert.equal(await commands.sendAndConfirm(c, {...context, editMode: true}, 55, () => {}, deps), false);
    context.mainApi.dataPaused = true;
    assert.equal(await commands.sendAndConfirm(c, context, 55, () => {}, deps), false);
    context.mainApi.dataPaused = false;
    context.mainApi.sendCommand = (cnl, value, hex, data, callback) => callback({ok: false, msg: 'denied'});
    assert.equal(await commands.sendAndConfirm(c, context, 55, () => {}, deps), false);
    assert.equal(commands.isBusy(c, context), false);
    console.log('PASS Command readback, invalid quality, timeout, duplicate output lock, failure and permission guards');
})().catch(error => { console.error(error); process.exitCode = 1; });

// Role filter: engineering-only components disappear for non-admins at runtime, never in the editor.
function roleHost() {
    const classes = new Set();
    const host = {
        classes,
        toggleClass: (name, on) => { if (on) classes.add(name); else classes.delete(name); return host; },
        removeClass: name => { classes.delete(name); return host; }
    };
    return host;
}
const roleComponent = M.FactorySet.componentFactories.get('IndustrialGauge').createComponent();
roleComponent.properties.roleFilter = 'admin';
const adminHost = roleHost();
scope.window.mimicIsAdmin = true;
M.IndustrialRoleFilter(adminHost, roleComponent, {editMode: false});
assert(!adminHost.classes.has('ic-role-hidden'), 'admin must see admin-only components');
scope.window.mimicIsAdmin = false;
assert(roleHost().classes.size === 0);
const guestHost = roleHost();
M.IndustrialRoleFilter(guestHost, roleComponent, {editMode: false});
assert(guestHost.classes.has('ic-role-hidden'), 'operator must not see admin-only components');
const roleEditorHost = roleHost();
M.IndustrialRoleFilter(roleEditorHost, roleComponent, {editMode: true});
assert(!roleEditorHost.classes.has('ic-role-hidden'), 'the editor must always show the component');
roleComponent.properties.roleFilter = 'all';
const allHost = roleHost();
M.IndustrialRoleFilter(allHost, roleComponent, {editMode: false});
assert(!allHost.classes.has('ic-role-hidden'), 'default role shows the component to everyone');
console.log('PASS Role filter hides admin-only components at runtime only');

// Number format: plain keeps the old output, thousand groups digits, compact scales units.
const fmt = M.IndustrialFormat;
assert.equal(fmt(1234567.891, {decimals: 2}), '1234567.89', 'plain format is unchanged');
assert.equal(fmt(1234567.891, {decimals: 2, numberFormat: 'thousand'}), '1,234,567.89');
assert.equal(fmt(-1234.5, {decimals: 1, numberFormat: 'thousand'}), '-1,234.5');
assert.equal(fmt(999, {decimals: 0, numberFormat: 'thousand'}), '999', 'no separator below 1000');
assert.equal(fmt(12345, {decimals: 1, numberFormat: 'compact'}), '12.3k');
assert.equal(fmt(2500000, {decimals: 2, numberFormat: 'compact'}), '2.50M');
assert.equal(fmt(NaN, {decimals: 2}), '—', 'invalid values stay empty');
console.log('PASS Number format: plain, thousands grouping and compact units');
