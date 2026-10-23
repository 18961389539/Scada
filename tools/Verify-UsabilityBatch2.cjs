// Verifies the second usability batch: kiosk/fullscreen, file actions, CSV export, editor help.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const BASE = 'http://127.0.0.1:5000';
const VIEWS_DIR = 'D:/projects/开发项目/scada/.local/HelloWorld/Views/HelloWorld';
const TRASH_DIR = path.join(path.dirname(VIEWS_DIR), '.trash');
const SHOT_DIR = 'D:/projects/开发项目/scada/.local/verification';
const OVERVIEW = path.join(VIEWS_DIR, 'Overview.mim');

const results = [];
const created = [];

function check(name, ok, detail) {
    results.push({ name, ok, detail });
    console.log((ok ? 'PASS' : 'FAIL') + ' | ' + name + ' | ' + detail);
}

// Removes leftovers from an interrupted run so the assertions start from a known state.
function purgeTestFiles() {
    for (const name of ['ZzRenameTest.mim', 'ZzRenamed.mim', 'ZzDeleteTest.mim']) {
        try { fs.rmSync(path.join(VIEWS_DIR, name), { force: true }); } catch { /* Best effort. */ }
    }

    if (fs.existsSync(TRASH_DIR)) {
        for (const name of fs.readdirSync(TRASH_DIR)) {
            if (name.includes('Zz')) {
                try { fs.rmSync(path.join(TRASH_DIR, name), { force: true }); } catch { /* Best effort. */ }
            }
        }
    }
}

