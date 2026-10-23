// Verifies the third usability batch: view tree search, multi-select hint, auto-save draft.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const BASE = 'http://127.0.0.1:5000';
const VIEWS_DIR = 'D:/projects/开发项目/scada/.local/HelloWorld/Views/HelloWorld';
const SHOT_DIR = 'D:/projects/开发项目/scada/.local/verification';
const OVERVIEW = path.join(VIEWS_DIR, 'Overview.mim');
const TEST_FILE = path.join(VIEWS_DIR, 'ZzAutoSave.mim');
const AUTO_SAVE = path.join(VIEWS_DIR, 'ZzAutoSave.autosave.mim');
const BACKUP_DIR = path.join(VIEWS_DIR, '.backup', 'ZzAutoSave');

const results = [];

function check(name, ok, detail) {
    results.push({ name, ok, detail });
    console.log((ok ? 'PASS' : 'FAIL') + ' | ' + name + ' | ' + detail);
}

function purge() {
    for (const target of [TEST_FILE, AUTO_SAVE]) {
        try { fs.rmSync(target, { force: true }); } catch { /* Best effort. */ }
    }
    try { fs.rmSync(BACKUP_DIR, { recursive: true, force: true }); } catch { /* Best effort. */ }
}

async function login(context) {
    const page = await context.newPage();
    await page.goto(BASE + '/Login');
    await page.fill('#txtUsername', 'admin');
    await page.fill('#txtPassword', '12345');
    await Promise.all([page.waitForNavigation(), page.click('#btnLogin')]);
    return page;
}

async function setTreeSearch(page, value) {
    if (await page.isVisible('#Main_txtViewSearch')) {
        await page.fill('#Main_txtViewSearch', value);
    } else {
        await page.evaluate(text => {
            const input = document.getElementById('Main_txtViewSearch');
            if (!input) return;
            input.value = text;
            input.dispatchEvent(new Event('input', { bubbles: true }));
        }, value);
    }
    await page.waitForTimeout(400);
}

(async () => {
    try {
        purge();
        fs.mkdirSync(SHOT_DIR, { recursive: true });
        fs.copyFileSync(OVERVIEW, TEST_FILE);

        const browser = await chromium.launch({ channel: 'msedge', headless: true });
        const context = await browser.newContext({ viewport: { width: 1680, height: 1000 } });
        const page = await login(context);

        // ---------- I. view tree search ----------
        await page.goto(BASE + '/View/10');
        await page.waitForTimeout(2500);

        const total = await page.evaluate(() =>
            document.querySelectorAll('#Main_divViewExplorer .node').length);

        await setTreeSearch(page, '储罐');
        const visible = await page.evaluate(() =>
            document.querySelectorAll('#Main_divViewExplorer .node:not(.rs-node-hidden)').length);
        check('I1 视图树搜索过滤', total > 0 && visible > 0 && visible < total,
            `total=${total} visible=${visible}`);
        await page.screenshot({ path: SHOT_DIR + '/08-tree-search.png' });

        await setTreeSearch(page, '');
        const restored = await page.evaluate(() =>
            document.querySelectorAll('#Main_divViewExplorer .node:not(.rs-node-hidden)').length);
        check('I2 清空后恢复整棵树', restored === total, `restored=${restored}/${total}`);

        // ---------- J. multi-select hint ----------
        const editor = await context.newPage();
        await editor.goto(BASE + '/MimicEditor/MimicOpen?fileName=' + encodeURIComponent(OVERVIEW));
        await editor.waitForTimeout(5000);

        await editor.evaluate(() => {
            const comps = document.querySelectorAll('#divMimicWrapper .comp');
            const fire = (elem, ctrl) => {
                for (const type of ['mousedown', 'mouseup', 'click']) {
                    elem.dispatchEvent(new MouseEvent(type, {
                        bubbles: true, cancelable: true, view: window, button: 0, ctrlKey: ctrl
                    }));
                }
            };
            fire(comps[0], false);
            fire(comps[1], true);
        });
        await editor.waitForTimeout(900);

        const multi = await editor.evaluate(() => {
            const hint = document.getElementById('divMultiHint');
            return {
                hidden: hint ? hint.hidden : true,
                text: hint ? (hint.textContent || '').trim() : '',
                selected: document.querySelectorAll('.comp.selected').length
            };
        });
        check('J1 多选时提示联合编辑', !multi.hidden && multi.text.includes('2'),
            `selected=${multi.selected} hidden=${multi.hidden} text=${multi.text.slice(0, 48)}`);
        await editor.screenshot({ path: SHOT_DIR + '/09-multi-select.png' });

        // ---------- K. auto-save draft ----------
        const editor2 = await context.newPage();
        await editor2.goto(BASE + '/MimicEditor/MimicOpen?fileName=' + encodeURIComponent(TEST_FILE));
        await editor2.waitForTimeout(5000);

        const autoSaveResult = await editor2.evaluate(async () => {
            const response = await fetch(rootPath + 'Api/MimicEditor/Updater/AutoSaveMimic?key=' +
                encodeURIComponent(mimicKey), { method: 'POST' });
            const dto = await response.json();
            return { status: response.status, ok: dto.ok };
        });
        await editor2.waitForTimeout(600);
        check('K1 自动保存写入草稿',
            autoSaveResult.ok === true && fs.existsSync(AUTO_SAVE),
            `status=${autoSaveResult.status} ok=${autoSaveResult.ok} file=${fs.existsSync(AUTO_SAVE)}`);

        await editor2.reload();
        await editor2.waitForTimeout(3500);
        const banner = await editor2.evaluate(() => {
            const elem = document.getElementById('divAutoSaveBanner');
            return elem ? { visible: elem.getClientRects().length > 0, text: elem.innerText } : null;
        });
        check('K2 重新打开时提示草稿', !!banner && banner.visible && banner.text.includes('草稿'),
            banner ? banner.text.replace(/\s+/g, ' ').slice(0, 60) : 'banner missing');
        await editor2.screenshot({ path: SHOT_DIR + '/10-autosave-banner.png' });

        await Promise.all([
            editor2.waitForNavigation({ timeout: 15000 }),
            editor2.click('#divAutoSaveBanner button[formaction*="RestoreAutoSave"]')
        ]);
        await editor2.waitForTimeout(3500);

        const afterRestore = await editor2.evaluate(() => ({
            hasBanner: !!document.getElementById('divAutoSaveBanner'),
            hasMimic: !!document.querySelector('#divMimicWrapper .mimic')
        }));
        check('K3 恢复草稿并重开画面',
            !afterRestore.hasBanner && afterRestore.hasMimic && !fs.existsSync(AUTO_SAVE),
            `banner=${afterRestore.hasBanner} mimic=${afterRestore.hasMimic} draft=${fs.existsSync(AUTO_SAVE)}`);

        // Close the test mimic so the file can be removed cleanly.
        await editor2.evaluate(async () => {
            await fetch(rootPath + 'Api/MimicEditor/Updater/CloseMimic?key=' +
                encodeURIComponent(mimicKey) + '&save=false', { method: 'POST' });
        });
        await editor2.waitForTimeout(400);

        await browser.close();
    } finally {
        purge();
    }

    const failed = results.filter(item => !item.ok);
    console.log('\nSUMMARY ' + (results.length - failed.length) + '/' + results.length + ' passed');
    if (failed.length) {
        console.log('FAILED: ' + failed.map(item => item.name).join(', '));
        process.exit(1);
    }
})().catch(error => {
    console.error('ERROR: ' + error.message);
    purge();
    process.exit(2);
});
