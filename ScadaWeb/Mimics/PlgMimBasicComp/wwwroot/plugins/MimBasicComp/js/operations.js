// Runtime workflow components. All reads/writes use the authenticated server APIs.
(() => {
    'use strict';
    const M = rs.mimic, S = M.IndustrialSupport, {esc, tr, num, clamp} = S;
    const field = (en, cn, type = 'string', category = 'data') => ({en, cn, type, category});
    const fields = {
        roleFilter: field('Visible for role', '可见角色（all / admin）', 'string', 'behavior'),

        label: field('Title', '标题'), viewIds: field('View IDs', '导航视图编号列表'), viewId: field('Target view ID', '目标视图编号', 'int'),
        channels: field('Channels', '通道列表'), labels: field('Labels', '名称列表'), archiveBit: field('Archive bit', '归档位号', 'int'),
        maxRows: field('Maximum rows', '最大行数', 'int'), unit: field('Unit', '单位'), minimum: field('Minimum', '最小值', 'float'), maximum: field('Maximum', '最大值', 'float'), step: field('Step', '步长', 'float'),
        feedbackChannel: field('Feedback channel', '反馈通道', 'int'), setpointChannel: field('Setpoint output', '设定输出通道', 'int'),
        processChannel: field('Process value channel', '过程值通道', 'int'), outputChannel: field('Control output feedback', '控制输出反馈通道', 'int'),
        modeChannel: field('Mode channel', '模式反馈通道', 'int'), modeOutputChannel: field('Mode output channel', '模式输出通道', 'int'),
        statusChannel: field('Device online channel', '设备在线状态通道', 'int'), staleSeconds: field('Read timeout seconds', '读取过期秒数', 'int'),
        tabs: field('Tab names', '选项卡名称'), collapsed: field('Initially collapsed', '初始折叠', 'bool'), historyMinutes: field('History minutes', '历史时长（分钟）', 'int'),
        expectedValues: field('Expected condition values', '条件期望值列表'), permitChannel: field('Controller permit channel', '控制器最终许可通道', 'int'),
        faultChannel: field('Fault code channel', '故障代码通道', 'int'), faultDefinitions: field('Fault definitions (JSON)', '故障说明（JSON）'), policyId: field('Server reset policy ID', '服务端复位策略编号')
    };
    const catalog = [
        ['NavBar', 'Page navigation', '页面导航栏', 900, 68, {viewIds: ''}],
        ['NavButton', 'Navigation button', '导航按钮', 200, 65, {viewId: 10}],
        ['BackButton', 'Back button', '返回按钮', 160, 65, {viewId: 10}],
        ['DevicePanel', 'Device operation panel', '设备操作面板', 410, 310, {channels: '', labels: '', feedbackChannel: 0, minimum: 0, maximum: 100, step: 1, unit: ''}],
        ['ActiveAlarmTable', 'Active alarms', '当前报警表', 700, 330, {channels: '', archiveBit: 4, maxRows: 12}],
        ['HistoryQuery', 'History query', '历史查询工具条', 700, 420, {channels: '', archiveBit: 1, historyMinutes: 60, maxRows: 100}],
        ['CommStatus', 'Communication status', '通信状态卡', 360, 190, {channels: '', statusChannel: 0, staleSeconds: 15}],
        ['Tabs', 'Tabs container', '选项卡容器', 600, 300, {tabs: tr('状态,参数,趋势', 'Status,Parameters,Trend')}],
        ['CollapsiblePanel', 'Collapsible container', '折叠容器', 600, 260, {collapsed: false}],
        ['OperationLog', 'Operation log', '操作记录表', 720, 350, {maxRows: 50}],
        ['Recipe', 'Recipe parameters', '配方与参数组', 760, 470, {channels: ''}],
        ['Schedule', 'Scheduled operation', '计划运行', 760, 490, {channels: ''}],
        ['PidPanel', 'PID loop panel', 'PID 回路面板', 440, 340, {processChannel: 0, feedbackChannel: 0, setpointChannel: 0, outputChannel: 0, modeChannel: 0, modeOutputChannel: 0, minimum: 0, maximum: 100, step: 1, unit: ''}],
        ['InterlockPanel', 'Interlock and operation permit', '联锁与操作许可', 620, 340, {channels: '', labels: '', expectedValues: '', permitChannel: 0}],
        ['FaultDiagnostics', 'Fault diagnostics', '故障诊断面板', 620, 340, {faultChannel: 0, faultDefinitions: '{}', policyId: ''}],
        ['AlarmStatistics', 'Alarm statistics', '报警统计与排行', 900, 460, {channels: '', archiveBit: 4, historyMinutes: 1440, maxRows: 20}]
    ].map(([key, en, cn, width, height, defaults]) => ({key, en, cn, type: 'Industrial' + key, width, height, defaults: {label: tr(cn, en), roleFilter: 'all', ...defaults}}));
    const containers = new Set(['Tabs', 'CollapsiblePanel']);
    const good = d => d && [1, 11, 12, 13, 14, 15].includes(d.stat) && Number.isFinite(d.val);
    const ids = text => [...new Set(String(text || '').split(/[,，;；\s]+/).filter(Boolean).map(Number))].filter(n => Number.isInteger(n) && n > 0).slice(0, 64);
    const root = context => context.mainApi?.rootPath || '/';
    async function api(context, action, params = {}, body) {
        const headers = {};
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
            headers.RequestVerificationToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        }
        const response = await fetch(root(context) + 'Api/Operations/' + action + (body === undefined ? '?' + new URLSearchParams(params) : ''),
            {method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', headers, ...(body !== undefined ? {body: JSON.stringify(body)} : {}), signal: AbortSignal.timeout(body !== undefined ? 90000 : 15000)});
        if (response.redirected || response.status === 401) throw new Error(tr('登录已失效，请重新登录', 'Session expired; sign in again'));
        if (response.status === 403) throw new Error(tr('当前账号没有此操作权限', 'Permission denied'));
        let dto; try { dto = await response.json(); } catch { throw new Error(tr('请求失败：', 'Request failed: ') + response.status); }
        if (!response.ok || !dto.ok) throw new Error(dto.msg || tr('请求失败', 'Request failed'));
        return dto.data;
    }
    const table = (heads, rows) => `<div class="op-scroll"><table><thead><tr>${heads.map(h => `<th>${esc(h)}</th>`).join('')}</tr></thead><tbody>${rows.join('')}</tbody></table></div>`;
    const cells = values => `<tr>${values.map(v => `<td>${esc(v)}</td>`).join('')}</tr>`;
    const button = (action, text) => `<button type="button" data-op="${action}">${esc(text)}</button>`;
    const input = (name, label, type = 'text', value = '', extra = '') => `<label>${esc(label)}<input data-field="${name}" aria-label="${esc(label)}" type="${type}" value="${esc(value)}" ${extra}></label>`;
    const localTime = date => {const d = new Date(date); return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);};
    function message(ui, text, error = false) { ui.body.find('.op-message').text(text).toggleClass('ic-error', error); }
    let previousView = 0;
    try {
        const current = typeof viewID === 'number' ? viewID : 0;
        const last = Number(sessionStorage.getItem('scada.currentView'));
        previousView = last !== current ? last : Number(sessionStorage.getItem('scada.previousView'));
        if (current > 0 && current !== last) { sessionStorage.setItem('scada.previousView', String(last)); sessionStorage.setItem('scada.currentView', String(current)); }
    } catch { /* Navigation does not require browser storage. */ }

    class OperationsFactory extends M.RegularComponentFactory {
        constructor(spec) { super(); this.spec = spec; }
        createProperties() { const p = super.createProperties(); Object.assign(p, this.spec.defaults); p.size = new M.Size({width: this.spec.width, height: this.spec.height}); return p; }
        parseProperties(source) {
            const p = super.parseProperties(source);
            for (const [key, value] of Object.entries(this.spec.defaults)) p[key] = typeof value === 'number' ? num(source?.[key], value) : typeof value === 'boolean' ? M.PropertyParser.parseBool(source?.[key] ?? value) : String(source?.[key] ?? value);
            return p;
        }
        createComponent() { const c = super.createComponent(this.spec.type); if (containers.has(this.spec.key)) c.children = []; return c; }
        createComponentFromSource(source) { const c = super.createComponentFromSource(source); if (containers.has(this.spec.key)) c.children = []; return c; }
    }

    const polling = new Map();
    let timer;
    function register(c, ui, poll) {
        if (ui.context.editMode) return;
        polling.set(c, {ui, poll, busy: false, next: Date.now()});
        if (!timer) timer = setInterval(() => {
            for (const [component, entry] of polling) {
                if (!component.dom?.[0]?.isConnected) { polling.delete(component); continue; }
                if (!entry.busy && Date.now() >= entry.next) {
                    entry.busy = true; entry.next = Date.now() + 5000;
                    entry.poll().then(() => {if (entry.failed) message(entry.ui, ''); entry.failed = false;})
                        .catch(error => {entry.failed = true; message(entry.ui, error.message, true);}).finally(() => {entry.busy = false;});
                }
            }
            if (!polling.size) {clearInterval(timer); timer = null;}
        }, 1000);
    }
    const canControl = (c, ui) => !ui.context.editMode && ui.context.controlRight && !ui.context.mainApi?.dataPaused && c.properties.enabled;
    function refreshControls(c, ui, key) {
        if (key === 'DevicePanel' || key === 'PidPanel') {
            const p = c.properties, allowed = canControl(c, ui);
            ui.body.find('[data-op=start], [data-op=stop]').prop('disabled', !allowed || !(p.outCnlNum > 0));
            ui.body.find('[data-op=set]').prop('disabled', !allowed || !((key === 'PidPanel' ? p.setpointChannel : p.outCnlNum) > 0));
            ui.body.find('[data-op=manual], [data-op=auto]').prop('disabled', !allowed || !(p.modeOutputChannel > 0));
            ui.body.find('[data-field=setpoint]').prop('disabled', !allowed);
        } else if (key === 'Recipe') ui.body.find('[data-op=run]').prop('disabled', !canControl(c, ui));
        else if (key === 'FaultDiagnostics') ui.body.find('[data-op=reset]').prop('disabled', ui.busy || !ui.resetAllowed || !canControl(c, ui));
    }
    function bindActions(c, ui, actions) {
        ui.body.off('click.operations').on('click.operations', '[data-op]', async event => {
            event.preventDefault(); event.stopPropagation();
            if (ui.busy) return;
            const action = event.currentTarget.dataset.op;
            if (!actions[action]) return;
            ui.busy = true;
            $(event.currentTarget).prop('disabled', true);
            try { await actions[action](); }
            catch (error) {message(ui, error.message, true);}
            finally {ui.busy = false; $(event.currentTarget).prop('disabled', ui.context.editMode); if (ui.kind === 'FaultDiagnostics') refreshControls(c, ui, ui.kind);}
        });
        ui.body.off('click.opStop').on('click.opStop', event => event.stopPropagation());
    }
    function preview(ui, text) {message(ui, tr('编辑预览 · ', 'Editor preview · ') + text); ui.body.find('input,select,button').prop('disabled', true);}
    async function current(ui, channels) { return S.request(ui.context, 'GetCurData', {cnlNums: channels.join(',')}); }
    function readback(ui, records, channels) {
        const labels = String(ui.c.properties.labels || '').split(/[,，]/);
        return channels.map((n, i) => {const data = records.find(r => r.cnlNum === n); return cells([labels[i] || n, good(data) ? data.val.toFixed(2) : tr('无有效数据', 'No valid data')]);});
    }
    async function send(c, ui, output, feedback, value, minimum, maximum, step) {
        if (!canControl(c, ui)) throw new Error(tr('当前不可控制', 'Control unavailable'));
        if (!(output > 0) || !Number.isFinite(value) || value < minimum || value > maximum || !(step > 0) || Math.abs((value - minimum) / step - Math.round((value - minimum) / step)) > 1e-6) throw new Error(tr('请检查输出通道、范围和步长', 'Check channel, range and step'));
        const command = {properties: {enabled: true, outCnlNum: output, inCnlNum: feedback || output, step}, bindings: {outCnlNum: output, inCnlNum: feedback || output}};
        return M.IndustrialCommands.sendAndConfirm(command, ui.context, value, (text, ok) => message(ui, text, ok === false));
    }
    async function nav(c, ui, spec) {
        let views = ui.context.editMode ? [{id: 10, title: tr('运行总览', 'Overview'), url: '#'}, {id: 21, title: tr('储罐泵站', 'Tank'), url: '#'}] : await api(ui.context, 'Views');
        const selected = ids(c.properties.viewIds);
        if (selected.length) views = views.filter(v => selected.includes(v.id));
        if (spec.key !== 'NavBar') views = views.filter(v => v.id === (spec.key === 'BackButton' ? previousView || c.properties.viewId : c.properties.viewId));
        const currentId = typeof viewID === 'number' ? viewID : 0;
        ui.body.find('.op-content').html(views.map(v => `<a href="${esc(v.url)}" target="_top" ${v.id === currentId ? 'aria-current="page"' : ''}>${esc(spec.key === 'NavBar' ? v.title : c.properties.label)}</a>`).join('') || esc(tr('没有可访问的目标画面', 'No accessible target view')));
        if (ui.context.editMode) ui.body.find('a').removeAttr('href').attr('aria-disabled', 'true');
    }
    function initDevice(c, ui, pid = false) {
        const p = c.properties;
        const channels = pid ? [p.processChannel, p.feedbackChannel, p.outputChannel, p.modeChannel].filter(n => n > 0) : ids(p.channels);
        ui.body.find('.op-content').html(`<div class="op-values"></div><div class="op-controls">${pid ? '' : button('start', tr('启动', 'Start')) + button('stop', tr('停止', 'Stop'))}
            ${input('setpoint', tr('设定值', 'Setpoint'), 'number', p.minimum, `min="${p.minimum}" max="${p.maximum}" step="${p.step}"`)}${button('set', tr('下发设定', 'Send setpoint'))}
            ${pid ? button('manual', tr('手动', 'Manual')) + button('auto', tr('自动', 'Auto')) : ''}</div>
            ${pid ? `<p class="op-note">${tr('连接控制器回路：PV=过程值，SP=设定值，MV=输出，模式 0=手动/1=自动；PID 算法由控制器执行。', 'Connect controller PV/SP/MV and mode (0=manual, 1=auto). The controller executes PID.')}</p>` : ''}`);
        const render = async () => {
            refreshControls(c, ui, pid ? 'PidPanel' : 'DevicePanel');
            if (!channels.length) {message(ui, tr('请绑定反馈通道', 'Bind feedback channels')); return;}
            const values = await current(ui, channels);
            if (pid) {
                const names = [tr('过程值 PV', 'Process PV'), tr('设定值 SP', 'Setpoint SP'), tr('输出 MV', 'Output MV'), tr('模式', 'Mode')];
                ui.body.find('.op-values').html(table([tr('信号', 'Signal'), tr('反馈', 'Feedback')], [p.processChannel, p.feedbackChannel, p.outputChannel, p.modeChannel].map((n, i) => {
                    const d = values.find(r => r.cnlNum === n);
                    return cells([names[i], n > 0 && good(d) ? (i === 3 ? (d.val === 0 ? tr('手动', 'Manual') : d.val === 1 ? tr('自动', 'Auto') : tr('未知模式', 'Unknown mode')) : d.val.toFixed(2) + ' ' + (i === 2 ? '%' : p.unit)) : tr('未绑定或无有效数据', 'Unbound or invalid')]);
                })));
            } else ui.body.find('.op-values').html(table([tr('信号', 'Signal'), tr('反馈', 'Feedback')], readback(ui, values, channels)));
        };
        bindActions(c, ui, {
            start: () => send(c, ui, p.outCnlNum, p.feedbackChannel || p.inCnlNum, 1, 0, 1, 1),
            stop: () => send(c, ui, p.outCnlNum, p.feedbackChannel || p.inCnlNum, 0, 0, 1, 1),
            set: () => send(c, ui, pid ? p.setpointChannel : p.outCnlNum, p.feedbackChannel || p.inCnlNum, Number(ui.body.find('[data-field=setpoint]').val()), p.minimum, p.maximum, p.step),
            manual: () => send(c, ui, p.modeOutputChannel, p.modeChannel, 0, 0, 1, 1),
            auto: () => send(c, ui, p.modeOutputChannel, p.modeChannel, 1, 0, 1, 1)
        });
        ui.body.find('[data-op]').prop('disabled', !canControl(c, ui));
        if (pid) {
            ui.body.find('[data-op=set]').prop('disabled', !canControl(c, ui) || !(p.setpointChannel > 0));
            ui.body.find('[data-op=manual], [data-op=auto]').prop('disabled', !canControl(c, ui) || !(p.modeOutputChannel > 0));
        }
        if (ui.context.editMode) preview(ui, tr('运行时读取绑定的设备信号', 'Reads bound controller signals at runtime'));
        else register(c, ui, render);
    }
    async function showAck(ui, eventId, archiveBit) {
        if (!/^\d+$/.test(String(eventId))) throw new Error(tr('没有可确认的事件', 'No event to acknowledge'));
        const url = root(ui.context) + 'Main/EventAck?eventID=' + eventId + '&archiveBit=' + archiveBit;
        if (ui.context.viewHub?.modalManager) ui.context.viewHub.modalManager.showModal(url, new ModalOptions({buttons: [ModalButton.OK, ModalButton.CLOSE], size: ModalSize.LARGE}), () => ui.refresh?.());
        else window.open(url, '_blank', 'noopener');
    }
    function initAlarms(c, ui, ackOnly = false) {
        ui.body.find('.op-content').html(`<div class="op-controls"><label><input type="checkbox" data-field="unacked">${tr('仅待确认', 'Unacknowledged only')}</label>${button('refresh', tr('刷新', 'Refresh'))}</div><div class="op-results"></div>`);
        ui.refresh = async () => {
            const data = await api(ui.context, 'ActiveAlarms', {channels: c.properties.channels || '', archiveBit: c.properties.archiveBit || 4});
            const rows = data.rows.filter(r => !ui.body.find('[data-field=unacked]').prop('checked') || r.ack === false);
            const limits = {11: tr('低低限', 'Low low'), 12: tr('低限', 'Low'), 14: tr('高限', 'High'), 15: tr('高高限', 'High high')};
            ui.body.find('.op-results').html(rows.length ? table([tr('通道', 'Channel'), tr('当前越限', 'Active condition'), tr('测量', 'Value'), tr('确认', 'Acknowledgement'), tr('定位', 'Locate')], rows.slice(0, clamp(num(c.properties.maxRows, 12), 1, 100)).map(r => `<tr><td>${esc(r.name)}</td><td class="ic-error">${esc(limits[r.status])}</td><td>${esc(r.value)} ${esc(r.unit)}</td><td>${r.canAck ? `<button type="button" data-ack="${esc(r.eventId)}">${tr('查看并确认', 'Review and acknowledge')}</button>` : esc(r.ack === true ? tr('已确认，仍未恢复', 'Acknowledged, still active') : r.ack === false ? tr('待确认', 'Unacknowledged') : tr('等待事件归档', 'Awaiting event archive'))}</td><td>${r.viewId ? `<a href="${root(ui.context)}View/${r.viewId}" target="_top">${tr('设备画面', 'Device view')}</a>` : '—'}</td></tr>`)) : `<p>${tr('当前没有匹配的未恢复报警', 'No matching active alarms')}</p>`);
            message(ui, tr('未恢复 ', 'Active ') + data.rows.length + tr(' 点；确认不代表恢复。无有效数据 ', '; acknowledgement does not clear conditions. Invalid data: ') + data.unavailable);
            ui.body.find('[data-ack]').on('click', event => {event.stopPropagation(); void showAck(ui, event.currentTarget.dataset.ack, c.properties.archiveBit || 4);});
        };
        bindActions(c, ui, {refresh: () => ui.refresh()});
        ui.body.find('[data-field=unacked]').on('change', () => ui.refresh().catch(error => message(ui, error.message, true)));
        if (ui.context.editMode) preview(ui, tr('按实时越限状态显示，可直接确认对应事件', 'Shows live active conditions and event acknowledgement'));
        else register(c, ui, ui.refresh);
    }
    function initHistory(c, ui) {
        const p = c.properties, end = new Date(), start = new Date(end - clamp(p.historyMinutes, 1, 10080) * 60000);
        ui.body.find('.op-content').html(`<div class="op-controls">${input('channels', tr('通道编号', 'Channel numbers'), 'text', p.channels)}${input('start', tr('开始时间', 'Start'), 'datetime-local', localTime(start))}${input('end', tr('结束时间', 'End'), 'datetime-local', localTime(end))}</div><div class="op-controls">${button('hour', tr('最近一小时', 'Last hour'))}${button('day', tr('最近一天', 'Last day'))}${button('query', tr('查询', 'Query'))}${button('export', tr('导出 CSV', 'Export CSV'))}</div><div class="op-results"></div>`);
        let csvRows = [], queryGeneration = 0;
        const query = async () => {
            const generation = ++queryGeneration;
            csvRows = []; ui.body.find('[data-op=export]').prop('disabled', true);
            ui.body.find('.op-results').empty();
            const channels = ids(ui.body.find('[data-field=channels]').val());
            const from = new Date(ui.body.find('[data-field=start]').val()), to = new Date(ui.body.find('[data-field=end]').val());
            if (!channels.length || !Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime()) || to <= from || to - from > 7 * 86400000) throw new Error(tr('请选择通道和有效时间范围（最长 7 天）', 'Choose channels and a valid range up to 7 days'));
            message(ui, tr('正在查询真实归档…', 'Querying archive…'));
            const data = await S.request(ui.context, 'GetHistData', {archiveBit: p.archiveBit, cnlNums: channels.join(','), startTime: from.toISOString(), endTime: to.toISOString(), endInclusive: true});
            if (generation !== queryGeneration) return;
            (data.timestamps || []).forEach((stamp, i) => channels.forEach((channel, j) => {const d = data.trends?.[j]?.[i]?.d; csvRows.push([typeof stamp === 'object' ? stamp.ut : stamp, channel, good(d) ? d.val : '', d?.stat ?? 0]);}));
            ui.body.find('.op-results').html(csvRows.length ? table([tr('时间 UTC', 'Time UTC'), tr('通道', 'Channel'), tr('数值', 'Value'), tr('状态', 'Status')], csvRows.slice(0, clamp(p.maxRows, 1, 500)).map(cells)) : `<p>${tr('所选时段暂无归档数据', 'No archive samples in this range')}</p>`);
            ui.body.find('[data-op=export]').prop('disabled', !csvRows.length);
            message(ui, tr('共 ', 'Total ') + csvRows.length + tr(' 条记录；CSV 导出全部查询结果，时间为 UTC。', ' records; CSV includes all results with UTC timestamps.'));
        };
        const quick = async hours => {const now = new Date(); ui.body.find('[data-field=start]').val(localTime(new Date(now - hours * 3600000))); ui.body.find('[data-field=end]').val(localTime(now)); await query();};
        bindActions(c, ui, {query, hour: () => quick(1), day: () => quick(24), export: async () => {
            if (!csvRows.length) return;
            const csv = [['TimeUTC', 'Channel', 'Value', 'Status'], ...csvRows].map(row => row.map(value => '"' + String(value).replaceAll('"', '""') + '"').join(',')).join('\r\n');
            const url = URL.createObjectURL(new Blob(['\ufeff' + csv], {type: 'text/csv;charset=utf-8'}));
            const link = document.createElement('a'); link.href = url; link.download = 'scada-history.csv'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        }});
        ui.body.find('[data-op=export]').prop('disabled', true);
        ui.body.find('input').on('input change', () => {queryGeneration++; csvRows = []; ui.body.find('[data-op=export]').prop('disabled', true); message(ui, tr('查询条件已变更，请重新查询', 'Filters changed; run the query again'));});
        if (ui.context.editMode) preview(ui, tr('运行时选择通道与时间查询、导出', 'Select channels and time at runtime'));
    }
    function initCommunication(c, ui) {
        const p = c.properties;
        ui.body.find('.op-content').html('<div class="op-health"></div><div class="op-readtime"></div>');
        let lastRead = 0;
        const poll = async () => {
            const channels = [...new Set([...ids(p.channels), ...(p.statusChannel > 0 ? [p.statusChannel] : [])])];
            if (!channels.length) throw new Error(tr('请绑定监测通道', 'Bind channels to monitor'));
            try {
                const rows = await current(ui, channels); lastRead = Date.now();
                const valid = rows.filter(good).length;
                const online = rows.find(r => r.cnlNum === p.statusChannel);
                ui.body.find('.op-health').text(tr('数据服务可读：', 'Data service readable: ') + valid + '/' + channels.length + (p.statusChannel > 0 ? ' · ' + (good(online) ? online.val > 0 ? tr('设备在线', 'Device online') : tr('设备离线', 'Device offline') : tr('设备状态未知', 'Device status unknown')) : ' · ' + tr('设备在线信号未绑定', 'Device online signal unbound')));
                ui.body.find('.op-readtime').text(tr('最后成功读取：', 'Last successful read: ') + new Date(lastRead).toLocaleString()); message(ui, tr('读取时间不等于设备采样时间', 'Read time is not device acquisition time'));
            } catch (error) {
                ui.body.find('.op-health').text(lastRead && Date.now() - lastRead < clamp(p.staleSeconds, 5, 3600) * 1000 ? tr('读取失败，等待重试', 'Read failed, retrying') : tr('读取已过期，状态未知', 'Read stale; status unknown'));
                throw error;
            }
        };
        if (ui.context.editMode) preview(ui, tr('监测数据质量与设备在线信号', 'Monitors quality and device status'));
        else register(c, ui, poll);
    }
    function initLog(c, ui) {
        ui.body.find('.op-content').html(`<div class="op-controls">${input('query', tr('搜索用户、操作或结果', 'Search user, action or result'))}${button('refresh', tr('查询', 'Query'))}</div><div class="op-results"></div>`);
        const poll = async () => {
            const rows = await api(ui.context, 'Log', {query: ui.body.find('[data-field=query]').val() || ''});
            ui.body.find('.op-results').html(rows.length ? table([tr('时间', 'Time'), tr('用户', 'User'), tr('操作', 'Action'), tr('通道', 'Channel'), tr('原值 → 目标值', 'Before → target'), tr('结果', 'Result')], rows.slice(0, clamp(c.properties.maxRows, 1, 200)).map(r => cells([new Date(r.time).toLocaleString(), r.user, r.action, r.channel || '—', (r.before ?? '—') + ' → ' + (r.value ?? '—'), r.result]))) : `<p>${tr('暂无匹配的操作记录', 'No matching operations')}</p>`);
        };
        bindActions(c, ui, {refresh: poll});
        if (ui.context.editMode) preview(ui, tr('查询真实命令、配方和计划的执行结果', 'Reads command, recipe and schedule outcomes'));
        else register(c, ui, poll);
    }
    function initInterlocks(c, ui) {
        const p = c.properties, names = String(p.labels).split(/[,，]/);
        ui.body.find('.op-content').html(`<div class="op-health" role="status"></div><div class="op-results"></div><p class="op-note">${tr('最终许可取自控制器通道（1=允许）；条件匹配仅用于解释闭锁原因。', 'Final permission comes from the controller (1=allowed); conditions explain blocking reasons.')}</p>`);
        const unknown = () => ui.body.find('.op-health').attr('data-state', 'unknown').text(tr('许可未知', 'Permission unknown'));
        unknown();
        const poll = async () => {
            try {
                const data = await api(ui.context, 'Interlocks', {channels: p.channels, expectedValues: p.expectedValues, permitChannel: p.permitChannel});
                ui.body.find('.op-health').attr('data-state', data.permitted === null ? 'unknown' : data.permitted ? 'ok' : 'blocked').text(data.permitted === null ? tr('最终许可未知 · 请绑定有效的控制器信号', 'Final permission unknown · bind a valid controller signal') : data.permitted ? tr('控制器许可：允许', 'Controller permission: allowed') : tr('控制器许可：闭锁', 'Controller permission: blocked'));
                ui.body.find('.op-results').html(data.rows.length ? table([tr('条件', 'Condition'), tr('当前值', 'Value'), tr('期望', 'Expected'), tr('状态', 'State')], data.rows.map((r, i) => `<tr><td>${esc(names[i] || r.name)}</td><td>${esc(r.matched === null ? '—' : r.value)}</td><td>${esc(r.expected)}</td><td><span class="op-state" data-state="${r.matched === null ? 'unknown' : r.matched ? 'ok' : 'blocked'}">${esc(r.matched === null ? tr('数据无效', 'Invalid data') : r.matched ? tr('匹配', 'Matched') : tr('未满足', 'Not met'))}</span></td></tr>`)) : `<p>${tr('尚未绑定条件通道', 'No condition channels bound')}</p>`);
                message(ui, tr('读取时间：', 'Read at: ') + new Date().toLocaleTimeString());
            } catch (error) {unknown(); ui.body.find('.op-results').empty(); throw error;}
        };
        if (ui.context.editMode) preview(ui, tr('显示真实联锁条件，不在浏览器内生成最终许可', 'Reads interlock conditions; final permission is never computed in the browser'));
        else register(c, ui, poll);
    }
    function faultDefinition(text, code) {
        const definitions = JSON.parse(text || '{}');
        if (!definitions || Array.isArray(definitions) || typeof definitions !== 'object') throw new Error(tr('故障说明必须是 JSON 对象', 'Fault definitions must be a JSON object'));
        const entry = definitions[String(code)];
        if (!entry || typeof entry !== 'object' || typeof entry.description !== 'string' || typeof entry.action !== 'string') return null;
        return {description: entry.description, action: entry.action};
    }
    function initDiagnostics(c, ui) {
        const p = c.properties; ui.kind = 'FaultDiagnostics'; ui.resetAllowed = false;
        ui.body.find('.op-content').html(`<div class="op-health" role="status"></div><div class="op-results"></div><div class="op-controls">${button('refresh', tr('刷新诊断', 'Refresh diagnosis'))}${button('reset', tr('申请故障复位', 'Request fault reset'))}</div><p class="op-note">${tr('复位绑定及条件由服务端策略指定；命令回读不代表故障已消除。', 'Reset bindings and conditions come from a server policy; command readback does not mean the fault is cleared.')}</p>`);
        const unknown = () => {ui.resetAllowed = false; refreshControls(c, ui, ui.kind); ui.body.find('.op-health').attr('data-state', 'unknown').text(tr('故障状态未知', 'Fault state unknown'));};
        unknown();
        const poll = async () => {
            try {
                const data = await api(ui.context, 'Diagnostics', {channel: p.faultChannel, policyId: p.policyId});
                ui.resetAllowed = data.canReset === true; refreshControls(c, ui, ui.kind);
                const state = !data.bound || !data.valid ? 'unknown' : data.code === 0 ? 'ok' : 'blocked';
                ui.body.find('.op-health').attr('data-state', state).text(!data.bound ? tr('尚未绑定设备故障代码通道', 'Device fault code channel not bound') : !data.valid ? tr('故障代码无效或数据质量异常', 'Invalid fault code or data quality') : data.code === 0 ? tr('无故障 · 代码 0', 'No fault · code 0') : tr('故障代码：', 'Fault code: ') + data.code);
                const definition = data.valid && data.code !== 0 ? faultDefinition(p.faultDefinitions, data.code) : null;
                ui.body.find('.op-results').html(data.valid && data.code !== 0 ? `<dl class="op-diagnosis"><dt>${tr('故障说明', 'Description')}</dt><dd>${esc(definition?.description || tr('未配置此故障代码，请查询设备手册', 'Unknown code; consult the device manual'))}</dd><dt>${tr('处理建议', 'Suggested action')}</dt><dd>${esc(definition?.action || tr('检查设备及控制器的诊断记录', 'Inspect device and controller diagnostics'))}</dd></dl>` : '');
                message(ui, data.canReset ? tr('复位条件满足；发送前服务端会重新校验', 'Reset conditions met; server rechecks before dispatch') : data.policyConfigured ? tr('当前不允许复位：请检查权限、故障代码和联锁条件', 'Reset unavailable: check permission, fault code and conditions') : tr('未配置服务端复位策略，复位不可用', 'No server reset policy; reset unavailable'));
            } catch (error) {unknown(); ui.body.find('.op-results').empty(); throw error;}
        };
        bindActions(c, ui, {refresh: poll, reset: async () => {
            if (!canControl(c, ui) || !ui.resetAllowed) throw new Error(tr('当前不允许复位', 'Reset unavailable'));
            if (!confirm(tr('向设备申请故障复位？请确认已处理故障原因。', 'Request a device fault reset? Confirm the cause has been addressed.'))) return;
            ui.resetAllowed = false;
            const result = await api(ui.context, 'ResetFault', {}, {id: p.policyId});
            await poll();
            message(ui, result.confirmed ? tr('复位命令回读已确认；请检查最新故障状态', 'Reset command readback confirmed; inspect the latest fault state') : result.rows.map(r => r.result).join('; '), !result.confirmed);
        }});
        if (ui.context.editMode) preview(ui, tr('故障代码映射与服务端授权复位', 'Fault code descriptions and server-authorized reset'));
        else register(c, ui, poll);
    }
    const csvCell = value => '"' + String(typeof value === 'string' && /^[=+\-@\t\r]/.test(value) ? "'" + value : value ?? '').replaceAll('"', '""') + '"';
    function initAlarmStatistics(c, ui) {
        const p = c.properties, end = new Date(), start = new Date(end - clamp(p.historyMinutes, 1, 10080) * 60000);
        ui.body.find('.op-content').html(`<div class="op-controls">${input('start', tr('开始时间', 'Start'), 'datetime-local', localTime(start))}${input('end', tr('结束时间', 'End'), 'datetime-local', localTime(end))}<label>${tr('排序', 'Sort')}<select data-field="sort"><option value="occurrences">${tr('发生次数', 'Occurrences')}</option><option value="alarmSeconds">${tr('持续时间', 'Duration')}</option><option value="chatter">${tr('频繁报警', 'Chatter')}</option></select></label>${button('query', tr('统计', 'Query'))}${button('export', tr('导出 CSV', 'Export CSV'))}</div><div class="op-summary"></div><div class="op-results"></div><p class="op-note">${tr('同一通道连续越限算一次，等级变化单列；未知数据时段不计持续时间。频繁报警：距上次发生 ≤60 秒。', 'Continuous alarm levels count as one occurrence; level changes are separate. Unknown intervals are excluded. Chatter: onset within 60 seconds of the previous onset.')}</p>`);
        let result = null, generation = 0;
        const render = () => {
            if (!result) return;
            const sort = ui.body.find('[data-field=sort]').val(), seconds = (new Date(result.to) - new Date(result.from)) / 1000;
            const rows = [...result.rows].sort((a, b) => (b[sort] ?? -1) - (a[sort] ?? -1) || a.channel - b.channel);
            const duration = v => v === null ? '—' : (v / 60).toFixed(1) + tr(' 分', ' min');
            ui.body.find('.op-summary').text(tr('报警发生 ', 'Alarm onsets ') + result.rows.reduce((n, r) => n + r.occurrences, 0) + tr(' 次 · 涉及 ', ' · channels ') + result.rows.filter(r => r.occurrences || r.alarmSeconds > 0).length + (result.truncated ? tr(' · 事件达到上限，次数仅为下界，持续时间不可用', ' · event limit reached; counts are lower bounds and durations unavailable') : ''));
            ui.body.find('.op-results').html(rows.length ? table([tr('通道', 'Channel'), tr('次数', 'Count'), tr('持续时间', 'Duration'), tr('恢复', 'Recoveries'), tr('等级变化', 'Level changes'), tr('频繁报警', 'Chatter'), tr('状态覆盖', 'Coverage'), tr('期末状态', 'End state')], rows.slice(0, clamp(p.maxRows, 1, 200)).map(r => cells([r.name, (result.truncated ? '≥' : '') + r.occurrences, duration(r.alarmSeconds), r.recoveries, r.levelChanges, r.chatter, r.knownSeconds === null ? '—' : (r.knownSeconds / seconds * 100).toFixed(1) + '%', r.activeAtEnd === null ? tr('未知', 'Unknown') : r.activeAtEnd ? tr('未恢复', 'Active') : tr('正常', 'Normal')]))) : `<p>${tr('没有可统计的通道', 'No channels to summarize')}</p>`);
            ui.body.find('[data-op=export]').prop('disabled', !rows.length);
        };
        const query = async () => {
            const ticket = ++generation; result = null; ui.body.find('[data-op=export]').prop('disabled', true); ui.body.find('.op-results, .op-summary').empty();
            const from = new Date(ui.body.find('[data-field=start]').val()), to = new Date(ui.body.find('[data-field=end]').val());
            if (!Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime()) || to <= from || to - from > 7 * 86400000) throw new Error(tr('请选择有效时间范围，最长 7 天', 'Choose a valid range up to 7 days'));
            message(ui, tr('正在统计真实归档事件…', 'Summarizing archived events…'));
            const data = await api(ui.context, 'AlarmStats', {channels: p.channels, archiveBit: p.archiveBit, start: from.toISOString(), end: to.toISOString()});
            if (ticket !== generation) return;
            result = data; render(); message(ui, tr('持续时间基于归档状态；状态覆盖不足的部分未知，查询前最多回溯 7 天寻找起始状态。', 'Duration uses archived states; uncovered intervals are unknown. Initial-state lookup is limited to the previous 7 days.'), data.truncated);
        };
        bindActions(c, ui, {query, export: async () => {
            if (!result) return;
            const rows = [['StartUTC', 'EndUTC', 'Channel', 'Name', 'Occurrences', 'AlarmSeconds', 'Recoveries', 'LevelChanges', 'Chatter', 'KnownSeconds', 'ActiveAtEnd', 'Truncated'], ...result.rows.map(r => [result.from, result.to, r.channel, r.name, r.occurrences, r.alarmSeconds, r.recoveries, r.levelChanges, r.chatter, r.knownSeconds, r.activeAtEnd, result.truncated])];
            const url = URL.createObjectURL(new Blob(['\ufeff' + rows.map(row => row.map(csvCell).join(',')).join('\r\n')], {type: 'text/csv;charset=utf-8'}));
            const link = document.createElement('a'); link.href = url; link.download = 'alarm-statistics.csv'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
        }});
        ui.body.find('[data-op=export]').prop('disabled', true);
        ui.body.find('[data-field=sort]').on('change', render);
        ui.body.find('input').on('input change', () => {generation++; result = null; ui.body.find('[data-op=export]').prop('disabled', true); ui.body.find('.op-results, .op-summary').empty(); message(ui, tr('时间范围已变更，请重新统计', 'Time range changed; query again'));});
        if (ui.context.editMode) preview(ui, tr('按真实事件统计，显示状态覆盖率并支持导出', 'Counts real events, reports coverage and exports CSV'));
        else void query().catch(error => message(ui, error.message, true));
    }
    function parameterRows(ui, channels, parameters) {
        const choices = channels.filter(c => c.writable || parameters.some(p => p.channel === c.number));
        ui.body.find('.op-parameters').html(table([tr('输出通道', 'Output'), tr('反馈通道', 'Feedback'), tr('值', 'Value'), tr('最小', 'Min'), tr('最大', 'Max'), tr('步长', 'Step'), tr('容差', 'Tolerance')], parameters.map(p => `<tr class="op-param"><td><select data-param="channel">${choices.map(c => `<option value="${c.number}" ${c.number === p.channel ? 'selected' : ''}>${esc(c.number + ' ' + c.name)}</option>`).join('')}</select></td>${['feedbackChannel', 'value', 'minimum', 'maximum', 'step', 'tolerance'].map(key => `<td><input type="number" aria-label="${esc(key)}" data-param="${key}" value="${esc(p[key])}" step="any"></td>`).join('')}</tr>`)));
    }
    function parameters(ui) {
        return [...ui.body.find('.op-param')].map(row => {
            const result = {}; row.querySelectorAll('[data-param]').forEach(input => {if (!input.value.trim()) throw new Error(tr('参数不能为空', 'Parameter is required')); result[input.dataset.param] = Number(input.value);}); return result;
        });
    }
    function initRecipe(c, ui) {
        ui.body.find('.op-content').html(`<div class="op-controls"><select data-field="recipe" aria-label="${tr('已保存配方', 'Saved recipe')}"></select>${button('load', tr('加载', 'Load'))}${button('new', tr('新建', 'New'))}${button('run', tr('执行所选配方', 'Run selected recipe'))}</div>${input('name', tr('配方名称', 'Recipe name'))}<div class="op-parameters"></div><div class="op-controls">${button('add', tr('添加参数', 'Add parameter'))}${button('remove', tr('移除末行', 'Remove last row'))}${button('save', tr('保存配方', 'Save recipe'))}${button('delete', tr('删除配方', 'Delete recipe'))}</div><div class="op-results"></div>`);
        let channels = [], recipes = [], editingId = '', admin = false;
        const refresh = async selected => {
            recipes = (await api(ui.context, 'Recipes')).filter(recipe => !ids(c.properties.channels).length || recipe.parameters.every(p => ids(c.properties.channels).includes(p.channel)));
            ui.body.find('[data-field=recipe]').html(recipes.map(r => `<option value="${esc(r.id)}">${esc(r.name)}</option>`).join(''));
            if (selected) ui.body.find('[data-field=recipe]').val(selected);
        };
        const blank = () => {editingId = ''; ui.body.find('[data-field=name]').val(''); const allowed = ids(c.properties.channels); const first = channels.find(c => c.writable && (!allowed.length || allowed.includes(c.number))); parameterRows(ui, channels, first ? [{channel: first.number, feedbackChannel: first.number, value: 0, minimum: 0, maximum: 100, step: 1, tolerance: 0.01}] : []);};
        const show = () => { const recipe = recipes.find(r => r.id === ui.body.find('[data-field=recipe]').val()); if (!recipe) throw new Error(tr('请选择配方', 'Select a recipe')); editingId = recipe.id; ui.body.find('[data-field=name]').val(recipe.name); parameterRows(ui, channels, recipe.parameters); ui.body.find('.op-parameters input, .op-parameters select').prop('disabled', !admin); };
        bindActions(c, ui, {
            load: async () => show(), new: async () => {if (!admin) throw new Error(tr('仅管理员可编辑配方', 'Only administrators edit recipes')); blank();},
            add: async () => {if (!admin) return; const rows = parameters(ui); if (rows.length >= 8) throw new Error(tr('最多 8 个参数', 'Up to 8 parameters')); const channel = channels.find(c => c.writable && !rows.some(p => p.channel === c.number)); if (!channel) return; rows.push({channel: channel.number, feedbackChannel: channel.number, value: 0, minimum: 0, maximum: 100, step: 1, tolerance: 0.01}); parameterRows(ui, channels, rows);},
            remove: async () => {if (admin) parameterRows(ui, channels, parameters(ui).slice(0, -1));},
            save: async () => {if (!admin) throw new Error(tr('仅管理员可编辑配方', 'Only administrators edit recipes')); const r = await api(ui.context, 'SaveRecipe', {}, {id: editingId, name: ui.body.find('[data-field=name]').val(), parameters: parameters(ui)}); await refresh(r.id); editingId = r.id; message(ui, tr('配方已保存到服务端', 'Recipe saved on server'));},
            delete: async () => {if (!admin) return; const id = ui.body.find('[data-field=recipe]').val(); if (!id || !confirm(tr('删除所选配方？', 'Delete selected recipe?'))) return; await api(ui.context, 'DeleteRecipe', {}, {id}); await refresh(); blank();},
            run: async () => {
                if (!canControl(c, ui)) throw new Error(tr('当前不可控制', 'Control unavailable'));
                const id = ui.body.find('[data-field=recipe]').val(), recipe = recipes.find(r => r.id === id);
                if (!recipe || !confirm(tr('下发服务端保存的配方：', 'Send the saved server recipe: ') + recipe.name + '\n' + recipe.parameters.map(p => `${p.channel} → ${p.value}`).join('\n'))) return;
                message(ui, tr('正在执行，逐项等待反馈…', 'Executing; awaiting feedback for each parameter…'));
                const result = await api(ui.context, 'RunRecipe', {}, {id});
                ui.body.find('.op-results').html(table([tr('通道', 'Channel'), tr('目标', 'Target'), tr('结果', 'Result')], result.rows.map(r => cells([r.channel, r.value, r.result]))));
                message(ui, result.completed ? tr('全部回读已确认', 'All readbacks confirmed') : tr('部分完成，后续参数已停止；请检查逐项结果', 'Partially completed; remaining parameters stopped; review results'), !result.completed);
            }
        });
        if (ui.context.editMode) {preview(ui, tr('参数组保存于服务端，执行前校验全部参数', 'Server-stored recipes validate all parameters before execution')); return;}
        (async () => {
            const catalog = await api(ui.context, 'Channels'); channels = catalog.channels.filter(channel => !ids(c.properties.channels).length || ids(c.properties.channels).includes(channel.number)); admin = catalog.admin;
            await refresh(); blank();
            ui.body.find('[data-op=new], [data-op=save], [data-op=delete], [data-op=add], [data-op=remove], [data-field=name], .op-parameters input, .op-parameters select').prop('disabled', !admin);
            ui.body.find('[data-op=run]').prop('disabled', !canControl(c, ui));
        })().catch(error => message(ui, error.message, true));
    }
    function initSchedule(c, ui) {
        ui.body.find('.op-content').html(`<div class="op-controls"><select data-field="schedule" aria-label="${tr('已保存计划', 'Saved schedule')}"></select>${button('load', tr('加载', 'Load'))}${button('new', tr('新建', 'New'))}</div><div class="op-controls">${input('name', tr('计划名称', 'Schedule name'))}${input('time', tr('执行时间（本地）', 'Run at (local time)'), 'datetime-local', localTime(new Date(Date.now() + 3600000)))}</div><div class="op-controls">${input('days', tr('重复星期（0=周日，留空单次）', 'Repeat days (0=Sun; blank=once)'), 'text', '')}<label><input data-field="enabled" type="checkbox">${tr('启用计划', 'Enable schedule')}</label></div><div class="op-parameters"></div><div class="op-controls">${button('save', tr('保存计划', 'Save schedule'))}${button('delete', tr('删除计划', 'Delete schedule'))}${button('refresh', tr('刷新状态', 'Refresh status'))}</div><div class="op-results"></div>`);
        let channels = [], schedules = [], editingId = '', admin = false;
        const refresh = async selected => {
            schedules = (await api(ui.context, 'Schedules')).filter(schedule => !ids(c.properties.channels).length || ids(c.properties.channels).includes(schedule.command.channel)); const select = ui.body.find('[data-field=schedule]'); const old = selected || select.val();
            select.html(schedules.map(s => `<option value="${esc(s.id)}">${esc(s.name)}</option>`).join('')); if (old) select.val(old);
            ui.body.find('.op-results').html(schedules.length ? table([tr('计划', 'Schedule'), tr('下次执行', 'Next run'), tr('启用', 'Enabled'), tr('结果', 'Result')], schedules.map(s => cells([s.name, new Date(s.nextRunUtc).toLocaleString(), s.enabled ? tr('是', 'Yes') : tr('否', 'No'), s.lastResult]))) : `<p>${tr('暂无计划；关闭浏览器不影响服务端执行', 'No schedules; execution is independent of the browser')}</p>`);
        };
        const blank = () => {editingId = ''; ui.body.find('[data-field=name]').val(''); ui.body.find('[data-field=enabled]').prop('checked', false); ui.body.find('[data-field=days]').val(''); const channel = channels.find(c => c.writable); parameterRows(ui, channels, channel ? [{channel: channel.number, feedbackChannel: channel.number, value: 0, minimum: 0, maximum: 1, step: 1, tolerance: 0.01}] : []);};
        bindActions(c, ui, {
            refresh: () => refresh(), new: async () => {if (admin) blank();},
            load: async () => {const schedule = schedules.find(s => s.id === ui.body.find('[data-field=schedule]').val()); if (!schedule) return; editingId = schedule.id; ui.body.find('[data-field=name]').val(schedule.name); ui.body.find('[data-field=time]').val(localTime(schedule.nextRunUtc)); ui.body.find('[data-field=days]').val(schedule.repeatDays); ui.body.find('[data-field=enabled]').prop('checked', schedule.enabled); parameterRows(ui, channels, [schedule.command]); ui.body.find('.op-parameters input, .op-parameters select').prop('disabled', !admin);},
            save: async () => {
                if (!admin) throw new Error(tr('仅管理员可管理计划', 'Only administrators manage schedules'));
                const date = new Date(ui.body.find('[data-field=time]').val()); if (!Number.isFinite(date.getTime())) throw new Error(tr('请选择执行时间', 'Choose run time'));
                const command = parameters(ui)[0]; if (!command) throw new Error(tr('请选择输出通道', 'Choose an output channel'));
                const enabled = ui.body.find('[data-field=enabled]').prop('checked');
                if (enabled && !confirm(tr('启用服务端计划，关闭浏览器后仍会执行：', 'Enable server schedule; it runs even with the browser closed: ') + `${command.channel} → ${command.value}`)) return;
                const result = await api(ui.context, 'SaveSchedule', {}, {id: editingId, name: ui.body.find('[data-field=name]').val(), command, nextRunUtc: date.toISOString(), repeatDays: ui.body.find('[data-field=days]').val(), timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone, enabled}); editingId = result.id; await refresh(result.id); message(ui, tr('计划已保存；错过超过一分钟的执行会跳过，不自动补发', 'Saved; occurrences missed by over a minute are skipped, never replayed'));
            },
            delete: async () => {if (!admin) return; const id = ui.body.find('[data-field=schedule]').val(); if (!id || !confirm(tr('删除所选计划？', 'Delete selected schedule?'))) return; await api(ui.context, 'DeleteSchedule', {}, {id}); await refresh(); blank();}
        });
        if (ui.context.editMode) {preview(ui, tr('计划保存在服务端，默认不启用', 'Server schedules are disabled by default')); return;}
        (async () => {const data = await api(ui.context, 'Channels'); channels = data.channels.filter(channel => !ids(c.properties.channels).length || ids(c.properties.channels).includes(channel.number)); admin = data.admin; blank(); await refresh(); ui.body.find('input, [data-op=new], [data-op=save], [data-op=delete], .op-parameters select').prop('disabled', !admin); register(c, ui, () => refresh());})().catch(error => message(ui, error.message, true));
    }

    function renderAcknowledgement(target, c, context) {
        target.closest('.industrial').addClass('operations');
        let ui = c.customData?.acknowledgement;
        if (ui) {ui.context = context; ui.body.find('.op-heading').text(c.properties.label); return;}
        target.html(`<div class="op-body"><div class="op-heading">${esc(c.properties.label)}</div><div class="op-content"><select data-field="event" aria-label="${tr('待确认事件', 'Unacknowledged event')}"></select>${button('ack', tr('查看并确认', 'Review and acknowledge'))}</div><div class="op-message" role="status"></div></div>`);
        ui = {c, body: target.find('.op-body'), context, busy: false}; c.customData ??= {}; c.customData.acknowledgement = ui;
        const refresh = async () => {
            const result = await S.request(context, 'GetLastAvailableEvents', {archiveBit: c.properties.archiveBit || 4, period: 2, limit: 500, filterID: 0});
            const channels = ids(c.properties.channels);
            const events = (result.records || []).filter(r => r.e.ackRequired && !r.e.ack && (!channels.length || channels.includes(r.e.cnlNum)));
            const select = ui.body.find('[data-field=event]'), selected = select.val();
            select.html(events.map(r => `<option value="${esc(r.id)}">${esc((r.ef?.time || '') + ' ' + (r.ef?.cnl || r.e.cnlNum) + ' ' + (r.ef?.descr || r.e.text))}</option>`).join(''));
            if (events.some(r => r.id === selected)) select.val(selected);
            ui.body.find('[data-op=ack]').prop('disabled', !events.length || !context.controlRight);
            message(ui, tr('最近两天最多 500 条事件中待确认：', 'Unacknowledged among up to 500 recent events: ') + events.length + ' · ' + tr('确认不代表恢复', 'Acknowledgement does not clear conditions'));
        };
        ui.refresh = refresh;
        bindActions(c, ui, {ack: () => showAck(ui, ui.body.find('[data-field=event]').val(), c.properties.archiveBit || 4)});
        if (context.editMode) preview(ui, tr('选择真实事件并在原生窗口确认，不发送普通命令', 'Selects actual archived events for native acknowledgement'));
        else register(c, ui, refresh);
    }

    class OperationsRenderer extends M.RegularComponentRenderer {
        constructor(spec) {super(); this.spec = spec;}
        _completeDom(elem) {elem.append('<div class="op-body"></div>');}
        _setClasses(elem, c, context) {super._setClasses(elem, c, context); elem.addClass('industrial operations').attr('data-operation-type', this.spec.key);}
        _setProps(elem, c, context) {
            super._setProps(elem, c, context);
            M.IndustrialRoleFilter?.(elem, c, context);
            c.customData ??= {};
            const signature = JSON.stringify(c.properties);
            let ui = c.customData.operations;
            if (ui && ui.signature === signature) {ui.context = context; refreshControls(c, ui, this.spec.key); return;}
            const body = elem.children('.op-body');
            // Preserve nested component DOMs while editing the container's title or tabs.
            const children = containers.has(this.spec.key) ? body.find('.op-content > .comp').detach() : $();
            body.html(`<div class="op-heading">${esc(c.properties.label)}</div><div class="op-content"></div><div class="op-message" role="status" aria-live="polite"></div>`);
            body.find('.op-content').append(children);
            ui = {body, context, c, signature, busy: false}; c.customData.operations = ui;
            const key = this.spec.key;
            if (key === 'NavBar' || key === 'NavButton' || key === 'BackButton') {body.addClass('op-navigation'); void nav(c, ui, this.spec).catch(error => message(ui, error.message, true));}
            else if (key === 'DevicePanel' || key === 'PidPanel') initDevice(c, ui, key === 'PidPanel');
            else if (key === 'ActiveAlarmTable') initAlarms(c, ui);
            else if (key === 'HistoryQuery') initHistory(c, ui);
            else if (key === 'CommStatus') initCommunication(c, ui);
            else if (key === 'OperationLog') initLog(c, ui);
            else if (key === 'Recipe') initRecipe(c, ui);
            else if (key === 'Schedule') initSchedule(c, ui);
            else if (key === 'InterlockPanel') initInterlocks(c, ui);
            else if (key === 'FaultDiagnostics') initDiagnostics(c, ui);
            else if (key === 'AlarmStatistics') initAlarmStatistics(c, ui);
            else if (key === 'Tabs') {
                const names = String(c.properties.tabs).split(/[,，]/).filter(Boolean).slice(0, 12);
                body.find('.op-heading').html(names.map((name, i) => `<button type="button" role="tab" data-tab="${i + 1}" aria-selected="${i === 0}">${esc(name)}</button>`).join(''));
                body.find('[data-tab]').on('click', event => {event.stopPropagation(); if (context.editMode) return; const selected = Number(event.currentTarget.dataset.tab); ui.tab = selected; this.applyTabs(c, ui);});
                ui.tab = 1; this.applyTabs(c, ui);
                if (context.editMode) message(ui, tr('子组件的 CSS 类使用 tab-1、tab-2…，编辑时显示全部', 'Use tab-1, tab-2… on child CSS classes; editor shows all'));
            } else if (key === 'CollapsiblePanel') {
                const toggle = $('<button type="button" class="op-collapse"></button>').text(c.properties.label).attr('aria-expanded', String(!c.properties.collapsed));
                body.find('.op-heading').empty().append(toggle);
                toggle.on('click', event => {event.stopPropagation(); if (context.editMode) return; const expanded = toggle.attr('aria-expanded') !== 'true'; toggle.attr('aria-expanded', String(expanded)); body.find('.op-content').toggle(expanded);});
                if (!context.editMode) body.find('.op-content').toggle(!c.properties.collapsed);
            }
        }
        applyTabs(c, ui) {
            if (ui.context.editMode) return;
            ui.body.find('[data-tab]').attr('aria-selected', 'false').filter(`[data-tab=${ui.tab}]`).attr('aria-selected', 'true');
            for (const child of c.children || []) {const match = String(child.properties.cssClass || '').match(/(?:^|\s)tab-(\d+)(?:\s|$)/); child.dom?.toggle(!match || Number(match[1]) === ui.tab);}
        }
        appendChild(parent, child) {parent.dom?.find('> .op-body > .op-content').append(child.dom); if (this.spec.key === 'Tabs') this.applyTabs(parent, parent.customData.operations);}
    }
    for (const spec of catalog) {
        M.FactorySet.componentFactories.set(spec.type, new OperationsFactory(spec));
        M.RendererSet.componentRenderers.set(spec.type, new OperationsRenderer(spec));
        const descriptor = new M.RegularComponentDescriptor();
        for (const key of Object.keys(spec.defaults)) {
            const f = fields[key]; descriptor.add(new M.PropertyDescriptor({name: key, displayName: tr(f.cn, f.en), type: f.type, category: f.category}));
        }
        M.DescriptorSet.componentDescriptors.set(spec.type, descriptor);
    }
    M.IndustrialCatalog.push(...catalog);
    M.Operations = {catalog, api, ids, good, showAck, renderAcknowledgement, faultDefinition, csvCell};
})();
