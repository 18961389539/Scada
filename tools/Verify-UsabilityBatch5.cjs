// Verifies the view rotation on the runtime page.
const { chromium } = require('playwright-core');

const BASE = 'http://127.0.0.1:5000';
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
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const context = await browser.newContext({ viewport: { width: 1680, height: 1000 } });
    const page = await login(context);

    // N1: the button exists when the page has more than one view
    await page.goto(BASE + '/Mimic/MimicView/10');
    await page.waitForTimeout(3500);
    const visible = await page.isVisible('#btnRotate');
    check('N1 轮播按钮出现', visible === true, `visible=${visible}`);

    // N2: ?rotate=6 enables rotation and shows the interval (read before the first switch)
    await page.goto(BASE + '/Mimic/MimicView/10?rotate=6');
    await page.waitForTimeout(1600);
    const pressed = await page.getAttribute('#btnRotate', 'aria-pressed');
    const title = await page.getAttribute('#btnRotate', 'title');
    check('N2 URL 参数自动开启', pressed === 'true' && (title || '').includes('6s'),
        `pressed=${pressed} title=${title}`);

    // N4: operator interaction holds the rotation for 30 s
    await page.goto(BASE + '/Mimic/MimicView/10?rotate=6');
    await page.waitForTimeout(2000);
    const urlBeforePause = page.url();
    await page.mouse.move(240, 220);
    await page.waitForTimeout(5000);
    const urlAfterPause = page.url();
    check('N4 操作后暂停轮播',
        urlBeforePause === urlAfterPause && urlAfterPause.includes('MimicView/10'),
        `${urlBeforePause} -> ${urlAfterPause}`);

    // N3: without interaction the page advances to the next view
    await page.goto(BASE + '/Mimic/MimicView/10?rotate=3');
    await page.waitForTimeout(2000);
    const urlBefore = page.url();
    await page.waitForTimeout(4500);
    const urlAfter = page.url();
    check('N3 自动切换到下一个视图', urlAfter !== urlBefore,
        `${urlBefore} -> ${urlAfter}`);

    // N5: the button turns rotation off and remembers it
    await page.goto(BASE + '/Mimic/MimicView/10?rotate=6');
    await page.waitForTimeout(2000);
    await page.click('#btnRotate');
    await page.waitForTimeout(500);
    const pressedAfter = await page.getAttribute('#btnRotate', 'aria-pressed');
    const stored = await page.evaluate(() => localStorage.getItem('rs.mimic.rotate'));
    check('N5 关闭并记住状态', pressedAfter === 'false' && stored === '0',
        `pressed=${pressedAfter} stored=${stored}`);
    await page.screenshot({ path: SHOT_DIR + '/13-rotation.png' });

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
