// Industrial component factories, descriptors and renderers. No external chart library is required.
// Loaded in both Mimic and MimicEditor after basic-bundle.js.
(() => {
    'use strict';
    const M = rs.mimic;
    const zh = (window.industrialLanguage || document.documentElement.lang || 'zh').startsWith('zh');
    const tr = (cn, en) => zh ? cn : en;
    const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const num = (value, fallback = 0) => Number.isFinite(Number(value)) && value !== '' ? Number(value) : fallback;
    const clamp = (value, low, high) => Math.min(high, Math.max(low, value));
    const fields = {
        label: ['Title', '标题', 'string', 'appearance'],
        value: ['Value / preview', '数值 / 预览值', 'float', 'data'],
        minimum: ['Minimum', '最小值', 'float', 'data'],
        maximum: ['Maximum', '最大值', 'float', 'data'],
        unit: ['Unit', '单位', 'string', 'appearance'],
        decimals: ['Decimal places', '小数位数', 'int', 'appearance'],
        activeColor: ['Active color', '运行颜色', 'string', 'appearance'],
        idleColor: ['Idle color', '停止颜色', 'string', 'appearance'],
        alarmColor: ['Alarm color', '报警颜色', 'string', 'appearance'],
        threshold: ['Active threshold', '运行判定阈值', 'float', 'behavior'],
        lineWidth: ['Line width', '线条宽度', 'float', 'appearance'],
        fillColor: ['Fill color', '填充颜色', 'string', 'appearance'],
        vertical: ['Vertical', '垂直方向', 'bool', 'layout'],
        animate: ['Animation', '启用动画', 'bool', 'behavior'],
        channels: ['Channels (comma separated)', '通道列表（逗号分隔）', 'string', 'data'],
        labels: ['Labels (comma separated)', '名称列表（逗号分隔）', 'string', 'data'],
        historyMinutes: ['History minutes', '历史时长（分钟）', 'int', 'data'],
        archiveBit: ['Archive bit', '归档位号', 'int', 'data'],
        maxRows: ['Maximum rows', '最大行数', 'int', 'layout'],
        options: ['Options (label=value;...)', '选项（名称=值;…）', 'string', 'data'],
        step: ['Step', '步长', 'float', 'data'],
        target: ['Target value', '目标值', 'float', 'data'],
        roleFilter: ['Visible for role', '可见角色（all / admin）', 'string', 'behavior'],
        numberFormat: ['Number format', '数字格式（plain / thousand / compact）', 'string', 'appearance'],
        integratePower: ['Integrate power', '按功率归档积分', 'bool', 'data'],
        units: ['Series units', '各曲线单位', 'string', 'data'],
        minimums: ['Series minima', '各曲线最小值', 'string', 'data'],
        maximums: ['Series maxima', '各曲线最大值', 'string', 'data']
    };
    const range = {value: 55, minimum: 0, maximum: 100, unit: '%', decimals: 1, activeColor: '#1689d8'};
    const device = {value: 0, threshold: 0, activeColor: '#159d83', idleColor: '#94a3b8', alarmColor: '#dc4848', animate: true};
    // Each entry is a real component type, with only the relevant properties exposed.
    const catalog = [
        ['Line', 'Line', '直线', 180, 30, {activeColor: '#526b86', lineWidth: 3, vertical: false}],
        ['Rectangle', 'Rectangle', '矩形', 150, 90, {activeColor: '#5281ae', lineWidth: 2, fillColor: '#e4edf7'}],
        ['Ellipse', 'Ellipse', '圆形', 100, 100, {activeColor: '#5281ae', lineWidth: 2, fillColor: '#e4edf7'}],
        ['Arrow', 'Arrow', '箭头', 160, 45, {activeColor: '#1689d8', lineWidth: 4, vertical: false}],
        ['Pipe', 'Pipe', '管道', 220, 40, {...device, lineWidth: 14, vertical: false}],
        ['Gauge', 'Gauge', '仪表盘', 210, 150, {...range}],
        ['Progress', 'Progress bar', '进度条', 240, 95, {...range}],
        ['Level', 'Level meter', '液位计', 135, 220, {...range}],
        ['Trend', 'Trend chart', '趋势图', 380, 215, {...range, historyMinutes: 60, archiveBit: 1}],
        ['DataTable', 'Data table', '数据表格', 360, 210, {channels: '', labels: '', decimals: 2, maxRows: 8}],
        ['NumberInput', 'Number input', '数值输入框', 250, 115, {...range, step: 1}],
        ['Select', 'Dropdown', '下拉框', 250, 115, {...range, value: 0, maximum: 1, unit: '', decimals: 0, step: 1, options: tr('关闭=0;开启=1', 'Off=0;On=1')}],
        ['Slider', 'Slider', '滑块', 300, 115, {...range, step: 1}],
        ['Pump', 'Pump', '水泵', 135, 125, {...device}],
        ['Valve', 'Valve', '阀门', 140, 105, {...device}],
        ['Fan', 'Fan', '风机', 125, 130, {...device}],
        ['Motor', 'Motor', '电机', 155, 115, {...device}],
        ['Tank', 'Tank', '储罐', 150, 220, {...range}],
        ['AlarmList', 'Alarm list', '报警列表', 520, 220, {channels: '', archiveBit: 4, maxRows: 8}],
        ['StatusGroup', 'Status lamps', '状态灯组', 360, 110, {channels: '', labels: '', ...device}],
        ['Tee', 'Tee', '三通', 120, 90, {...device, lineWidth: 14, vertical: false}],
        ['Elbow', 'Elbow', '弯头', 120, 120, {...device, lineWidth: 14, vertical: false}],
        ['DoubleArrow', 'Double arrow', '双向箭头', 160, 45, {activeColor: '#1689d8', lineWidth: 4, vertical: false}],
        ['Flange', 'Flange', '法兰', 200, 45, {activeColor: '#526b86', lineWidth: 6, fillColor: '#e4edf7', vertical: false}],
        ['Thermometer', 'Thermometer', '温度计', 95, 210, {...range, unit: '°C'}],
        ['PressureGauge', 'Pressure gauge', '压力表', 190, 180, {...range, value: 0.8, maximum: 1.6, unit: 'MPa', decimals: 2}],
        ['FlowMeter', 'Flow meter', '流量计', 170, 170, {...range, value: 120, maximum: 200, unit: 'm³/h'}],
        ['EnergyMeter', 'Energy meter', '电能表', 230, 165, {...range, value: 4280, maximum: 10000, unit: 'kWh', decimals: 1, integratePower: false, historyMinutes: 1440, archiveBit: 1}],
        ['BarChart', 'Bar chart', '柱状图', 330, 210, {channels: '', labels: '', unit: '', decimals: 1, activeColor: '#1689d8'}],
        ['PieChart', 'Pie chart', '饼图', 300, 220, {channels: '', labels: '', unit: '', decimals: 1, activeColor: '#1689d8'}],
        ['Checkbox', 'Checkbox', '复选框', 230, 95, {value: 0, minimum: 0, maximum: 1, step: 1, decimals: 0, unit: ''}],
        ['RadioGroup', 'Radio group', '单选组', 290, 165, {value: 0, minimum: 0, maximum: 2, step: 1, decimals: 0, unit: '', options: tr('低速=0;中速=1;高速=2', 'Low=0;Medium=1;High=2')}],
        ['ButtonGroup', 'Button group', '按钮组', 350, 105, {value: 0, minimum: 0, maximum: 2, step: 1, decimals: 0, unit: '', options: tr('启动=1;停止=0;复位=2', 'Start=1;Stop=0;Reset=2')}],
        ['AlarmBanner', 'Alarm banner', '报警横幅', 520, 95, {channels: '', labels: '', decimals: 0, unit: '', ...device}],
        ['StatusBadge', 'Status badge', '状态牌', 210, 95, {...device, minimum: 0, maximum: 100, decimals: 0, unit: ''}],
        ['KpiCard', 'KPI card', '指标卡', 280, 165, {...range, value: 72, target: 90, unit: '件/时'}],
        ['HeatExchanger', 'Heat exchanger', '换热器', 175, 125, {...device}],
        ['Compressor', 'Compressor', '压缩机', 150, 135, {...device}],
        ['Boiler', 'Boiler', '锅炉', 145, 205, {...device}],
        ['Conveyor', 'Conveyor', '传送带', 245, 120, {...device}],
        ['Filter', 'Filter', '过滤器', 135, 165, {...device}],
        ['CoolingTower', 'Cooling tower', '冷却塔', 150, 200, {...device}],
        ['GroupBox', 'Group box', '分组框', 420, 260, {activeColor: '#8aa0b9', lineWidth: 1, fillColor: 'none'}],
        ['Divider', 'Divider', '分隔线', 320, 40, {activeColor: '#b8c7d8', lineWidth: 2, vertical: false}],
        ['EmergencyStop', 'Emergency stop', '急停按钮', 150, 165, {value: 0, minimum: 0, maximum: 1, step: 1, decimals: 0, unit: '', activeColor: '#dc2626', idleColor: '#8f1d1d', threshold: 0}],
        ['KeySwitch', 'Key switch', '钥匙开关', 150, 145, {value: 0, minimum: 0, maximum: 1, step: 1, decimals: 0, unit: ''}],
        ['SelectorSwitch', 'Selector switch', '选择开关', 180, 155, {value: 1, minimum: 0, maximum: 2, step: 1, decimals: 0, unit: '', options: tr('手动=1;停止=0;自动=2', 'Manual=1;Stop=0;Auto=2')}],
        ['AlarmAckButton', 'Alarm acknowledge', '报警确认', 340, 180, {channels: '', archiveBit: 4}],
        ['StackLight', 'Stack light', '报警灯柱', 110, 235, {channels: '', labels: tr('红,黄,绿', 'Red,Amber,Green'), threshold: 0, decimals: 0, unit: ''}],
        ['ControlValve', 'Control valve', '调节阀', 165, 155, {...device, value: 45, minimum: 0, maximum: 100, decimals: 0, unit: '%', activeColor: '#1689d8'}],
        ['TrendMulti', 'Multi-channel trend', '多通道趋势', 480, 260, {channels: '', labels: '', minimum: 0, maximum: 100, unit: '%', units: '', minimums: '', maximums: '', decimals: 1, activeColor: '#1689d8', historyMinutes: 60, archiveBit: 1}]
    ].map(([key, en, cn, width, height, defaults]) => ({key, en, cn, width, height, type: 'Industrial' + key, defaults: {label: tr(cn, en), roleFilter: 'all', numberFormat: 'plain', ...defaults}}));
    M.IndustrialCatalog = catalog;
    const inputTypes = new Set(['NumberInput', 'Select', 'Slider']);
    const choiceTypes = new Set(['Checkbox', 'RadioGroup']);
    const commandTypes = new Set(['ButtonGroup']);
    const safetyTypes = new Set(['EmergencyStop', 'KeySwitch', 'SelectorSwitch', 'AlarmAckButton']);
    const shapeTypes = new Set(['Line', 'Rectangle', 'Ellipse', 'Arrow', 'Pipe', 'Tee', 'Elbow', 'DoubleArrow', 'Flange']);
    const layoutTypes = new Set(['GroupBox', 'Divider']);
    const remoteTypes = new Set(['Trend', 'DataTable', 'AlarmList', 'StatusGroup', 'BarChart', 'PieChart', 'AlarmBanner', 'StackLight', 'TrendMulti']);
    const equipment = new Set(['Pump', 'Valve', 'Fan', 'Motor', 'HeatExchanger', 'Compressor', 'Boiler', 'Conveyor', 'Filter', 'CoolingTower']);
    const groupDigits = text => {
        const parts = String(text).split('.');
        const sign = parts[0].startsWith('-') ? '-' : '';
        const digits = sign ? parts[0].slice(1) : parts[0];
        const grouped = digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
        return sign + grouped + (parts.length > 1 ? '.' + parts[1] : '');
    };
    const compactNumber = (value, decimals) => {
        const units = [[1e9, 'G'], [1e6, 'M'], [1e3, 'k']];
        for (const [base, suffix] of units) {
            if (Math.abs(value) >= base) return (value / base).toFixed(decimals) + suffix;
        }
        return value.toFixed(decimals);
    };
    const fmt = (v, p) => {
        if (!Number.isFinite(Number(v))) return '—';
        const decimals = clamp(Math.round(num(p.decimals)), 0, 6);
        const mode = String(p.numberFormat || 'plain').toLowerCase();
        if (mode === 'compact') return compactNumber(Number(v), decimals);
        const text = Number(v).toFixed(decimals);
        return mode === 'thousand' ? groupDigits(text) : text;
    };
    const channelList = p => [...new Set(String(p.channels || '').split(/[,，;；\s]+/).map(Number).filter(v => Number.isInteger(v) && v > 0))].slice(0, 16);
    const optionList = p => String(p.options || '').split(/[;；\n]/).map(s => {
        const i = s.lastIndexOf('=');
        return {label: s.slice(0, i).trim(), value: Number(s.slice(i + 1))};
    }).filter(o => o.label && Number.isFinite(o.value)).slice(0, 50);
    const valid = c => c.customData?.industrial?.valid !== false;
    const ratio = p => p.maximum > p.minimum ? clamp((num(p.value) - p.minimum) / (p.maximum - p.minimum), 0, 1) : 0;
    const color = c => !valid(c) ? '#94a3b8' : state(c).alarm ? c.properties.alarmColor : num(c.properties.value) > num(c.properties.threshold) ? c.properties.activeColor : c.properties.idleColor;
    const svg = body => `<svg viewBox="0 0 100 90" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">${body}</svg>`;
    const title = (p, note = '') => `<div class="ic-title"><span>${esc(p.label)}</span><span class="ic-muted">${esc(note)}</span></div>`;
    const empty = (message, error = false) => `<div class="ic-empty ${error ? 'ic-error' : ''}">${esc(message)}</div>`;
    const state = c => c.customData?.industrial || {};

    // Shared by controls bound to the same output, including controls on different components.
    const pendingCommands = new Set();
    const commandKey = (c, context) => (context.mainApi?.rootPath || '/') + ':' + (c.bindings?.outCnlNum || c.properties.outCnlNum);
    const commandBusy = (c, context) => pendingCommands.has(commandKey(c, context));
    async function sendAndConfirm(c, context, value, feedback, dependencies = {}) {
        const output = c.bindings?.outCnlNum || c.properties.outCnlNum;
        if (context.editMode || !context.controlRight || context.mainApi?.dataPaused || !c.properties.enabled || !(output > 0) || !Number.isFinite(value)) return false;
        const key = commandKey(c, context);
        if (pendingCommands.has(key)) return false;
        const read = dependencies.request || request;
        const delay = dependencies.delay || (ms => new Promise(resolve => setTimeout(resolve, ms)));
        const now = dependencies.now || Date.now;
        const timeout = dependencies.timeout ?? 15000;
        pendingCommands.add(key);
        try {
            feedback(tr('正在发送…', 'Sending…'));
            const dto = await new Promise((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error(tr('发送超时，执行结果未知，请检查反馈后再操作', 'Send timed out; check feedback before retrying'))), 10000);
                try {
                    context.mainApi.sendCommand(output, value, false, null, result => { clearTimeout(timer); resolve(result); });
                } catch (error) { clearTimeout(timer); reject(error); }
            });
            if (!dto.ok) throw new Error(dto.msg || tr('发送失败', 'Command failed'));
            feedback(tr('命令已发送，等待反馈…', 'Command sent, awaiting feedback…'));
            const input = c.bindings?.inCnlNum || c.properties.inCnlNum || output;
            const tolerance = input === output ? 0.000001 : Math.max(0.000001, num(c.properties.step, 0) / 2);
            const deadline = now() + timeout;
            while (now() < deadline) {
                await delay(600);
                try {
                    const records = await read(context, 'GetCurData', {cnlNums: input});
                    const record = records.find(r => r.cnlNum === input);
                    if (record && [1, 11, 12, 13, 14, 15].includes(record.stat) && Number.isFinite(record.val) && Math.abs(record.val - value) <= tolerance) {
                        feedback(input === output ? tr('命令通道回读已确认', 'Command channel readback confirmed') : tr('采集反馈已确认', 'Acquired feedback confirmed'), true);
                        return true;
                    }
                } catch { /* A transient read failure must not resend the command. */ }
            }
            throw new Error(tr('反馈超时，执行结果未知，请检查设备状态', 'Feedback timed out; check device state'));
        } catch (error) {
            feedback(error.message, false);
            return false;
        } finally {
            pendingCommands.delete(key);
        }
    }
    M.IndustrialCommands = {sendAndConfirm, isBusy: commandBusy};
    M.IndustrialSupport = {request, esc, tr, num, clamp, channelList};

    class IndustrialScript extends M.ComponentScript {
        dataUpdated(args) {
            const c = args.component;
            const cnl = c.bindings?.inCnlNum;
            if (cnl > 0) {
                const data = args.dataProvider.getCurData(cnl);
                const next = data?.d?.val;
                const isValid = data?.d?.stat > 0 && Number.isFinite(next);
                const old = state(c);
                const alarm = [11, 12, 14, 15].includes(data?.d?.stat);
                const valueBound = c.bindings.propertyBindings?.some(binding => binding.propertyName === 'value');
                if (old.valid !== isValid || old.alarm !== alarm || (!valueBound && c.properties.value !== next)) {
                    c.customData ??= {};
                    c.customData.industrial = {...old, valid: isValid, alarm};
                    if (isValid && !valueBound) c.properties.value = next;
                    args.propertyChanged = true;
                }
            }
        }
    }

    class IndustrialFactory extends M.RegularComponentFactory {
        constructor(spec) { super(); this.spec = spec; }
        createProperties() {
            const p = super.createProperties();
            Object.assign(p, this.spec.defaults);
            p.size = new M.Size({width: this.spec.width, height: this.spec.height});
            p.foreColor = '#213047';
            if (equipment.has(this.spec.key) || this.spec.key === 'ControlValve') {
                p.clickAction.actionType = M.ActionType.SEND_COMMAND;
                p.clickAction.commandArgs.showDialog = true;
            }
            return p;
        }
        parseProperties(source) {
            const p = super.parseProperties(source);
            for (const [key, fallback] of Object.entries(this.spec.defaults)) {
                const v = source?.[key] ?? fallback;
                p[key] = typeof fallback === 'boolean' ? M.PropertyParser.parseBool(v) :
                    typeof fallback === 'number' ? num(v, fallback) : String(v);
            }
            p.decimals = clamp(Math.round(num(p.decimals)), 0, 6);
            return p;
        }
        _copyProperties(component, source) {
            super._copyProperties(component, source);
            component.properties.id = component.id;
        }
        _createExtraScript() { return new IndustrialScript(); }
        createComponent() { return super.createComponent(this.spec.type); }
    }

    // Shared scheduler: no per-component timers; detached components are removed on the next tick.
    const remote = new Map();
    let timer = null;
    async function request(context, method, params) {
        const root = context.mainApi?.rootPath || '/';
        const response = await fetch(root + 'Api/Main/' + method + '?' + new URLSearchParams(params),
            {signal: AbortSignal.timeout(15000)});
        if (!response.ok) throw new Error(tr('请求失败：', 'Request failed: ') + response.status);
        const dto = await response.json();
        if (!dto.ok) throw new Error(dto.msg || tr('读取数据失败', 'Unable to read data'));
        return dto.data;
    }
    async function pollOne(entry) {
        const {component: c, context, renderer} = entry;
        if (entry.busy) return;
        entry.busy = true;
        const p = c.properties, key = renderer.spec.key;
        try {
            let data;
            if (key === 'EnergyMeter' && p.integratePower) {
                data = await M.Operations.api(context, 'Energy', {channel: c.bindings?.inCnlNum || p.inCnlNum, archiveBit: p.archiveBit, minutes: p.historyMinutes});
            } else if (key === 'Trend' || key === 'TrendMulti') {
                const cnls = key === 'TrendMulti' ? channelList(p) : [c.bindings?.inCnlNum || p.inCnlNum].filter(v => v > 0);
                if (!cnls.length) throw new Error(tr(key === 'TrendMulti' ? '请设置通道列表' : '请设置输入通道', key === 'TrendMulti' ? 'Set the channel list' : 'Set the input channel'));
                const end = new Date(), start = new Date(end - clamp(num(p.historyMinutes, 60) * (state(c).historyZoom || 1), 1, 1440) * 60000);
                data = await request(context, 'GetHistData', {archiveBit: p.archiveBit, startTime: start.toISOString(), endTime: end.toISOString(), endInclusive: true, cnlNums: cnls.join(',')});
            } else if (key === 'AlarmList') {
                data = await request(context, 'GetLastAvailableEvents', {archiveBit: p.archiveBit, period: 2, limit: clamp(num(p.maxRows, 8) * 10, 1, 500), filterID: 0});
            } else {
                const cnls = channelList(p);
                if (!cnls.length) throw new Error(tr('请设置通道列表', 'Set the channel list'));
                data = await request(context, 'GetCurData', {cnlNums: cnls.join(',')});
            }
            c.customData ??= {};
            c.customData.industrial = {...state(c), remote: data, error: '', lastRead: Date.now()};
        } catch (error) {
            c.customData ??= {};
            c.customData.industrial = {...state(c), remote: null, error: String(error.message)};
        } finally {
            entry.busy = false;
            if (c.dom?.[0]?.isConnected) renderer.paint(c.dom, c, context);
        }
    }
    function registerRemote(c, context, renderer) {
        if (context.editMode) return;
        let entry = remote.get(c);
        if (!entry) {
            entry = {component: c, context, renderer, busy: false, next: Date.now() + 1000};
            remote.set(c, entry);
            // DOM is inserted by the native renderer immediately after _setProps returns.
        }
        if (!timer) timer = setInterval(() => {
            const now = Date.now();
            for (const [component, item] of remote) {
                if (!component.dom?.[0]?.isConnected) { remote.delete(component); continue; }
                if (now >= item.next) {
                    item.next = now + (['Trend', 'TrendMulti'].includes(item.renderer.spec.key) ? 30000 : 5000);
                    void pollOne(item);
                }
            }
            if (!remote.size) { clearInterval(timer); timer = null; }
        }, 1000);
    }

    function shapeMarkup(key, p, c) {
        const stroke = esc(p.activeColor), fill = esc(p.fillColor || 'none');
        const w = clamp(num(p.lineWidth, 3), 1, 30);
        let body;
        if (key === 'Rectangle') body = `<rect x="3" y="3" width="94" height="84" rx="3" fill="${fill}" stroke="${stroke}" stroke-width="${w}"/>`;
        else if (key === 'Ellipse') body = `<ellipse cx="50" cy="45" rx="46" ry="41" fill="${fill}" stroke="${stroke}" stroke-width="${w}"/>`;
        else {
            const col = esc(color(c));
            const running = p.animate && num(p.value) > p.threshold && valid(c) ? 'running' : '';
            const tube = path => `<path d="${path}" fill="none" stroke="#8092a6" stroke-width="${w + 4}"/><path d="${path}" fill="none" stroke="#e3eaf2" stroke-width="${w}"/><path d="${path}" fill="none" stroke="${col}" stroke-width="${Math.max(1, w - 5)}" stroke-dasharray="10 7" class="ic-flow ${running}"/>`;
            if (key === 'Pipe') body = tube(p.vertical ? 'M50 6 V84' : 'M5 45 H95');
            else if (key === 'Tee') body = tube('M5 42 H95') + tube('M50 42 V88');
            else if (key === 'Elbow') body = tube(p.vertical ? 'M50 5 V50 H95' : 'M5 45 H50 V88');
            else if (key === 'Flange') {
                const plateFill = fill === 'none' ? '#dbe5f0' : fill;
                body = `<line ${p.vertical ? 'x1="50" y1="6" x2="50" y2="84"' : 'x1="5" y1="45" x2="95" y2="45"'} stroke="${stroke}" stroke-width="${w}"/>` +
                    (p.vertical ?
                        `<rect x="38" y="24" width="24" height="7" rx="2" fill="${plateFill}" stroke="${stroke}" stroke-width="1.5"/><rect x="38" y="59" width="24" height="7" rx="2" fill="${plateFill}" stroke="${stroke}" stroke-width="1.5"/>` :
                        `<rect x="24" y="33" width="7" height="24" rx="2" fill="${plateFill}" stroke="${stroke}" stroke-width="1.5"/><rect x="69" y="33" width="7" height="24" rx="2" fill="${plateFill}" stroke="${stroke}" stroke-width="1.5"/>`);
            } else {
                body = `<line ${p.vertical ? 'x1="50" y1="8" x2="50" y2="82"' : 'x1="8" y1="45" x2="92" y2="45"'} stroke="${stroke}" stroke-width="${w}"/>`;
                if (key === 'DoubleArrow')
                    body += `<path d="${p.vertical ? 'M38 24 L50 8 L62 24 M38 66 L50 82 L62 66' : 'M22 33 L8 45 L22 57 M78 33 L92 45 L78 57'}" fill="none" stroke="${stroke}" stroke-width="${w}"/>`;
                else if (key === 'Arrow')
                    body += p.vertical ? `<path d="M35 65 L50 84 L65 65" fill="none" stroke="${stroke}" stroke-width="${w}"/>` : `<path d="M76 30 L95 45 L76 60" fill="none" stroke="${stroke}" stroke-width="${w}"/>`;
            }
        }
        return svg(body).replace('<svg ', '<svg preserveAspectRatio="none" ');
    }

    function equipmentMarkup(key, p, c) {
        const col = esc(color(c)), running = p.animate && num(p.value) > p.threshold && valid(c) ? 'running' : '';
        let body;
        if (key === 'Pump') body = `<path d="M4 52 H22 M70 29 H96 V44 H75 M35 77 V83 H80" fill="none" stroke="#60758d" stroke-width="10"/><circle cx="49" cy="47" r="29" fill="#eef4fa" stroke="${col}" stroke-width="6"/><path class="ic-rotor ${running}" d="M49 26 L66 56 H32 Z" fill="${col}"/>`;
        else if (key === 'Valve') body = `<path d="M3 45 H97 M50 45 V12 M33 12 H67" fill="none" stroke="#60758d" stroke-width="5"/><path d="M15 24 L85 66 V24 L15 66 Z" stroke="${col}" fill="${col}" fill-opacity=".22" stroke-width="4"/>`;
        else if (key === 'Fan') body = `<circle cx="50" cy="45" r="37" fill="#eef4fa" stroke="${col}" stroke-width="4"/><g class="ic-rotor ${running}" fill="${col}"><path d="M50 45 C20 5 80 0 63 35 Z M50 45 C100 35 97 82 62 58 Z M50 45 C35 96 1 63 33 46 Z"/><circle cx="50" cy="45" r="7" fill="#fff"/></g>`;
        else if (key === 'Motor') body = `<path d="M8 35 H23 M79 40 H98 M20 77 H83" stroke="#60758d" stroke-width="8"/><rect x="22" y="22" width="59" height="48" rx="7" fill="${col}" fill-opacity=".2" stroke="${col}" stroke-width="4"/><path d="M34 28 V64 M45 28 V64 M56 28 V64 M67 28 V64" stroke="${col}" stroke-width="3"/><rect x="37" y="11" width="29" height="11" fill="${col}"/>`;
        else if (key === 'HeatExchanger') body = `<path d="M2 26 H20 M80 62 H98" stroke="#60758d" stroke-width="7"/><rect x="20" y="14" width="60" height="62" rx="7" fill="#eef4fa" stroke="${col}" stroke-width="4"/><path class="ic-flow ${running}" d="M28 24 V44 H44 V66 H56 V44 H72" fill="none" stroke="${col}" stroke-width="3" stroke-dasharray="6 4"/>`;
        else if (key === 'Compressor') body = `<path d="M74 34 H94 V18" fill="none" stroke="#60758d" stroke-width="7"/><rect x="16" y="14" width="58" height="58" rx="10" fill="#eef4fa" stroke="${col}" stroke-width="4"/><path d="M45 43 m-13 0 a13 13 0 1 1 13 13 a10 10 0 1 1 -10 -10 a7 7 0 1 1 7 7 a4 4 0 1 1 -4 -4" fill="none" stroke="${col}" stroke-width="3"/><path d="M22 78 H68" stroke="#60758d" stroke-width="6"/>`;
        else if (key === 'Boiler') body = `<path d="M34 16 q-4 -5 0 -10 M50 16 q-4 -5 0 -10 M66 16 q-4 -5 0 -10" fill="none" stroke="#9bafc7" stroke-width="2.5"/><rect x="26" y="18" width="48" height="54" rx="12" fill="#eef4fa" stroke="${col}" stroke-width="4"/><path d="M50 66 C41 58 47 53 50 44 C57 53 59 59 50 66 Z" fill="#e8843c"/><path d="M32 74 V80 M68 74 V80" stroke="#60758d" stroke-width="4"/>`;
        else if (key === 'Conveyor') body = `<rect x="6" y="42" width="88" height="10" rx="5" fill="#dbe5f0" stroke="#8aa0b9" stroke-width="1.5"/><path class="ic-flow ${running}" d="M10 47 H90" stroke="${col}" stroke-width="3" stroke-dasharray="8 6"/><rect x="24" y="24" width="18" height="17" rx="2" fill="${col}" opacity=".35"/><rect x="58" y="28" width="18" height="13" rx="2" fill="${col}" opacity=".35"/><path d="M24 52 V72 M50 52 V72 M76 52 V72" stroke="#60758d" stroke-width="4"/><circle cx="24" cy="74" r="5" fill="#eef4fa" stroke="${col}" stroke-width="2"/><circle cx="50" cy="74" r="5" fill="#eef4fa" stroke="${col}" stroke-width="2"/><circle cx="76" cy="74" r="5" fill="#eef4fa" stroke="${col}" stroke-width="2"/>`;
        else if (key === 'Filter') body = `<path d="M2 16 H18" stroke="#60758d" stroke-width="7"/><path d="M22 14 H78 L64 46 V70 H36 V46 Z" fill="#eef4fa" stroke="${col}" stroke-width="4"/><g fill="#9bafc7"><circle cx="42" cy="26" r="2"/><circle cx="52" cy="26" r="2"/><circle cx="62" cy="26" r="2"/><circle cx="47" cy="36" r="2"/><circle cx="57" cy="36" r="2"/></g><path d="M50 70 V82" stroke="#60758d" stroke-width="7"/>`;
        else body = `<path d="M30 18 H70 L64 44 Q60 62 66 78 H34 Q40 62 36 44 Z" fill="#eef4fa" stroke="${col}" stroke-width="4"/><circle cx="50" cy="14" r="10" fill="#eef4fa" stroke="${col}" stroke-width="3"/><g class="ic-rotor-top ${running}" fill="${col}"><path d="M50 14 C41 5 57 2 53 11 Z M50 14 C59 5 65 16 54 16 Z M50 14 C50 25 43 23 48 16 Z"/></g>`;
        const text = !valid(c) ? tr('无数据', 'No data') : state(c).alarm ? tr('报警', 'Alarm') : num(p.value) > p.threshold ? tr('运行', 'Running') : tr('停止', 'Stopped');
        return `<div class="ic-equipment">${svg(body)}</div><div class="ic-caption"><span>${esc(p.label)}</span><span style="color:${col}"><i class="ic-dot"></i>${esc(text)}</span></div>`;
    }

    function tableMarkup(spec, p, data, preview) {
        const names = String(p.labels || '').split(/[,，]/);
        const previewValues = {201: 55, 202: 18, 203: 2.8, 204: 1, 301: 23, 302: 48, 303: 3.2, 304: 1, 401: 44, 402: 2.2, 403: 5.4, 404: 4.59};
        const previewChannels = channelList(p);
        const rows = (preview ? (previewChannels.length ? previewChannels : [201, 301]).map(cnlNum => ({cnlNum, val: previewValues[cnlNum] ?? 0, stat: 13})) : data || []).slice(0, clamp(num(p.maxRows, 8), 1, 50));
        if (spec.key === 'StatusGroup') return `<div class="ic-lamps">${rows.map((r, i) => `<span class="ic-lamp" style="color:${esc(r.stat <= 0 ? '#94a3b8' : r.val > p.threshold ? p.activeColor : p.idleColor)}"><i class="ic-dot"></i>${esc(names[i] || tr('通道 ', 'Channel ') + r.cnlNum)} · ${esc(r.stat <= 0 ? tr('无数据', 'No data') : r.val > p.threshold ? tr('开启', 'On') : tr('关闭', 'Off'))}</span>`).join('')}</div>`;
        return `<div class="ic-scroll"><table><thead><tr><th>${tr('通道', 'Channel')}</th><th>${tr('名称', 'Name')}</th><th>${tr('数值', 'Value')}</th><th>${tr('状态', 'Status')}</th></tr></thead><tbody>${rows.map((r, i) => `<tr><td>${esc(r.cnlNum)}</td><td>${esc(names[i] || '—')}</td><td>${r.stat > 0 ? esc(fmt(r.val, p)) : '—'}</td><td>${r.stat > 0 ? tr('有效', 'Valid') : tr('无数据', 'No data')}</td></tr>`).join('')}</tbody></table></div>`;
    }

    function trendMarkup(p, data, preview) {
        const samples = preview ? [20, 28, 25, 42, 38, 55, 49, 62, 55].map((v, i) => ({t: i * 60000, v})) :
            (data?.timestamps || []).map((t, i) => ({t: typeof t === 'object' ? num(t.ms, new Date(t.ut).getTime()) : new Date(t).getTime(), v: data.trends?.[0]?.[i]?.d?.val, stat: data.trends?.[0]?.[i]?.d?.stat}));
        const available = samples.filter(s => (preview || s.stat > 0) && Number.isFinite(s.v) && Number.isFinite(s.t));
        if (!available.length) return empty(tr('暂无历史数据', 'No historical data'));
        const min = num(p.minimum), max = num(p.maximum, 100);
        if (!(max > min)) return empty(tr('最大值必须大于最小值', 'Maximum must exceed minimum'), true);
        const start = samples[0].t, span = Math.max(1, samples.at(-1).t - start);
        // Preserve gaps for invalid samples and reduce long archives to at most 1000 plotted points.
        const stride = Math.max(1, Math.ceil(samples.length / 1000));
        let path = '', points = '', penDown = false;
        samples.forEach((s, i) => {
            if (!(preview || s.stat > 0) || !Number.isFinite(s.v)) { penDown = false; return; }
            if (i % stride && i !== samples.length - 1) return;
            const x = (10 + (s.t - start) / span * 280).toFixed(2), y = (94 - clamp((s.v - min) / (max - min), 0, 1) * 84).toFixed(2);
            path += `${penDown ? 'L' : 'M'}${x},${y} `;
            const label = new Date(s.t).toLocaleString() + ' · ' + fmt(s.v, p) + ' ' + p.unit;
            points += `<circle class="ic-sample" cx="${x}" cy="${y}" r="3" fill="${esc(p.activeColor)}" tabindex="0" aria-label="${esc(label)}"><title>${esc(label)}</title></circle>`;
            penDown = true;
        });
        return `<div class="ic-chart"><svg viewBox="0 0 300 104" preserveAspectRatio="none" role="img" aria-label="${esc(p.label + ' (' + p.unit + ')')}"><path d="M10 10 H290 M10 52 H290 M10 94 H290" stroke="#e1e9f2" stroke-dasharray="3 3"/><path d="${path}" fill="none" stroke="${esc(p.activeColor)}" stroke-width="2" vector-effect="non-scaling-stroke"/>${points}</svg></div><div class="ic-footer"><span>${esc(new Date(start).toLocaleTimeString())}</span><span>${esc(new Date(samples.at(-1).t).toLocaleTimeString())}</span></div><div class="ic-footer"><span>${esc(min)}–${esc(max)} ${esc(p.unit)}</span><span>${preview ? tr('编辑预览', 'Editor preview') : esc(p.historyMinutes) + tr(' 分钟 · ', ' min · ') + available.length + tr(' 点', ' points')}</span></div>`;
    }

    function alarmMarkup(p, data, preview, context) {
        const filter = channelList(p);
        const rows = (preview ? [{e: {cnlNum: 201, severity: 900, ack: false}, ef: {time: '12:00:00', cnl: tr('储罐液位', 'Tank level'), descr: tr('高限报警（编辑预览）', 'High alarm (preview)')}}] : data?.records || [])
            .filter(r => !filter.length || filter.includes(r.e.cnlNum)).slice(-clamp(num(p.maxRows, 8), 1, 50)).reverse();
        if (!rows.length) return empty(tr('最近两天无匹配事件', 'No matching events in the last two days'));
        const pending = rows.filter(r => r.e.ackRequired && !r.e.ack).length;
        const root = context?.mainApi?.rootPath || '/';
        return `<div class="ic-muted">${tr('下列事件待确认：', 'Unacknowledged below: ')}${pending} · ${tr('确认不代表恢复正常', 'Acknowledgement does not clear the condition')}</div><div class="ic-scroll"><table><thead><tr><th>${tr('时间', 'Time')}</th><th>${tr('通道', 'Channel')}</th><th>${tr('事件', 'Event')}</th><th>${tr('确认', 'Ack')}</th></tr></thead><tbody>${rows.map(r => {
            const id = String(r.id ?? '');
            const canAck = !preview && context?.controlRight && r.e.ackRequired && !r.e.ack && /^\d+$/.test(id);
            const ack = r.e.ack ? tr('已确认', 'Acknowledged') : r.e.ackRequired ? tr('待确认', 'Required') : '—';
            const link = canAck ? `<a class="ic-ack-link" href="${esc(root)}Main/EventAck?eventID=${id}&amp;archiveBit=${num(p.archiveBit)}" target="_blank" rel="noopener">${tr('查看并确认', 'Review and acknowledge')} ↗</a>` : esc(ack);
            return `<tr><td>${esc(r.ef?.time || r.e.timestamp)}</td><td>${esc(r.ef?.cnl || r.e.cnlNum)}</td><td>${esc(r.ef?.descr || r.e.text)}</td><td>${link}</td></tr>`;
        }).join('')}</tbody></table></div>`;
    }

    // Role filter: engineering-only components can be hidden from operators at runtime.
    function applyRoleVisibility(elem, c, context) {
        if (context.editMode) {
            elem.removeClass('ic-role-hidden');
            return;
        }

        const role = String(c.properties.roleFilter || 'all').trim().toLowerCase();
        const isAdmin = typeof window.mimicIsAdmin === 'boolean' ? window.mimicIsAdmin : false;
        elem.toggleClass('ic-role-hidden', role === 'admin' && !isAdmin);
    }

    function layoutMarkup(key, p) {
        const stroke = esc(p.activeColor), fill = esc(p.fillColor || 'none'), w = clamp(num(p.lineWidth, 1), 1, 8);
        if (key === 'Divider')
            return `<div class="ic-divider" style="--ic-divider:${stroke}"><span>${esc(p.label)}</span></div>`;
        return `<div class="ic-group" style="border-color:${stroke};border-width:${w}px;background:${fill === 'none' ? 'transparent' : fill}"><span class="ic-group-title">${esc(p.label)}</span></div>`;
    }

    function indicatorMarkup(key, p, c) {
        const r = valid(c) ? ratio(p) : 0, col = esc(p.activeColor);
        const text = valid(c) ? fmt(p.value, p) + ' ' + p.unit : tr('无数据', 'No data');
        if (key === 'StatusBadge') {
            const badgeColor = esc(color(c));
            return `<div class="ic-badge" style="background:${badgeColor}"><i class="ic-dot"></i>${esc(valid(c) ? p.label : tr('无数据', 'No data'))}<strong>${esc(valid(c) ? fmt(p.value, p) + ' ' + p.unit : '')}</strong></div>`;
        }
        if (key === 'KpiCard') {
            const t = p.maximum > p.minimum ? clamp((num(p.target) - p.minimum) / (p.maximum - p.minimum), 0, 1) : 0;
            return `<div class="ic-kpi"><div class="ic-kpi-value">${esc(valid(c) ? fmt(p.value, p) : '—')}<small>${esc(p.unit)}</small></div><div class="ic-muted">${esc(tr('参考 ', 'Reference '))}${esc(fmt(p.target, p))} ${esc(p.unit)}</div><div class="ic-track"><div class="ic-fill" style="background:${col};width:${(r * 100).toFixed(1)}%"></div><i class="ic-kpi-mark" style="left:${(t * 100).toFixed(1)}%"></i></div></div>`;
        }
        if (!(p.maximum > p.minimum)) return empty(tr('最大值必须大于最小值', 'Maximum must exceed minimum'), true);
        if (key === 'Thermometer')
            return `<div class="ic-chart">${svg(`<path d="M68 16 H75 M68 30 H75 M68 44 H75" stroke="#9bafc7" stroke-width="2"/><rect x="42" y="6" width="16" height="58" rx="8" fill="#eef4fa" stroke="#8aa0b9" stroke-width="2"/><rect x="45" y="${(62 - r * 52).toFixed(2)}" width="10" height="${Math.max(1, r * 52).toFixed(2)}" rx="5" fill="${col}"/><circle cx="50" cy="70" r="12" fill="#eef4fa" stroke="#8aa0b9" stroke-width="2"/><circle cx="50" cy="70" r="8" fill="${col}"/>`)}</div><div class="ic-value">${esc(text)}</div>`;
        if (key === 'PressureGauge') {
            const angle = Math.PI * (1 - r), x = (50 + 34 * Math.cos(angle)).toFixed(2), y = (52 - 34 * Math.sin(angle)).toFixed(2);
            return `<div class="ic-chart">${svg(`<path d="M14 52 A36 36 0 0 1 86 52" fill="none" stroke="#e1e9f2" stroke-width="8"/><path d="M14 52 A36 36 0 0 1 86 52" pathLength="100" fill="none" stroke="${col}" stroke-width="8" stroke-dasharray="${(r * 100).toFixed(1)} 100"/><path d="M18 44 L24 47 M82 44 L76 47 M50 12 V17 M28 24 L32 28 M72 24 L68 28" stroke="#8aa0b9" stroke-width="2"/><path d="M50 52 L${x} ${y}" stroke="#213047" stroke-width="2.5"/><circle cx="50" cy="52" r="4" fill="#213047"/><text x="50" y="78" text-anchor="middle" font-size="12" fill="#213047">${esc(text)}</text>`)}</div>`;
        }
        if (key === 'FlowMeter') {
            const spin = p.animate && num(p.value) > p.threshold && valid(c) ? 'running' : '';
            return `<div class="ic-chart">${svg(`<circle cx="50" cy="45" r="30" fill="#eef4fa" stroke="${col}" stroke-width="4"/><path d="M50 15 V21 M80 45 H74 M50 75 V69 M20 45 H26" stroke="#8aa0b9" stroke-width="2"/><g class="ic-rotor ${spin}" fill="#8aa0b9"><path d="M50 45 C42 33 58 31 52 43 Z M50 45 C62 37 64 53 52 47 Z M50 45 C58 57 42 59 48 47 Z"/></g><text x="50" y="87" text-anchor="middle" font-size="12" fill="#213047">${esc(text)}</text>`)}</div>`;
        }
        return `<div class="ic-energy"><div class="ic-energy-value">${esc(valid(c) ? fmt(p.value, p) : '—')}</div><div class="ic-energy-unit">${esc(p.unit)}</div><div class="ic-track"><div class="ic-fill" style="background:${col};width:${(r * 100).toFixed(1)}%"></div></div></div>`;
    }

    function barMarkup(p, data, preview) {
        const names = String(p.labels || '').split(/[,，]/);
        const rows = (preview ? [{cnlNum: 201, val: 55, stat: 13}, {cnlNum: 204, val: 32, stat: 13}, {cnlNum: 301, val: 78, stat: 13}, {cnlNum: 304, val: 46, stat: 13}] : data || []).slice(0, 8);
        if (!rows.length) return empty(tr('请设置通道列表', 'Set the channel list'));
        const max = Math.max(1, ...rows.map(r => Math.abs(num(r.val))));
        return `<div class="ic-bars">${rows.map((r, i) => `<div class="ic-bar"><span class="ic-bar-val">${r.stat > 0 ? esc(fmt(r.val, p)) : '—'}</span><div class="ic-bar-track"><div class="ic-bar-fill" style="height:${(clamp(Math.abs(num(r.val)) / max, 0, 1) * 100).toFixed(1)}%;background:${esc(p.activeColor)}"></div></div><span class="ic-bar-name">${esc(names[i] || r.cnlNum)}</span></div>`).join('')}</div>`;
    }

    function pieMarkup(p, data, preview) {
        const names = String(p.labels || '').split(/[,，]/);
        const rows = (preview ? [{cnlNum: 201, val: 45, stat: 13}, {cnlNum: 204, val: 30, stat: 13}, {cnlNum: 301, val: 25, stat: 13}] : data || []).filter(r => r.stat > 0 && num(r.val) > 0).slice(0, 8);
        const total = rows.reduce((sum, r) => sum + num(r.val), 0);
        if (!total) return empty(tr('暂无有效数据', 'No valid data'));
        const palette = ['#1689d8', '#159d83', '#e8843c', '#8b5cf6', '#d94b6a', '#0ea5b7', '#c08a1e', '#5d7290'];
        let acc = 0;
        const slices = rows.map((r, i) => {
            const frac = num(r.val) / total, start = acc * 2 * Math.PI;
            acc += frac;
            const end = acc * 2 * Math.PI;
            const x1 = (50 + 34 * Math.sin(start)).toFixed(2), y1 = (45 - 34 * Math.cos(start)).toFixed(2);
            const x2 = (50 + 34 * Math.sin(end)).toFixed(2), y2 = (45 - 34 * Math.cos(end)).toFixed(2);
            return `<path d="M50 45 L${x1} ${y1} A34 34 0 ${frac > .5 ? 1 : 0} 1 ${x2} ${y2} Z" fill="${palette[i % palette.length]}"/>`;
        }).join('');
        const legend = rows.map((r, i) => `<span class="ic-legend"><i style="background:${palette[i % palette.length]}"></i>${esc(names[i] || r.cnlNum)} ${(num(r.val) / total * 100).toFixed(0)}%</span>`).join('');
        return `<div class="ic-chart">${svg(slices)}</div><div class="ic-legend-box">${legend}</div>`;
    }

    function bannerMarkup(p, data) {
        const names = String(p.labels || '').split(/[,，]/), rows = data || [];
        const index = rows.findIndex(r => [11, 12, 14, 15].includes(r.stat));
        if (index < 0) return `<div class="ic-banner ic-banner-ok"><i class="ic-dot"></i>${esc(tr('系统正常，无报警', 'No active alarms'))}</div>`;
        const worst = rows[index];
        return `<div class="ic-banner ic-banner-alarm"><i class="ic-dot"></i><span>${esc(names[index] || tr('通道 ', 'Channel ') + worst.cnlNum)}</span><strong>${esc(fmt(worst.val, p))} ${esc(p.unit)}</strong><span class="ic-muted">${esc(tr('状态码 ', 'Status code ') + worst.stat)}</span></div>`;
    }

    // Custom tooltip: multi-line text, no browser delay, keyboard and long-press friendly.
    const tooltips = new Map();
    let tooltipScrollBound = false;
    function tooltipHost(component) {
        let tip = tooltips.get(component.id);
        if (!tip) {
            tip = document.createElement('div');
            tip.className = 'ic-tooltip';
            tip.id = 'ic-tip-' + component.id;
            tip.setAttribute('role', 'tooltip');
            document.body.append(tip);
            tooltips.set(component.id, tip);
        }
        return tip;
    }
    function placeTooltip(tip, host) {
        if (!host?.getBoundingClientRect) return;
        const box = host.getBoundingClientRect();
        tip.classList.add('show');
        const size = tip.getBoundingClientRect();
        const left = clamp(box.left + box.width / 2 - size.width / 2, 8, Math.max(8, window.innerWidth - size.width - 8));
        let top = box.top - size.height - 10;
        if (top < 8) top = Math.min(box.bottom + 10, Math.max(8, window.innerHeight - size.height - 8));
        tip.style.left = left + 'px';
        tip.style.top = top + 'px';
        tip.classList.toggle('below', top > box.top);
    }
    function bindTooltip(elem, component, context) {
        const text = String(component.properties.tooltip || '').trim();
        const signature = (context.editMode ? 'edit|' : 'run|') + text;
        if (elem.attr('data-ic-tip') === signature) return;
        elem.off('.ictip').attr('data-ic-tip', signature);
        // Empty text clears everything; the editor keeps the native title for quick reference.
        if (!text) { elem.removeAttr('title').removeAttr('aria-describedby'); return; }
        if (context.editMode || !elem[0]?.getBoundingClientRect) return;
        // The native title ignores line breaks, waits ~1s and never appears on touch screens.
        elem.removeAttr('title');
        const tip = tooltipHost(component), host = elem[0];
        tip.textContent = text;
        const hide = () => { clearTimeout(tip._timer); clearTimeout(tip._press); tip.classList.remove('show'); };
        const show = () => { clearTimeout(tip._timer); tip._timer = setTimeout(() => placeTooltip(tip, host), 120); };
        if (!tooltipScrollBound) {
            tooltipScrollBound = true;
            window.addEventListener('scroll', () => tooltips.forEach(item => item.classList.remove('show')), true);
        }
        elem.attr('aria-describedby', tip.id)
            .on('mouseenter.ictip', show)
            .on('mouseleave.ictip', hide)
            .on('focusin.ictip', show)
            .on('focusout.ictip', hide)
            .on('keydown.ictip', event => { if (event.key === 'Escape') hide(); })
            .on('touchstart.ictip', () => { clearTimeout(tip._press); tip._press = setTimeout(() => { tip.textContent = text; placeTooltip(tip, host); }, 400); })
            .on('touchend.ictip touchcancel.ictip touchmove.ictip', hide);
    }

    // Exposed for tests and for other plugins that want the same hover behaviour.
    M.IndustrialTooltip = {bind: bindTooltip, place: placeTooltip, hosts: tooltips};
    M.IndustrialRoleFilter = applyRoleVisibility;
    M.IndustrialFormat = fmt;

    function stackMarkup(p, data, preview) {
        const rows = (preview ? [{cnlNum: 201, val: 0, stat: 13}, {cnlNum: 202, val: 0, stat: 13}, {cnlNum: 203, val: 1, stat: 13}] : data || []).slice(0, 3);
        const alarm = rows.some(r => [11, 12, 14, 15].includes(r.stat));
        const lit = i => {
            const row = rows[i];
            if (i === 0) return alarm;
            return !!row && row.stat > 0 && num(row.val) > num(p.threshold) && !(i === 2 && alarm);
        };
        const lamps = ['red', 'amber', 'green']
            .map((color, i) => `<span class="ic-stack-lamp ic-stack-${color}${lit(i) ? ' on' : ''}"></span>`).join('');
        const state = alarm ? tr('报警', 'Alarm') : rows.some((r, i) => i > 0 && lit(i)) ? tr('运行', 'Running') : tr('停止', 'Stopped');
        return `<div class="ic-stack">${lamps}</div><div class="ic-footer"><span>${esc(p.label)}</span><span>${esc(state)}</span></div>`;
    }

    function valveMarkup(p, c) {
        const r = valid(c) ? ratio(p) : 0, col = esc(p.activeColor);
        const text = valid(c) ? fmt(p.value, p) + ' ' + p.unit : tr('无数据', 'No data');
        return `<div class="ic-chart">${svg(`<path d="M3 40 H97 M50 40 V16 M33 16 H67" fill="none" stroke="#60758d" stroke-width="5"/><path d="M15 20 L85 60 V20 L15 60 Z" stroke="${col}" fill="${col}" fill-opacity=".22" stroke-width="4"/><path d="M22 70 A28 28 0 0 1 78 70" fill="none" stroke="#e1e9f2" stroke-width="6"/><path d="M22 70 A28 28 0 0 1 78 70" pathLength="100" fill="none" stroke="${col}" stroke-width="6" stroke-dasharray="${(r * 100).toFixed(1)} 100"/>`)}</div><div class="ic-value">${esc(text)}</div>`;
    }

    function trendMultiMarkup(p, data, preview) {
        const names = String(p.labels || '').split(/[,，]/);
        const units = String(p.units || '').split(/[,，]/);
        const minima = String(p.minimums || '').split(/[,，]/), maxima = String(p.maximums || '').split(/[,，]/);
        const palette = ['#1689d8', '#159d83', '#e8843c', '#8b5cf6'];
        const min = num(p.minimum), max = num(p.maximum, 100);
        if (!(max > min)) return empty(tr('最大值必须大于最小值', 'Maximum must exceed minimum'), true);
        const timestamps = preview ? [0, 1, 2, 3, 4, 5].map(i => i * 60000) :
            (data?.timestamps || []).map(t => typeof t === 'object' ? num(t.ms, new Date(t.ut).getTime()) : new Date(t).getTime());
        const columns = preview ? [[20, 30, 26, 44, 40, 56], [46, 42, 50, 47, 55, 52]] : (data?.trends || []);
        if (!timestamps.length || !columns.length) return empty(tr('暂无历史数据', 'No historical data'));
        const start = timestamps[0], span = Math.max(1, timestamps.at(-1) - start);
        const ranges = columns.slice(0, 4).map((_, i) => ({min: minima[i]?.trim() ? num(minima[i], min) : min, max: maxima[i]?.trim() ? num(maxima[i], max) : max, unit: units[i]?.trim() || p.unit}));
        if (ranges.some(r => !(r.max > r.min))) return empty(tr('各曲线最大值必须大于最小值', 'Each series maximum must exceed its minimum'), true);
        const paths = columns.slice(0, 4).map((column, index) => {
            let d = '', points = '', pen = false;
            const range = ranges[index];
            timestamps.forEach((t, i) => {
                const v = preview ? column[i] : column?.[i]?.d?.val;
                const stat = preview ? 13 : column?.[i]?.d?.stat;
                if (!(stat > 0) || !Number.isFinite(v) || !Number.isFinite(t)) { pen = false; return; }
                const x = (10 + (t - start) / span * 280).toFixed(2), y = (94 - clamp((v - range.min) / (range.max - range.min), 0, 1) * 84).toFixed(2);
                d += `${pen ? 'L' : 'M'}${x},${y} `;
                const label = `${names[index] || channelList(p)[index] || index + 1} · ${new Date(t).toLocaleString()} · ${fmt(v, p)} ${range.unit}`;
                points += `<circle class="ic-sample" cx="${x}" cy="${y}" r="3" fill="${palette[index % palette.length]}" tabindex="0" aria-label="${esc(label)}"><title>${esc(label)}</title></circle>`;
                pen = true;
            });
            return `<path d="${d}" fill="none" stroke="${palette[index % palette.length]}" stroke-width="2" vector-effect="non-scaling-stroke"/>${points}`;
        }).join('');
        const legend = columns.slice(0, 4).map((_, i) => `<span class="ic-legend"><i style="background:${palette[i % palette.length]}"></i>${esc(names[i] || tr('通道 ', 'Channel ') + (channelList(p)[i] ?? i + 1))} (${esc(ranges[i].min)}–${esc(ranges[i].max)} ${esc(ranges[i].unit)})</span>`).join('');
        return `<div class="ic-chart"><svg viewBox="0 0 300 104" preserveAspectRatio="none"><path d="M10 10 H290 M10 52 H290 M10 94 H290" stroke="#e1e9f2" stroke-dasharray="3 3"/>${paths}</svg></div><div class="ic-footer"><span>${esc(new Date(start).toLocaleTimeString())}</span><span>${esc(p.historyMinutes)} ${tr('分钟', 'min')} · ${esc(new Date(timestamps.at(-1)).toLocaleTimeString())}</span></div><div class="ic-legend-box">${legend}</div>`;
    }

    class IndustrialRenderer extends M.RegularComponentRenderer {
        constructor(spec) { super(); this.spec = spec; }
        _completeDom(elem) { elem.append('<div class="ic-body"></div>'); }
        _setClasses(elem, c, context) { super._setClasses(elem, c, context); elem.addClass('industrial').attr('data-industrial-type', this.spec.key); }
        _executeAction(elem, c, context) {
            // A double click raises two clicks; never fire the same command twice in a row.
            const now = Date.now();
            if (c._lastActionAt && now - c._lastActionAt < 400) return;
            c._lastActionAt = now;
            super._executeAction(elem, c, context);
        }
        _setProps(elem, c, context) {
            super._setProps(elem, c, context);
            if (!context.editMode && (c.bindings?.inCnlNum || c.properties.inCnlNum) > 0 && state(c).valid === undefined) {
                c.customData ??= {};
                c.customData.industrial = {...state(c), valid: false};
            }
            this.paint(elem, c, context);
            if (remoteTypes.has(this.spec.key) || (this.spec.key === 'EnergyMeter' && c.properties.integratePower)) registerRemote(c, context, this);
            bindTooltip(elem, c, context);
        }
        setSize(c, width, height) {
            super.setSize(c, width, height);
            // SVGs and flex layouts scale without recreating their data or inputs.
        }
        paint(elem, c, context) {
            const key = this.spec.key, p = c.properties, preview = context.editMode, current = state(c);
            const target = elem.children('.ic-body');
            if (key === 'AlarmAckButton') {
                if (M.Operations) M.Operations.renderAcknowledgement(target, c, context);
                else target.html(empty(tr('报警确认组件正在载入', 'Loading event acknowledgement')));
                return;
            }
            if (inputTypes.has(key)) { this.paintInput(target, c, context); return; }
            if (choiceTypes.has(key)) { this.paintChoice(target, c, context); return; }
            if (commandTypes.has(key)) { this.paintCommands(target, c, context); return; }
            if (safetyTypes.has(key)) { this.paintSafety(target, c, context); return; }
            if (shapeTypes.has(key)) { target.html(shapeMarkup(key, p, c)); return; }
            if (layoutTypes.has(key)) { target.html(layoutMarkup(key, p)); return; }
            if (equipment.has(key)) { target.html(equipmentMarkup(key, p, c)); return; }
            let content = '';
            if (key === 'EnergyMeter' && p.integratePower) {
                const data = preview ? {kwh: 2.3, coveredMinutes: 60} : current.remote;
                content = current.error ? empty(current.error, true) : !data ? empty(tr('正在读取功率归档…', 'Reading power archive…')) : data.kwh === null ? empty(tr('没有可积分的连续有效采样', 'No continuous valid samples to integrate')) :
                    `<div class="ic-value">${esc(fmt(data.kwh, p))} kWh</div><div class="ic-muted">${tr('功率归档积分估算 · 有效覆盖 ', 'Archive integration estimate · coverage ')}${esc(Math.round(data.coveredMinutes))}${tr(' 分钟', ' min')}</div>`;
            } else if (remoteTypes.has(key)) {
                if (!preview && current.error) content = empty(current.error, true);
                else if (!preview && !current.remote) content = empty(tr('正在读取数据…', 'Loading data…'));
                else if (key === 'Trend') content = trendMarkup({...p, historyMinutes: clamp(p.historyMinutes * (current.historyZoom || 1), 1, 1440)}, current.remote, preview);
                else if (key === 'TrendMulti') content = trendMultiMarkup({...p, historyMinutes: clamp(p.historyMinutes * (current.historyZoom || 1), 1, 1440)}, current.remote, preview);
                else if (key === 'StackLight') content = stackMarkup(p, current.remote, preview);
                else if (key === 'AlarmList') content = alarmMarkup(p, current.remote, preview, context);
                else if (key === 'BarChart') content = barMarkup(p, current.remote, preview);
                else if (key === 'PieChart') content = pieMarkup(p, current.remote, preview);
                else if (key === 'AlarmBanner') content = bannerMarkup(p, current.remote);
                else content = tableMarkup(this.spec, p, current.remote, preview);
            } else if (key === 'ControlValve') {
                content = valveMarkup(p, c);
            } else if (['Thermometer', 'PressureGauge', 'FlowMeter', 'EnergyMeter', 'KpiCard', 'StatusBadge'].includes(key)) {
                content = indicatorMarkup(key, p, c);
            } else {
                const r = valid(c) ? ratio(p) : 0, text = valid(c) ? fmt(p.value, p) + ' ' + p.unit : tr('无数据', 'No data');
                if (!(p.maximum > p.minimum)) content = empty(tr('最大值必须大于最小值', 'Maximum must exceed minimum'), true);
                else if (key === 'Gauge') {
                    const angle = Math.PI * (1 - r), x = 50 + 36 * Math.cos(angle), y = 52 - 36 * Math.sin(angle);
                    content = `<div class="ic-chart">${svg(`<path d="M10 52 A40 40 0 0 1 90 52" fill="none" stroke="#e1e9f2" stroke-width="9"/><path d="M10 52 A40 40 0 0 1 90 52" pathLength="100" fill="none" stroke="${esc(p.activeColor)}" stroke-width="9" stroke-dasharray="${(r * 100).toFixed(1)} 100"/><path d="M50 52 L${x.toFixed(2)} ${y.toFixed(2)}" stroke="#213047" stroke-width="2.5"/><circle cx="50" cy="52" r="4" fill="#213047"/><text x="50" y="77" text-anchor="middle" font-size="12" fill="#213047">${esc(text)}</text>`)}</div>`;
                } else if (key === 'Tank') {
                    content = `<div class="ic-chart">${svg(`<path d="M20 15 V70 A30 10 0 0 0 80 70 V15" fill="#e4edf7" stroke="#8aa0b9" stroke-width="2"/><path d="M22 70 V${(70 - r * 52).toFixed(2)} H78 V70 A28 8 0 0 1 22 70" fill="${esc(p.activeColor)}" opacity=".75"/><ellipse cx="50" cy="15" rx="30" ry="9" fill="#eef4fa" stroke="#8aa0b9" stroke-width="2"/>`)}</div><div class="ic-value">${esc(text)}</div>`;
                } else content = `<div class="ic-track ${key === 'Level' ? 'ic-level' : ''}"><div class="ic-fill" style="background:${esc(p.activeColor)};${key === 'Level' ? 'height' : 'width'}:${(r * 100).toFixed(2)}%"></div></div><div class="ic-value">${esc(text)}</div>`;
            }
            const ranged = ['Gauge', 'Tank', 'Level', 'Progress', 'Thermometer', 'PressureGauge', 'FlowMeter', 'EnergyMeter', 'KpiCard', 'ControlValve'].includes(key);
            const overRange = ranged && valid(c) && p.maximum > p.minimum && (p.value < p.minimum || p.value > p.maximum);
            target.html(`<div class="ic-card">${title(p, preview ? tr('编辑预览', 'Editor preview') : '')}${content}${overRange ? `<div class="ic-error" role="status">${tr('超出显示量程：', 'Outside display range: ')}${esc(p.minimum)}–${esc(p.maximum)} ${esc(p.unit)}</div>` : ''}</div>`);
            if (['Trend', 'TrendMulti'].includes(key) && !preview && target.find) {
                target.find('.ic-title').append(`<span class="ic-trend-zoom"><button type="button" data-zoom="0.5" aria-label="${tr('缩短时间范围', 'Shorter time range')}">＋</button><button type="button" data-zoom="2" aria-label="${tr('扩大时间范围', 'Longer time range')}">−</button></span>`);
                target.find('[data-zoom]').on('click', event => {
                    event.stopPropagation();
                    c.customData.industrial.historyZoom = clamp(p.historyMinutes * (state(c).historyZoom || 1) * Number(event.currentTarget.dataset.zoom), 1, 1440) / p.historyMinutes;
                    const entry = remote.get(c); if (entry) {entry.next = Date.now(); void pollOne(entry);}
                });
            }
            if (key === 'AlarmList' && !preview && context.viewHub?.modalManager && typeof ModalOptions !== 'undefined' && target.on) {
                target.off('click.industrialAck').on('click.industrialAck', '.ic-ack-link', event => {
                    event.preventDefault(); event.stopPropagation();
                    context.viewHub.modalManager.showModal(event.currentTarget.getAttribute('href'),
                        new ModalOptions({buttons: [ModalButton.OK, ModalButton.CLOSE], size: ModalSize.LARGE}),
                        () => { const entry = remote.get(c); if (entry) void pollOne(entry); });
                });
            }
        }
        _feedback(target, message, ok) {
            target.find('.ic-feedback').removeClass('ic-error ic-good')
                .addClass(ok === true ? 'ic-good' : ok === false ? 'ic-error' : '').text(message || '');
        }
        async _send(target, c, context, value) {
            if (!this._canControl(c, context)) return false;
            target.attr('data-sending', 'true');
            target.find('button, input, select').prop('disabled', true);
            try {
                const confirmed = await sendAndConfirm(c, context, value, (message, ok) => this._feedback(target, message, ok));
                if (confirmed) target.find('.ic-choice input').removeAttr('data-dirty');
                return confirmed;
            } finally {
                target.removeAttr('data-sending');
                this.paint(c.dom || {children: () => target}, c, context);
            }
        }
        paintChoice(target, c, context) {
            const p = c.properties, key = this.spec.key;
            const canControl = this._canControl(c, context);
            if (!target.find('.ic-choice').length) {
                const field = key === 'Checkbox'
                    ? `<label class="ic-check"><input type="checkbox" class="ic-editor"><span>${esc(tr('启用', 'Enabled'))}</span></label>`
                    : `<div class="ic-radios">${optionList(p).map(o => `<label class="ic-radio"><input type="radio" name="rg${c.id}" value="${o.value}"><span>${esc(o.label)}</span></label>`).join('')}</div>`;
                target.html(`<div class="ic-card">${title(p)}<div class="ic-choice">${field}<button type="button" class="ic-send">${tr('发送', 'Send')}</button></div><div class="ic-feedback" role="status"></div></div>`);
                const box = target.find('.ic-choice');
                box.on('click.industrial', event => event.stopPropagation());
                box.find('input').on('change.industrial', () => box.find('input').attr('data-dirty', 'true'));
                box.find('.ic-send').on('click.industrial', event => {
                    event.stopPropagation();
                    if (!this._canControl(c, context)) return;
                    const value = key === 'Checkbox' ? (box.find('input').prop('checked') ? 1 : 0) : num(box.find('input:checked').val());
                    this._send(target, c, context, value);
                });
            }
            const box = target.find('.ic-choice');
            target.find('.ic-title span:first').text(p.label);
            if (key === 'Checkbox')
                box.find('input').not(':focus, [data-dirty=true]').prop('checked', num(p.value) > 0);
            else
                box.find('input[type=radio]').not(':focus, [data-dirty=true]').each(function () { this.checked = num(this.value) === num(p.value); });
            box.find('input').prop('disabled', !canControl).attr('aria-label', p.label);
            box.find('.ic-send').prop('disabled', !canControl);
            if (context.editMode) this._feedback(target, tr('编辑预览 · 运行时发送命令', 'Preview · commands are available at runtime'));
            else if (!context.controlRight) this._feedback(target, tr('只读权限', 'Read-only'));
            else if (context.mainApi?.dataPaused) this._feedback(target, tr('等待实时数据，控制已暂停', 'Awaiting live data; controls paused'));
            else if (target.find('.ic-feedback').text() === tr('等待实时数据，控制已暂停', 'Awaiting live data; controls paused')) this._feedback(target, '');
        }
        paintCommands(target, c, context) {
            const p = c.properties, options = optionList(p);
            const canControl = this._canControl(c, context);
            const signature = options.map(o => o.label + '=' + o.value).join(';');
            if (!target.find('.ic-commands').length || target.find('.ic-commands').attr('data-options') !== signature) {
                target.html(`<div class="ic-card">${title(p)}<div class="ic-commands" data-options="${esc(signature)}"><div class="ic-cmd-row">${options.map(o => `<button type="button" class="ic-cmd" data-value="${o.value}">${esc(o.label)}</button>`).join('')}</div><div class="ic-feedback" role="status"></div></div></div>`);
                const box = target.find('.ic-commands');
                box.on('click.industrial', event => event.stopPropagation());
                box.find('.ic-cmd').on('click.industrial', event => {
                    event.stopPropagation();
                    if (!this._canControl(c, context)) return;
                    this._send(target, c, context, num(event.currentTarget.dataset.value));
                });
            }
            target.find('.ic-title span:first').text(p.label);
            target.find('.ic-cmd').prop('disabled', !canControl);
            if (context.editMode) this._feedback(target, tr('编辑预览 · 运行时发送命令', 'Preview · commands are available at runtime'));
            else if (!context.controlRight) this._feedback(target, tr('只读权限', 'Read-only'));
            else if (context.mainApi?.dataPaused) this._feedback(target, tr('等待实时数据，控制已暂停', 'Awaiting live data; controls paused'));
            else if (target.find('.ic-feedback').text() === tr('等待实时数据，控制已暂停', 'Awaiting live data; controls paused')) this._feedback(target, '');
        }
        _canControl(c, context) {
            const p = c.properties;
            return !context.editMode && context.controlRight && !context.mainApi?.dataPaused && p.enabled && (c.bindings?.outCnlNum || p.outCnlNum) > 0 && !commandBusy(c, context);
        }
        paintSafety(target, c, context) {
            const p = c.properties, key = this.spec.key, options = key === 'SelectorSwitch' ? optionList(p) : [];
            if (!target.find('.ic-safety').length) {
                const body =
                    key === 'EmergencyStop' ? '<button type="button" class="ic-estop"><span class="ic-estop-cap"></span></button>' :
                    key === 'KeySwitch' ? '<div class="ic-keyswitch" data-open="false"><span class="ic-key-cover"></span><span class="ic-key-knob"></span></div>' :
                    key === 'SelectorSwitch' ? '<div class="ic-selector"><span class="ic-selector-knob"></span><div class="ic-selector-labels"></div></div>' :
                    `<button type="button" class="ic-ack">${esc(tr('确认报警', 'Acknowledge'))}</button>`;
                target.html(`<div class="ic-card">${title(p)}<div class="ic-safety">${body}</div><div class="ic-feedback" role="status"></div></div>`);
                const box = target.find('.ic-safety');
                box.on('click.industrial', event => event.stopPropagation());
                if (key === 'EmergencyStop') {
                    const button = box.find('.ic-estop');
                    let timer = null;
                    const cancel = () => { clearTimeout(timer); timer = null; button.removeClass('pressing'); };
                    button.on('pointerdown.industrial', event => {
                        event.preventDefault();
                        event.stopPropagation();
                        if (!this._canControl(c, context) || timer) return;
                        button.addClass('pressing');
                        timer = setTimeout(() => { timer = null; button.removeClass('pressing'); this._send(target, c, context, 1); }, 800);
                    }).on('pointerup.industrial pointerleave.industrial pointercancel.industrial', cancel);
                } else if (key === 'KeySwitch') {
                    const guard = box.find('.ic-keyswitch');
                    guard.on('click.industrial', event => {
                        event.stopPropagation();
                        if (!this._canControl(c, context)) return;
                        if (guard.attr('data-open') !== 'true') {
                            guard.attr('data-open', 'true').addClass('open');
                            clearTimeout(guard[0]._icClose);
                            guard[0]._icClose = setTimeout(() => { guard.attr('data-open', 'false').removeClass('open'); }, 4000);
                            this._feedback(target, tr('保护盖已打开，再次点击操作', 'Cover open, click again'));
                            return;
                        }
                        this._send(target, c, context, num(p.value) > 0 ? 0 : 1);
                    });
                } else if (key === 'SelectorSwitch') {
                    box.find('.ic-selector').on('click.industrial', event => {
                        event.stopPropagation();
                        if (!this._canControl(c, context) || !options.length) return;
                        const current = options.findIndex(o => o.value === num(p.value));
                        const next = options[(current + 1 + options.length) % options.length];
                        this._send(target, c, context, next.value);
                    });
                } else {
                    box.find('.ic-ack').on('click.industrial', event => {
                        event.stopPropagation();
                        if (!this._canControl(c, context)) return;
                        this._send(target, c, context, 1);
                    });
                }
            }
            target.find('.ic-title span:first').text(p.label);
            if (key === 'SelectorSwitch') {
                target.find('.ic-selector-labels').html(options.map(o => `<span>${esc(o.label)}</span>`).join(''));
                const index = Math.max(0, options.findIndex(o => o.value === num(p.value)));
                const angle = options.length > 1 ? -60 + index * (120 / (options.length - 1)) : 0;
                target.find('.ic-selector-knob').css('transform', `rotate(${angle}deg)`);
            }
            if (key === 'KeySwitch') target.find('.ic-keyswitch').attr('data-state', num(p.value) > 0 ? 'on' : 'off');
            const locked = !this._canControl(c, context);
            target.find('.ic-safety button, .ic-keyswitch, .ic-selector').prop('disabled', locked).toggleClass('ic-locked', locked);
            if (context.editMode) this._feedback(target, tr('编辑预览 · 运行时发送命令', 'Preview · commands are available at runtime'));
            else if (!context.controlRight) this._feedback(target, tr('只读权限', 'Read-only'));
            else if (context.mainApi?.dataPaused) this._feedback(target, tr('等待实时数据，控制已暂停', 'Awaiting live data; controls paused'));
            else if (locked && !commandBusy(c, context)) this._feedback(target, tr('请设置输出通道并启用组件', 'Set an output channel and enable the component'));
            else if (key === 'EmergencyStop' && !target.find('.ic-feedback').text()) this._feedback(target, tr('长按 0.8 秒触发', 'Press and hold for 0.8 s'));
        }
        paintInput(target, c, context) {
            const p = c.properties, key = this.spec.key;
            const canControl = this._canControl(c, context);
            let input = target.find('.ic-editor');
            if (!input.length) {
                const field = key === 'Select' ? '<select class="ic-editor"></select>' : `<input class="ic-editor" type="${key === 'Slider' ? 'range' : 'number'}">`;
                target.html(`<div class="ic-card">${title(p)}<div class="ic-input-row">${field}<button type="button" class="ic-send">${tr('发送', 'Send')}</button></div><div class="ic-footer"><span class="ic-readback"></span><span class="ic-draft"></span></div><div class="ic-feedback" role="status"></div></div>`);
                input = target.find('.ic-editor');
                input.on('input.industrial change.industrial', () => {
                    input.attr('data-dirty', 'true');
                    target.find('.ic-draft').text(tr('待发送：', 'Pending: ') + input.val());
                });
                target.find('.ic-send').on('click.industrial', async event => {
                    event.stopPropagation();
                    if (!this._canControl(c, context) || input.prop('disabled') || target.attr('data-sending') === 'true') return;
                    const value = String(input.val()).trim(), parsed = Number(value), feedback = target.find('.ic-feedback');
                    const steps = (parsed - p.minimum) / Math.max(0.000001, num(p.step, 1));
                    if (!value || !Number.isFinite(parsed) || !(p.maximum > p.minimum) || parsed < p.minimum || parsed > p.maximum || (key !== 'Select' && Math.abs(steps - Math.round(steps)) > 0.000001) || (key === 'Select' && !optionList(p).some(o => o.value === parsed))) {
                        feedback.addClass('ic-error').text(tr('请输入范围内的有效数值', 'Enter a valid value within the range')); return;
                    }
                    const confirmed = await this._send(target, c, context, parsed);
                    if (confirmed) { input.removeAttr('data-dirty'); target.find('.ic-draft').text(''); }
                });
            }
            target.find('.ic-title span:first').text(p.label);
            input.prop('disabled', !canControl).attr({min: p.minimum, max: p.maximum, step: Math.max(0.000001, num(p.step, 1)), 'aria-label': p.label});
            if (key === 'Select' && input.attr('data-options') !== p.options) {
                input.html(optionList(p).map(o => `<option value="${o.value}">${esc(o.label)}</option>`).join('')).attr('data-options', p.options);
            }
            if (!input.is(':focus') && input.attr('data-dirty') !== 'true') {
                const step = Math.max(0.000001, num(p.step, 1));
                input.val(key === 'Select' ? p.value : Number(clamp(p.minimum + Math.round((p.value - p.minimum) / step) * step, p.minimum, p.maximum).toFixed(6)));
            }
            target.find('.ic-send').prop('disabled', !canControl || target.attr('data-sending') === 'true');
            target.find('.ic-readback').text(tr('当前：', 'Current: ') + (valid(c) ? fmt(p.value, p) + ' ' + p.unit : '—'));
            if (context.editMode) target.find('.ic-feedback').text(tr('编辑预览 · 运行时发送命令', 'Preview · commands are available at runtime'));
            else if (!context.controlRight) target.find('.ic-feedback').text(tr('只读权限', 'Read-only'));
            else if (context.mainApi?.dataPaused) this._feedback(target, tr('等待实时数据，控制已暂停', 'Awaiting live data; controls paused'));
            else if (!canControl && !commandBusy(c, context)) target.find('.ic-feedback').text(tr('请设置输出通道并启用组件', 'Set an output channel and enable the component'));
            else if (canControl && target.find('.ic-feedback').text() === tr('等待实时数据，控制已暂停', 'Awaiting live data; controls paused')) this._feedback(target, '');
            // Embedded controls must not invoke the parent component's click action.
            target.off('click.industrial').on('click.industrial', event => event.stopPropagation());
        }
    }

    for (const spec of catalog) {
        M.FactorySet.componentFactories.set(spec.type, new IndustrialFactory(spec));
        M.RendererSet.componentRenderers.set(spec.type, new IndustrialRenderer(spec));
        if (M.RegularComponentDescriptor) {
            const descriptor = new M.RegularComponentDescriptor();
            for (const key of Object.keys(spec.defaults)) {
                const [en, , type, category] = fields[key];
                descriptor.add(new M.PropertyDescriptor({name: key, displayName: en, type,
                    category, ...(key.endsWith('Color') ? {editor: M.PropertyEditor.COLOR_DIALOG} : {})}));
            }
            M.DescriptorSet.componentDescriptors.set(spec.type, descriptor);
        }
    }
})();
