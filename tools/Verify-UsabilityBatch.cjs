// Verifies the usability batch (property search, mimic lint, offline feedback, paused polling).
// Requires the demo instance on http://127.0.0.1:5000 and playwright-core with msedge.
const fs = require('fs');
const { chromium } = require('playwright-core');

const BASE = 'http://127.0.0.1:5000';
const EDIT_URL = BASE + '/MimicEditor/MimicEdit/7699749796150001404';
const SHOT_DIR = 'D:/projects/开发项目/scada/.local/verification';

const results = [];
function check(name, ok, detail) {
    results.push({ name, ok, detail });
    console.log((ok ? 'PASS' : 'FAIL') + ' | ' + name + ' | ' + detail);
}

const visibleLabels = page => page.evaluate(() =>
    Array.from(document.querySelectorAll('#tweakpane .tp-lblv'))
        .filter(row => row.getClientRects().length > 0)
        .map(row => (row.querySelector('.tp-lblv_l')?.textContent || '').trim())
        .filter(Boolean));

(async () => {
    fs.mkdirSync(SHOT_DIR, { recursive: true });
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const context = await browser.newContext({ viewport: { width: 1680, height: 1000 } });
    const page = await context.newPage();

    await page.goto(BASE + '/Login');
    await page.fill('#txtUsername', 'admin');
    await page.fill('#txtPassword', '12345');
    await Promise.all([page.waitForNavigation(), page.click('#btnLogin')]);

    // ---------- A. property search ----------
    await page.goto(EDIT_URL);
    await page.waitForTimeout(5000);
    await page.evaluate(() => {
        const elem = document.querySelector('.comp.industrial');
        for (const type of ['mousedown', 'mouseup', 'click']) {
            elem.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window, button: 0 }));
        }
    });
    await page.waitForTimeout(1200);
    await page.click('button[data-bs-target="#divProperties"]');
    await page.waitForTimeout(400);

    const toolsVisible = await page.isVisible('#divPropTools');
    const initial = await visibleLabels(page);

    await page.fill('#txtPropSearch', '颜色');
    await page.waitForTimeout(400);
    const filtered = await visibleLabels(page);
    const countText = await page.textContent('#spanPropCount');

    check('A1 工具条在选中组件后出现', toolsVisible, `visible=${toolsVisible}`);
    check('A2 属性行已加载', initial.length >= 20, `rows=${initial.length}`);
    check('A3 搜索"颜色"缩小结果', filtered.length > 0 && filtered.length < initial.length,
        `${initial.length} -> ${filtered.length} [${filtered.join(', ')}]`);
    check('A4 命中项正确', filtered.includes('背景颜色') && filtered.includes('前景颜色'),
        `count=${countText}`);

    await page.screenshot({ path: SHOT_DIR + '/01-prop-search.png' });

    await page.check('#chkPropModified');
    await page.waitForTimeout(400);
    const modifiedOnly = await visibleLabels(page);
    check('A5 仅已修改进一步过滤', modifiedOnly.length <= filtered.length && modifiedOnly.length > 0,
        `${filtered.length} -> ${modifiedOnly.length} [${modifiedOnly.join(', ')}]`);
    await page.screenshot({ path: SHOT_DIR + '/02-prop-modified.png' });

    await page.fill('#txtPropSearch', '');
    await page.uncheck('#chkPropModified');
    await page.waitForTimeout(400);
    const restored = await visibleLabels(page);
    check('A6 清空后恢复全部', restored.length === initial.length, `${restored.length}/${initial.length}`);

    // ---------- B. offline feedback ----------
    const view = await context.newPage();
    let abortData = false;
    await view.route('**/GetCurDataByView*', route => abortData ? route.abort() : route.continue());
    await view.goto(BASE + '/Mimic/MimicView/10');
    await view.waitForTimeout(4500);

    const onlineStatus = await view.textContent('#mimicConnectionStatus');
    check('B0 数据在线', (onlineStatus || '').includes('更新于'), onlineStatus);

    abortData = true;
    await view.waitForTimeout(3200);

    const bannerVisible = await view.isVisible('#divOfflineBanner');
    const bannerText = (await view.textContent('#divOfflineBanner')) || '';
    const bodyOffline = await view.evaluate(() => document.body.classList.contains('mimic-offline'));
    const dotRed = await view.evaluate(() => document.querySelector('#mimicConnectionStatus')?.classList.contains('text-danger'));

    check('B1 顶部横幅出现', bannerVisible && bannerText.includes('连接中断'), bannerText);
    check('B2 页面进入离线态', bodyOffline && !!dotRed, `body=${bodyOffline} dot=${dotRed}`);

    await view.evaluate(() => {
        const elem = document.querySelector('#divMimicWrapper .comp');
        elem.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
    });
    await view.waitForTimeout(500);
    const toastVisible = await view.isVisible('#divRuntimeToast');
    const toastText = (await view.textContent('#divRuntimeToast')) || '';
    check('B3 断连点击给出提示', toastVisible && toastText.includes('命令未发送'), toastText);
    await view.screenshot({ path: SHOT_DIR + '/03-offline-feedback.png' });

    // ---------- C. polling pauses while hidden ----------
    abortData = false;
    await view.waitForTimeout(3000);

    let requests = 0;
    view.on('request', request => {
        if (request.url().includes('GetCurDataByView')) requests++;
    });

    await view.waitForTimeout(2200);
    const baseline = requests;

    await view.evaluate(() => {
        Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
        document.dispatchEvent(new Event('visibilitychange'));
    });
    const atHide = requests;
    await view.waitForTimeout(3000);
    const afterHide = requests;

    check('C1 隐藏后停止轮询', afterHide - atHide <= 1, `requests during 3s hidden = ${afterHide - atHide}`);

    await view.evaluate(() => {
        Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
        document.dispatchEvent(new Event('visibilitychange'));
    });
    await view.waitForTimeout(2500);
    const afterShow = requests;

    check('C2 恢复可见后继续轮询', afterShow - afterHide >= 2, `requests after show = ${afterShow - afterHide}`);

    const resumedStatus = await view.textContent('#mimicConnectionStatus');
    const bannerGone = !(await view.isVisible('#divOfflineBanner'));
    check('C3 恢复后离线提示消失', bannerGone && (resumedStatus || '').includes('更新于'),
        `bannerGone=${bannerGone} status=${resumedStatus}`);

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
