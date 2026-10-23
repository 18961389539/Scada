// Verifies the fourth usability batch: event time-range query and multi-select snapping.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const BASE = 'http://127.0.0.1:5000';
const VIEWS_DIR = 'D:/projects/开发项目/scada/.local/HelloWorld/Views/HelloWorld';
const OVERVIEW = path.join(VIEWS_DIR, 'Overview.mim');
const SHOT_DIR = 'D:/projects/开发项目/scada/.local/verification';

const results = [];

function check(name, ok, detail) {
    results.push({ name, ok, detail });
    console.log((ok ? 'PASS' : 'FAIL') + ' | ' + name + ' | ' + detail);
}

async function login(context) {
    const page = await context.newPage();
    await page.goto(BASE + '/Login');
    await page.fill('#txtUsername', 'admin');
    await page.fill('#txtPassword', '12345');
    await Promise.all([page.waitForNavigation(), page.click('#btnLogin')]);
    return page;
}

(async () => {
    fs.mkdirSync(SHOT_DIR, { recursive: true });
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const context = await browser.newContext({ viewport: { width: 1680, height: 1000 } });
    await login(context);

    // ---------- L. event time range ----------
    const eventsPage = await context.newPage();
    await eventsPage.goto(BASE + '/Main/Events');
    await eventsPage.waitForTimeout(4000);

    const defaults = await eventsPage.evaluate(() => ({
        start: document.getElementById('dtRangeStart')?.value || '',
        end: document.getElementById('dtRangeEnd')?.value || '',
        live: document.getElementById('spanLiveMode')?.classList.contains('selected')
    }));
    check('L1 时间范围默认值', /^\d{4}-\d{2}-\d{2}T00:00$/.test(defaults.start) && !!defaults.end,
        `start=${defaults.start} end=${defaults.end}`);

    let rangeStatus = 0;
    let rangeBody = '';
    const rangeResponse = eventsPage.waitForResponse(
        response => response.url().includes('/Api/Main/GetEvents?'), { timeout: 15000 }).catch(() => null);

    await eventsPage.click('#spanQueryRange');
    const response = await rangeResponse;

    if (response) {
        rangeStatus = response.status();
        try { rangeBody = await response.text(); } catch { rangeBody = ''; }
    }

    await eventsPage.waitForTimeout(800);
    const rangeState = await eventsPage.evaluate(() => ({
        live: document.getElementById('spanLiveMode')?.classList.contains('selected'),
        tableHidden: document.getElementById('divTableWrapper')?.classList.contains('hidden'),
        message: (document.getElementById('divMessage')?.textContent || '').trim()
    }));

    check('L2 按范围查询发出请求', rangeStatus === 200 && rangeBody.includes('"ok":true'),
        `status=${rangeStatus} ok=${rangeBody.includes('"ok":true')}`);
    check('L3 进入范围模式（实时标记取消）', rangeState.live === false,
        `live=${rangeState.live} table=${rangeState.tableHidden ? 'hidden' : 'shown'} msg=${rangeState.message}`);
    await eventsPage.screenshot({ path: SHOT_DIR + '/11-events-range.png' });

    const liveResponse = eventsPage.waitForResponse(
        response => response.url().includes('GetLastAvailableEvents') || response.url().includes('GetLastEventsByView'),
        { timeout: 15000 }).catch(() => null);
    await eventsPage.click('#spanLiveMode');
    const live = await liveResponse;
    await eventsPage.waitForTimeout(600);

    const liveState = await eventsPage.evaluate(() =>
        document.getElementById('spanLiveMode')?.classList.contains('selected'));
    check('L4 回到实时刷新', !!live && liveState === true,
        `status=${live ? live.status() : 0} live=${liveState}`);

    // ---------- M. multi-select drag ----------
    const editor = await context.newPage();
    await editor.goto(BASE + '/MimicEditor/MimicOpen?fileName=' + encodeURIComponent(OVERVIEW));
    await editor.waitForTimeout(5000);

    const before = await editor.evaluate(() =>
        Array.from(document.querySelectorAll('#divMimicWrapper .comp')).slice(0, 2).map(elem => {
            const rect = elem.getBoundingClientRect();
            return {
                id: elem.id,
                cx: rect.left + rect.width / 2,
                cy: rect.top + rect.height / 2
            };
        }));

    // first click selects, ctrl+click adds the second one
    await editor.mouse.click(before[0].cx, before[0].cy);
    await editor.keyboard.down('Control');
    await editor.mouse.click(before[1].cx, before[1].cy);
    await editor.keyboard.up('Control');
    await editor.waitForTimeout(500);

    const selectedCount = await editor.evaluate(() => document.querySelectorAll('.comp.selected').length);

    // Identify the real selection: a click may hit a component stacked above the one we aimed at.
    const beforeSel = await editor.evaluate(() => selectedComponents.map(component => ({
        id: component.id,
        left: parseFloat(component.dom?.css('left') || '0'),
        top: parseFloat(component.dom?.css('top') || '0'),
        cx: component.dom ? component.dom[0].getBoundingClientRect().left +
            component.dom[0].getBoundingClientRect().width / 2 : 0,
        cy: component.dom ? component.dom[0].getBoundingClientRect().top +
            component.dom[0].getBoundingClientRect().height / 2 : 0
    })));

    const dragPoint = beforeSel[0];

    await editor.mouse.move(dragPoint.cx, dragPoint.cy);
    await editor.mouse.down();
    await editor.mouse.move(dragPoint.cx + 60, dragPoint.cy + 40, { steps: 8 });
    await editor.mouse.up();
    await editor.waitForTimeout(600);

    const afterSel = await editor.evaluate(() => selectedComponents.map(component => ({
        id: component.id,
        left: parseFloat(component.dom?.css('left') || '0'),
        top: parseFloat(component.dom?.css('top') || '0')
    })));

    const deltas = beforeSel.map(start => {
        const end = afterSel.find(item => item.id === start.id) || start;
        return { id: start.id, x: Math.round(end.left - start.left), y: Math.round(end.top - start.top) };
    });

    const moved = deltas.every(item => Math.abs(item.x) + Math.abs(item.y) > 0);
    const sameDelta = deltas.length >= 2 &&
        Math.abs(deltas[0].x - deltas[1].x) <= 2 && Math.abs(deltas[0].y - deltas[1].y) <= 2;

    check('M1 两个组件都被选中', selectedCount === 2, `selected=${selectedCount}`);
    check('M2 多选整体拖动且位移一致', moved && sameDelta,
        deltas.map(item => `#${item.id}=(${item.x},${item.y})`).join(' '));
    await editor.screenshot({ path: SHOT_DIR + '/12-multi-drag.png' });

    await browser.close();

    const failed = results.filter(item => !item.ok);
    console.log('\nSUMMARY ' + (results.length - failed.length) + '/' + results.length + ' passed');
    if (failed.length) {
        console.log('FAILED: ' + failed.map(item => item.name).join(', '));
        process.exit(1);
    }
})().catch(error => {
    console.error('ERROR: ' + error.message);
    process.exit(2);
});