function cleanup() {
    for (const file of created) {
        try { fs.rmSync(file, { recursive: true, force: true }); } catch { /* Best effort. */ }
    }

    purgeTestFiles();

    try { fs.rmdirSync(TRASH_DIR); } catch { /* Not empty or missing, fine. */ }
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
    try {
        fs.mkdirSync(SHOT_DIR, { recursive: true });
        purgeTestFiles();

        const renameSource = path.join(VIEWS_DIR, 'ZzRenameTest.mim');
        const renamedTarget = path.join(VIEWS_DIR, 'ZzRenamed.mim');
        const deleteSource = path.join(VIEWS_DIR, 'ZzDeleteTest.mim');
        fs.copyFileSync(OVERVIEW, renameSource);
        fs.copyFileSync(OVERVIEW, deleteSource);
        created.push(renameSource, renamedTarget, deleteSource);

        const browser = await chromium.launch({ channel: 'msedge', headless: true });
        const context = await browser.newContext({ viewport: { width: 1680, height: 1000 }, acceptDownloads: true });
        const page = await login(context);

        // ---------- E. kiosk + full screen ----------
        const view = await context.newPage();
        await view.goto(BASE + '/Mimic/MimicView/10?kiosk=1');
        await view.waitForTimeout(3500);

        const kiosk = await view.evaluate(() => {
            const nav = document.getElementById('runtimeNavigation');
            const toolbar = document.getElementById('divToolbar');
            return {
                bodyClass: document.body.classList.contains('mimic-kiosk'),
                navVisible: !!nav && nav.getClientRects().length > 0,
                toolbarOpacity: toolbar ? parseFloat(getComputedStyle(toolbar).opacity) : 1
            };
        });
        check('E1 kiosk 隐藏视图导航条', kiosk.bodyClass && !kiosk.navVisible,
            `body=${kiosk.bodyClass} nav=${kiosk.navVisible}`);
        check('E2 kiosk 工具栏淡化', kiosk.toolbarOpacity < 0.5, `opacity=${kiosk.toolbarOpacity}`);
        await view.screenshot({ path: SHOT_DIR + '/05-kiosk.png' });

        const hasFullscreenBtn = await view.isVisible('#btnFullscreen');
        await view.click('#btnFullscreen');
        await view.waitForTimeout(600);
        const fullscreenOn = await view.evaluate(() => !!document.fullscreenElement);
        const pressed = await view.getAttribute('#btnFullscreen', 'aria-pressed');
        check('E3 全屏按钮存在并响应', hasFullscreenBtn && (fullscreenOn || pressed === 'true'),
            `button=${hasFullscreenBtn} fullscreen=${fullscreenOn} pressed=${pressed}`);
        if (fullscreenOn) {
            await view.evaluate(() => document.exitFullscreen());
        }

        // ---------- F. file list: search, rename, delete ----------
        await page.goto(BASE + '/MimicEditor/MimicList');
        await page.waitForTimeout(1200);

        await page.fill('#txtFileSearch', 'Overview');
        await page.waitForTimeout(400);
        const visibleRows = await page.evaluate(() =>
            Array.from(document.querySelectorAll('#ulAvailableFiles > li'))
                .filter(item => item.getClientRects().length > 0).length);
        check('F1 搜索按文件名过滤', visibleRows === 1, `visible=${visibleRows}`);
        await page.fill('#txtFileSearch', '');
        await page.waitForTimeout(300);

        const renameRow = page.locator('#ulAvailableFiles > li', { hasText: 'ZzRenameTest.mim' });
        page.once('dialog', dialog => dialog.accept('ZzRenamed.mim'));
        await renameRow.locator('.dropdown > button').click();
        await renameRow.locator('button[formaction*="Rename"]').click();
        await page.waitForTimeout(1500);
        check('F2 重命名文件',
            fs.existsSync(renamedTarget) && !fs.existsSync(renameSource),
            `target=${fs.existsSync(renamedTarget)} source=${fs.existsSync(renameSource)}`);

        const deleteRow = page.locator('#ulAvailableFiles > li', { hasText: 'ZzDeleteTest.mim' });
        page.once('dialog', dialog => dialog.accept());
        await deleteRow.locator('.dropdown > button').click();
        await deleteRow.locator('button[formaction*="Delete"]').click();
        await page.waitForTimeout(1500);

        const trashed = fs.existsSync(TRASH_DIR)
            ? fs.readdirSync(TRASH_DIR).filter(name => name.includes('ZzDeleteTest'))
            : [];
        check('F3 删除移到 .trash', !fs.existsSync(deleteSource) && trashed.length >= 1,
            `gone=${!fs.existsSync(deleteSource)} trash=${trashed.join(',')}`);
        await page.screenshot({ path: SHOT_DIR + '/06-file-actions.png' });

        // ---------- G1. events table export ----------
        const eventsPage = await context.newPage();
        await eventsPage.goto(BASE + '/Main/Events');
        await eventsPage.waitForTimeout(5000);
        const eventsHasButton = await eventsPage.isVisible('#spanExportBtn');

        let eventsCsv = '';
        try {
            const eventsDownload = eventsPage.waitForEvent('download', { timeout: 8000 });
            await eventsPage.click('#spanExportBtn');
            const download = await eventsDownload;
            eventsCsv = fs.readFileSync(await download.path(), 'utf8').replace(/^\uFEFF/, '');
        } catch (error) {
            eventsCsv = '';
        }
        check('G1 事件表导出 CSV',
            eventsHasButton && eventsCsv.includes(',') && eventsCsv.includes('时间'),
            `button=${eventsHasButton} head=${eventsCsv.split(/\r?\n/)[0] || '(empty)'}`);

        // ---------- G2. CSV quoting rules (synthetic table, same exporter) ----------
        await eventsPage.evaluate(() => {
            const table = document.createElement('table');
            table.id = 'tblCsvUnit';
            table.innerHTML = '<thead><tr><th>名称</th><th>值</th></tr></thead>' +
                '<tbody><tr><td>含逗号,的值</td><td>12.5</td></tr><tr><td>带"引号"</td><td>3</td></tr></tbody>';
            document.body.appendChild(table);
        });

        let unitCsv = '';
        try {
            const unitDownload = eventsPage.waitForEvent('download', { timeout: 8000 });
            await eventsPage.evaluate(() => TableExport.exportCsv('#tblCsvUnit', 'unit'));
            unitCsv = fs.readFileSync(await (await unitDownload).path(), 'utf8').replace(/^\uFEFF/, '');
        } catch (error) {
            unitCsv = '';
        }
        check('G2 CSV 转义规则', unitCsv.includes('"含逗号,的值"') && unitCsv.includes('"带""引号"""'),
            unitCsv.split(/\r?\n/).slice(0, 3).join(' | '));

        // ---------- G3. mismatched view type must not return HTTP 500 ----------
        const tableView = await context.newPage();
        const tableResponse = await tableView.goto(BASE + '/Main/TableView/10');
        await tableView.waitForTimeout(1500);
        const mismatchText = await tableView.evaluate(() =>
            (document.getElementById('divError')?.innerText || '').trim());
        check('G3 视图类型不匹配给出提示',
            tableResponse.status() === 200 && mismatchText.includes('不匹配'),
            `status=${tableResponse.status()} text=${mismatchText.slice(0, 60)}`);

        // ---------- H. editor shortcut help ----------
        const editor = await context.newPage();
        const openUrl = BASE + '/MimicEditor/MimicOpen?fileName=' + encodeURIComponent(OVERVIEW);
        await editor.goto(openUrl);
        await editor.waitForTimeout(5000);
        await editor.click('#btnEditorHelp');
        await editor.waitForTimeout(400);

        const panel = await editor.evaluate(() => {
            const elem = document.getElementById('divEditorShortcuts');
            return elem ? { visible: elem.getClientRects().length > 0, text: elem.innerText } : null;
        });
        check('H1 编辑器快捷键帮助', !!panel && panel.visible && panel.text.includes('Ctrl + S'),
            panel ? `visible=${panel.visible} hasCtrlS=${panel.text.includes('Ctrl + S')}` : 'panel missing');
        await editor.screenshot({ path: SHOT_DIR + '/07-editor-help.png' });

        await browser.close();
    } finally {
        cleanup();
    }

    const failed = results.filter(item => !item.ok);
    console.log('\nSUMMARY ' + (results.length - failed.length) + '/' + results.length + ' passed');
    if (failed.length) {
        console.log('FAILED: ' + failed.map(item => item.name).join(', '));
        process.exit(1);
    }
})().catch(error => {
    console.error('ERROR: ' + error.message);
    cleanup();
    process.exit(2);
});
