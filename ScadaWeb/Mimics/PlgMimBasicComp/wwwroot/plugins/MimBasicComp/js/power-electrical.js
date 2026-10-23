// Loaded after industrial.js by the runtime's alphabetically sorted script list.
// Electrical single-line symbols. Position and energization are independent controller signals.
(() => {
    'use strict';
    const M = rs.mimic, {esc, tr, num, clamp, request} = M.IndustrialSupport;
    const field = (en, cn, type = 'string', category = 'data') => ({en, cn, type, category});
    const fields = {
        roleFilter: field('Visible for role', '可见角色（all / admin）', 'string', 'behavior'),

        label: field('Title', '标题'), value: field('Position / energy preview', '位置／带电预览值', 'float'),
        openValue: field('Open feedback value', '分位反馈值', 'float'), closedValue: field('Closed feedback value', '合位反馈值', 'float'),
        tripValue: field('Trip feedback value', '跳闸反馈值', 'float'), tripChannel: field('Trip feedback channel', '跳闸反馈通道', 'int'),
        energizedChannel: field('Independent energization channel', '独立带电状态通道', 'int'),
        energizedValue: field('Energized feedback value', '带电反馈值', 'float'), deenergizedValue: field('Deenergized feedback value', '无电反馈值', 'float'),
        previewEnergized: field('Energized in editor preview', '编辑预览带电', 'bool'),
        vertical: field('Vertical', '垂直方向', 'bool', 'layout'), showLabel: field('Show title', '显示标题', 'bool', 'appearance'),
        showState: field('Show state text', '显示状态文字', 'bool', 'appearance'), labelPosition: field('Label position (right/left/top/bottom)', '文字位置（right/left/top/bottom）', 'string', 'layout'),
        lineWidth: field('Line width', '线条宽度', 'float', 'appearance'), route: field('Line route (straight/elbow/tee)', '接线形状（straight/elbow/tee）', 'string', 'layout'),
        showJunction: field('Show junction dot', '显示连接点', 'bool', 'appearance'),
        energizedColor: field('Energized color', '带电颜色', 'string', 'appearance'), deenergizedColor: field('Deenergized color', '无电颜色', 'string', 'appearance'),
        unknownColor: field('Unknown color', '未知颜色', 'string', 'appearance'), openColor: field('Open color', '分闸颜色', 'string', 'appearance'),
        closedColor: field('Closed color', '合闸颜色', 'string', 'appearance'), tripColor: field('Trip color', '跳闸颜色', 'string', 'appearance')
    };
    const common = {value: 0, energizedValue: 1, deenergizedValue: 0, previewEnergized: false, vertical: true, showLabel: true, showState: true, labelPosition: 'right', lineWidth: 3,
        energizedColor: '#d32f2f', deenergizedColor: '#526880', unknownColor: '#94a3b8'};
    const switchDefaults = {...common, openValue: 0, closedValue: 1, energizedChannel: 0, openColor: '#138b65', closedColor: '#d32f2f'};
    const catalog = [
        ['CircuitBreaker', 'Circuit breaker', '断路器', 100, 160, {...switchDefaults, tripValue: 2, tripChannel: 0, tripColor: '#b76b06'}],
        ['Disconnector', 'Disconnector', '隔离开关', 100, 150, {...switchDefaults}],
        ['GroundSwitch', 'Ground switch', '接地开关', 100, 150, {...switchDefaults}],
        ['Transformer', 'Transformer', '变压器', 140, 140, {...common}],
        ['Busbar', 'Busbar', '母线', 500, 24, {...common, vertical: false, labelPosition: 'top', lineWidth: 7}],
        ['ElectricalLine', 'Electrical connection', '电气连接线', 24, 180, {...common, showLabel: false, showState: false, route: 'straight', showJunction: false}]
    ].map(([key, en, cn, width, height, defaults]) => ({key, en, cn, type: 'Industrial' + key, width, height, defaults: {label: tr(cn, en), roleFilter: 'all', ...defaults}}));
    const switches = new Set(['CircuitBreaker', 'Disconnector', 'GroundSwitch']);
    const good = d => d && [1, 11, 12, 13, 14, 15].includes(d.stat) && Number.isFinite(d.val);
    const inputChannel = c => c.bindings?.inCnlNum || c.properties.inCnlNum || 0;
    const binary = (value, on, off) => on !== off && value === on ? true : on !== off && value === off ? false : null;
    function state(c, key, context, now = Date.now()) {
        const p = c.properties, cache = c.customData?.electrical;
        const live = !context.mainApi?.dataPaused && cache && now - cache.readAt <= 15000;
        const read = channel => live && channel > 0 && good(cache.records[channel]) ? cache.records[channel].val : null;
        const value = context.editMode ? p.value : read(inputChannel(c));
        const energizedValue = context.editMode ? (p.previewEnergized ? p.energizedValue : p.deenergizedValue) : read(switches.has(key) ? p.energizedChannel : inputChannel(c));
        let position = value !== null && p.openValue !== p.closedValue ? value === p.openValue ? 'open' : value === p.closedValue ? 'closed' : 'unknown' : 'unknown';
        let tripUnknown = false;
        if (key === 'CircuitBreaker') {
            const trip = context.editMode ? null : p.tripChannel > 0 ? read(p.tripChannel) : null;
            tripUnknown = !context.editMode && p.tripChannel > 0 && binary(trip, 1, 0) === null;
            // A configured trip input takes precedence over the position channel.
            if ((p.tripChannel > 0 && trip === 1) || (p.tripChannel <= 0 && p.tripValue !== p.openValue && p.tripValue !== p.closedValue && value === p.tripValue)) position = 'trip';
        }
        return {position, energized: energizedValue === null ? null : binary(energizedValue, p.energizedValue, p.deenergizedValue), tripUnknown};
    }
    const positionText = s => s.position === 'open' ? tr('分闸', 'Open') : s.position === 'closed' ? tr('合闸', 'Closed') : s.position === 'trip' ? tr('跳闸', 'Tripped') : tr('位置未知', 'Position unknown');
    const energyText = s => s.energized === true ? tr('带电', 'Energized') : s.energized === false ? tr('无电反馈', 'Deenergized feedback') : tr('带电状态未知', 'Energization unknown');
    function markup(key, p, s, editMode = false) {
        const isSwitch = switches.has(key), grounded = key === 'GroundSwitch';
        const energyColor = esc(s.energized === null ? p.unknownColor : s.energized ? p.energizedColor : p.deenergizedColor);
        const positionColor = esc(s.position === 'open' ? p.openColor : s.position === 'closed' ? p.closedColor : s.position === 'trip' ? p.tripColor : p.unknownColor);
        const width = clamp(num(p.lineWidth, 3), 1, 12);
        const stroke = (color, extra = '') => `fill="none" stroke="${color}" stroke-width="${width}" vector-effect="non-scaling-stroke" ${extra}`;
        const wire = path => `<path d="${path}" ${stroke(energyColor, s.energized === null ? 'stroke-dasharray="5 4"' : '')}/>`;
        const pole = body => `<g ${stroke(positionColor, s.position === 'unknown' ? 'stroke-dasharray="4 3"' : '')}>${body}</g>`;
        let body;
        if (key === 'CircuitBreaker') {
            body = wire('M50 0 V35 M50 65 V100') + pole(`<rect x="37" y="35" width="26" height="30"/>` + (s.position === 'closed' ? '<path d="M50 35 V65"/>' : '<path d="M50 35 V42 M50 58 V65"/>') + (s.position === 'trip' ? '<path d="M42 44 L58 56 M58 44 L42 56"/>' : ''));
        } else if (key === 'Disconnector' || grounded) {
            body = wire(grounded ? 'M50 0 V32' : 'M50 0 V32 M50 68 V100') + pole(`<circle cx="50" cy="32" r="2.5"/><circle cx="50" cy="68" r="2.5"/><path d="${s.position === 'closed' ? 'M50 35 V65' : 'M50 65 L74 39'}"/>`);
            if (grounded) body += `<path d="M50 71 V84 M32 84 H68 M38 91 H62 M44 98 H56" ${stroke('#526880')}/>`;
        } else if (key === 'Transformer') body = wire('M50 0 V21 M50 79 V100') + `<g ${stroke(energyColor, s.energized === null ? 'stroke-dasharray="5 4"' : '')}><circle cx="50" cy="38" r="17"/><circle cx="50" cy="62" r="17"/></g>`;
        else if (key === 'Busbar') body = wire('M0 50 H100');
        else {
            body = wire(p.route === 'elbow' ? 'M50 0 V50 H100' : p.route === 'tee' ? 'M0 50 H100 M50 50 V100' : 'M50 0 V100');
        }
        const horizontal = !p.vertical, rotate = key === 'Busbar' ? (p.vertical ? 90 : 0) : horizontal ? 90 : 0;
        const description = (isSwitch ? positionText(s) + (s.tripUnknown ? tr(' · 跳闸反馈未知', ' · trip feedback unknown') : '') + ' · ' : '') + energyText(s);
        const labelPosition = ['right', 'left', 'top', 'bottom'].includes(p.labelPosition) ? p.labelPosition : 'right';
        return `<div class="el-body" data-position="${isSwitch ? s.position : 'none'}" data-energy="${s.energized === null ? 'unknown' : s.energized ? 'on' : 'off'}"><svg viewBox="0 0 100 100" preserveAspectRatio="none" role="img" aria-label="${esc(p.label + ' · ' + description)}"><g transform="rotate(${rotate} 50 50)">${body}</g>${isSwitch && s.position === 'unknown' ? '<text x="50" y="54" text-anchor="middle" fill="#70500b" font-size="15">?</text>' : ''}</svg>${key === 'ElectricalLine' && p.showJunction ? `<span class="el-junction" aria-hidden="true" style="background-color:${energyColor}"></span>` : ''}<div class="el-label el-label-${labelPosition}">${p.showLabel ? `<strong>${esc(p.label)}</strong>` : ''}${p.showState ? `<span class="el-position" style="color:${isSwitch ? positionColor : energyColor}">${esc(isSwitch ? positionText(s) : energyText(s))}</span>${isSwitch ? `<small>${esc(energyText(s) + (s.tripUnknown ? tr(' · 跳闸反馈未知', ' · trip feedback unknown') : ''))}</small>` : ''}` : ''}${editMode && (p.showLabel || p.showState) ? `<small>${tr('编辑预览', 'Editor preview')}</small>` : ''}</div></div>`;
    }
    class ElectricalFactory extends M.RegularComponentFactory {
        constructor(spec) {super(); this.spec = spec;}
        createProperties() {const p = super.createProperties(); Object.assign(p, this.spec.defaults); p.size = new M.Size({width: this.spec.width, height: this.spec.height}); return p;}
        parseProperties(source) {const p = super.parseProperties(source); for (const [key, value] of Object.entries(this.spec.defaults)) p[key] = typeof value === 'boolean' ? M.PropertyParser.parseBool(source?.[key] ?? value) : typeof value === 'number' ? num(source?.[key], value) : String(source?.[key] ?? value); return p;}
        createComponent() {return super.createComponent(this.spec.type);}
    }
    // One shared current-data request per page, including optional energy and trip inputs.
    const entries = new Map(); let timer = null, polling = false, nextRead = 0;
    function channels(c, key) {return [...new Set([inputChannel(c), ...(switches.has(key) ? [c.properties.energizedChannel] : []), ...(key === 'CircuitBreaker' ? [c.properties.tripChannel] : [])])].filter(n => Number.isInteger(n) && n > 0);}
    async function poll() {
        if (polling) return;
        const connected = [...entries.values()].filter(e => e.c.dom?.[0]?.isConnected);
        const numbers = [...new Set(connected.flatMap(e => channels(e.c, e.renderer.spec.key)))];
        if (!numbers.length) return;
        polling = true;
        try {
            const rows = [];
            for (let i = 0; i < numbers.length; i += 128) rows.push(...await request(connected[0].context, 'GetCurData', {cnlNums: numbers.slice(i, i + 128).join(',')}));
            const records = Object.fromEntries(rows.map(r => [r.cnlNum, r]));
            for (const entry of connected) {entry.c.customData ??= {}; entry.c.customData.electrical = {records, readAt: Date.now(), expired: false};}
        } catch {
            for (const entry of connected) {entry.c.customData ??= {}; entry.c.customData.electrical = {records: {}, readAt: 0, expired: true};}
        } finally {
            polling = false;
            for (const entry of connected) if (entries.get(entry.c) === entry && entry.c.dom?.[0]?.isConnected) entry.renderer.paint(entry.c.dom, entry.c, entry.context);
        }
    }
    function register(c, context, renderer) {
        if (context.editMode) return;
        const previous = entries.get(c);
        const signature = channels(c, renderer.spec.key).join(',');
        if (!previous || previous.signature !== signature) {nextRead = 0; c.customData ??= {}; c.customData.electrical = {records: {}, readAt: 0};}
        entries.set(c, {c, context, renderer, signature});
        if (!timer) timer = setInterval(() => {
            for (const [component, entry] of entries) {
                if (!component.dom?.[0]?.isConnected) {entries.delete(component); continue;}
                const cache = component.customData?.electrical;
                if (cache && !cache.expired && Date.now() - cache.readAt > 15000) {cache.expired = true; entry.renderer.paint(component.dom, component, entry.context);}
            }
            if (!entries.size) {clearInterval(timer); timer = null; return;}
            if (Date.now() >= nextRead) {nextRead = Date.now() + 5000; void poll();}
        }, 1000);
    }
    class ElectricalRenderer extends M.RegularComponentRenderer {
        constructor(spec) {super(); this.spec = spec;}
        _completeDom(elem) {elem.append('<div class="el-host"></div>');}
        _setClasses(elem, c, context) {super._setClasses(elem, c, context); elem.addClass('industrial electrical').attr('data-electrical-type', this.spec.key);}
        _setProps(elem, c, context) {super._setProps(elem, c, context); register(c, context, this); this.paint(elem, c, context); M.IndustrialRoleFilter?.(elem, c, context); M.IndustrialTooltip.bind(elem, c, context);}
        paint(elem, c, context) {elem.children('.el-host').html(markup(this.spec.key, c.properties, state(c, this.spec.key, context), context.editMode));}
    }
    for (const spec of catalog) {
        M.FactorySet.componentFactories.set(spec.type, new ElectricalFactory(spec));
        M.RendererSet.componentRenderers.set(spec.type, new ElectricalRenderer(spec));
        const descriptor = new M.RegularComponentDescriptor();
        for (const key of Object.keys(spec.defaults)) {const f = fields[key]; descriptor.add(new M.PropertyDescriptor({name: key, displayName: tr(f.cn, f.en), type: f.type, category: f.category, ...(key.endsWith('Color') ? {editor: M.PropertyEditor.COLOR_DIALOG} : {})}));}
        M.DescriptorSet.componentDescriptors.set(spec.type, descriptor);
    }
    M.IndustrialCatalog.push(...catalog);
    M.Electrical = {catalog, state, markup, channels};
})();
