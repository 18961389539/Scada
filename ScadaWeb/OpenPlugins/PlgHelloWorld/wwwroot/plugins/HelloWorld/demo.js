/* HelloWorld overview reads the native API; all commands use the native, authorized form. */
(() => {
    'use strict';
    const root = document.body.dataset.root;
    const canControl = document.body.dataset.control === 'true';
    const channels = '201-205,301-305,401-406';
    const $id = id => document.getElementById(id);
    const number = value => Number(value).toLocaleString('zh-CN', { maximumFractionDigits: 2 });
    const clock = date => date.toLocaleTimeString('zh-CN', { hour12: false });
    const statusText = { 0: '无数据', 1: '正常', 3: '公式错误', 4: '数据不可靠', 11: '低低限', 12: '低限', 13: '正常', 14: '高限', 15: '高高限' };
    const alarmStatuses = new Set([11, 12, 14, 15]);
    const tempSamples = [];
    let live = false;
    let latestHistory = null;
    let historyGeneration = 0;

    async function api(action, params) {
        const response = await fetch(root + 'Api/Main/' + action + '?' + new URLSearchParams(params), {
            credentials: 'same-origin', signal: AbortSignal.timeout(10000)
        });
        if (response.redirected || response.status === 401) throw new Error('登录已失效，请重新登录。');
        if (!response.ok) throw new Error('数据请求失败（HTTP ' + response.status + '）');
        const dto = await response.json();
        if (!dto.ok) throw new Error(dto.msg || '数据服务暂不可用');
        return dto.data;
    }

    function updateControls() {
        document.querySelectorAll('[data-command]').forEach(button => {
            button.disabled = !canControl || !live;
            button.title = !canControl ? '当前账号只读，切换 operator 或 admin 可操作' : (!live ? '等待数据服务恢复' : '打开原生命令窗口');
        });
    }

    function signalConnection(ok, message = '') {
        live = ok;
        $id('connection').textContent = ok ? '实时连接' : '数据已暂停';
        $id('liveDot').classList.toggle('offline', !ok);
        $id('errorBanner').hidden = ok;
        $id('errorBanner').textContent = message;
        document.body.classList.toggle('stale', !ok);
        updateControls();
    }

    async function refreshCurrent() {
        try {
            const packet = await api('GetCurDataStep1', { cnlNums: channels, useCache: false, appendUnit: false });
            const map = new Map(packet.records.map(record => [record.d.cnlNum, record]));
            const raw = n => map.get(n)?.d;
            const val = n => raw(n)?.stat > 0 ? raw(n).val : null;
            document.querySelectorAll('[data-value]').forEach(element => {
                const record = raw(Number(element.dataset.value));
                element.textContent = record?.stat > 0 && Number.isFinite(record.val) ? number(record.val) : '—';
            });
            const level = val(201);
            const temp = val(301);
            const levelAlarm = alarmStatuses.has(raw(201)?.stat);
            const tempAlarm = alarmStatuses.has(raw(301)?.stat);
            $id('tankState').textContent = statusText[raw(201)?.stat] || '数据异常';
            $id('tempState').textContent = statusText[raw(301)?.stat] || '数据异常';
            $id('tankState').classList.toggle('alarm-text', levelAlarm);
            $id('tempState').classList.toggle('alarm-text', tempAlarm);
            $id('alarmCard').classList.toggle('alarm', levelAlarm || tempAlarm);
            $id('alarmSummary').textContent = levelAlarm || tempAlarm
                ? [levelAlarm ? '液位越限' : '', tempAlarm ? '温度越限' : ''].filter(Boolean).join(' · ')
                : '液位与温度均未越限';
            const fraction = level === null ? 0 : Math.max(0, Math.min(100, level));
            $id('tankMeter').style.width = fraction + '%';
            $id('tankMeter').classList.toggle('alarm-meter', levelAlarm);
            $id('tankFill').setAttribute('height', fraction * 1.86);
            $id('tankFill').setAttribute('y', 215 - fraction * 1.86);
            $id('tankSvgValue').textContent = level === null ? '— %' : number(level) + ' %';
            $id('flowSvgValue').textContent = val(202) === null ? '—' : number(val(202));
            $id('pressureSvg').textContent = '管道压力 ' + (val(203) === null ? '—' : number(val(203))) + ' bar';
            const pumpOn = val(204) !== null && val(204) > 0;
            const fanOn = val(304) !== null && val(304) > 0;
            $id('pumpLabel').textContent = val(204) === null ? '尚未下发命令' : (pumpOn ? '运行中' : '已停止');
            $id('pumpSvgState').textContent = 'PUMP-01 · ' + (pumpOn ? '运行中' : '待机');
            $id('fanBadge').textContent = fanOn ? '运行中' : '待机';
            $id('pumpRotor').classList.toggle('rotating', pumpOn);
            $id('fanRotor').classList.toggle('rotating', fanOn);
            $id('flowPipe').classList.toggle('flowing', pumpOn);
            $id('flowCheck').textContent = val(406) > 0 ? '泵开 · 流量正常' : (pumpOn ? '等待流量反馈' : '泵停 · 流量为零');
            if (Number.isFinite(temp) && temp !== null) {
                tempSamples.push(temp);
                if (tempSamples.length > 60) tempSamples.shift();
                const low = Math.min(...tempSamples) - 0.5, high = Math.max(...tempSamples) + 0.5;
                $id('tempSpark').setAttribute('points', tempSamples.map((v, i) => `${i * 220 / Math.max(1, tempSamples.length - 1)},${24 - (v - low) / (high - low) * 22}`).join(' '));
            }
            signalConnection(true);
            $id('updated').textContent = '最后更新 ' + clock(new Date()) + ' · 每秒刷新';
        } catch (error) {
            signalConnection(false, '实时数据获取失败，保留的数值已过期。' + error.message);
        }
    }

    function textElement(tag, text, className) {
        const element = document.createElement(tag);
        element.textContent = text;
        if (className) element.className = className;
        return element;
    }

    async function refreshEvents() {
        try {
            const packet = await api('GetLastAvailableEvents', { archiveBit: 4, period: 2, limit: 6, filterID: 0 });
            const list = $id('eventList');
            list.replaceChildren();
            if (!packet.records.length) list.append(textElement('p', '暂无事件。试着启动设备，或完成一次报警实验。', 'subtle'));
            for (const record of packet.records) {
                const row = textElement('div', '', 'event-item');
                const alarm = [11,12,14,15].includes(record.e?.cnlStat);
                row.append(textElement('span', alarm ? '!' : '·', alarm ? 'event-dot warning' : 'event-dot'));
                const body = document.createElement('div');
                body.append(textElement('strong', record.ef.cnl), textElement('p', record.ef.descr), textElement('small', record.ef.time));
                row.append(body); list.append(row);
            }
        } catch (error) {
            $id('eventList').replaceChildren(textElement('p', '事件读取失败：' + error.message, 'subtle'));
        }
    }

    async function refreshHistory() {
        const generation = ++historyGeneration;
        const cnlNum = $id('historyChannel').value;
        const period = Number($id('historyPeriod').value);
        const end = new Date(), start = new Date(end.getTime() - period * 60000);
        $id('refreshHistory').disabled = true;
        $id('historyMessage').textContent = '正在读取归档…';
        latestHistory = null;
        $id('exportCsv').disabled = true;
        try {
            const data = await api('GetHistData', { archiveBit: 1, cnlNums: cnlNum, startTime: start.toISOString(), endTime: end.toISOString(), endInclusive: true });
            if (generation !== historyGeneration) return;
            const rows = data.timestamps.map((stamp, i) => ({ time: stamp.ut, value: data.trends[0]?.[i]?.d.val, stat: data.trends[0]?.[i]?.d.stat }));
            const valid = rows.filter(r => r.stat > 0 && Number.isFinite(r.value));
            const line = $id('historyLine');
            line.setAttribute('points', '');
            $id('historyMax').textContent = '';
            $id('historyMin').textContent = '';
            if (valid.length) {
                let low = Math.min(...valid.map(r => r.value)), high = Math.max(...valid.map(r => r.value));
                const padding = Math.max((high - low) * 0.15, 0.5); low -= padding; high += padding;
                line.setAttribute('points', valid.map(r => `${40 + (new Date(r.time) - start) / (end - start) * 585},${155 - (r.value - low) / (high - low) * 140}`).join(' '));
                // A round linecap makes a single sample visible as well.
                if (valid.length === 1) { const p = line.getAttribute('points'); line.setAttribute('points', p + ' ' + p); }
                $id('historyMax').textContent = number(high);
                $id('historyMin').textContent = number(low);
                latestHistory = { cnlNum, startTime: start.toISOString(), endTime: end.toISOString() };
                $id('exportCsv').disabled = false;
            }
            $id('historyStart').textContent = clock(start);
            $id('historyEnd').textContent = clock(end);
            $id('historyMessage').textContent = valid.length ? `已读取 ${valid.length} 个有效归档点 · 每分钟采样 · 每 15 秒自动查询` : '此范围还没有有效归档。新启动的通道请等待约 1 分钟，再查询。';
        } catch (error) {
            if (generation === historyGeneration) {
                $id('historyLine').setAttribute('points', '');
                $id('historyMessage').textContent = '查询失败：' + error.message;
            }
        } finally {
            if (generation === historyGeneration) $id('refreshHistory').disabled = false;
        }
    }

    $id('exportCsv').addEventListener('click', () => {
        if (!latestHistory) return;
        const link = document.createElement('a');
        link.href = root + 'Demo?' + new URLSearchParams({ handler: 'Export', ...latestHistory });
        link.download = 'HelloWorld-channel-' + latestHistory.cnlNum + '.csv';
        document.body.append(link); link.click(); link.remove();
    });

    document.querySelectorAll('[data-chart]').forEach(button => button.addEventListener('click', () => {
        const today = new Date();
        const date = `${today.getFullYear()}-${String(today.getMonth()+1).padStart(2,'0')}-${String(today.getDate()).padStart(2,'0')}`;
        window.open(root + 'Chart/Chart?' + new URLSearchParams({ cnlNums: button.dataset.chart, startDate: date }), '_blank', 'noopener');
    }));
    document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => {
        if (!canControl || !live) return;
        const manager = ModalManager.getInstance();
        manager.showModal(root + 'Main/Command?cnlNum=' + Number(button.dataset.command), new ModalOptions({ buttons: [ModalButton.EXEC, ModalButton.CLOSE], size: ModalSize.LARGE }), () => { refreshCurrent(); refreshEvents(); });
    }));
    Object.assign(modalPhrases, { execute: '执行命令', close: '关闭' });
    for (const id of ['historyChannel','historyPeriod']) $id(id).addEventListener('change', refreshHistory);
    $id('refreshHistory').addEventListener('click', refreshHistory);
    updateControls();
    async function loop(action, delay) { await action(); setTimeout(() => loop(action, delay), delay); }
    loop(refreshCurrent, 1000); loop(refreshEvents, 5000); loop(refreshHistory, 15000);
})();
