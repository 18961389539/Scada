// Browser checks for the mimic file library. Uses only disposable view files and editor operations.
// Requires playwright-core on NODE_PATH, system Edge, and a running local HelloWorld demo.
// Run from any directory: node tools/Verify-MimicLibrary.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright-core');

const repo = path.resolve(__dirname, '..');
const views = path.join(repo, '.local/HelloWorld/Views');
const fixtureName = `__library-check-${process.pid}-${Date.now()}`;
const fixtureDir = path.join(views, fixtureName);
const base = process.env.SCADA_BASE_URL || 'http://127.0.0.1:5000';
const listPath = '/MimicEditor/MimicList';
const screenshotDir = path.join(repo, '.local/verification');
const keys = new Set();
const fixtureFiles = [];
const errors = [];
let browser;
let context;
let passed = 0;

function check(name, ok, detail = '') {
    assert.ok(ok, `${name}${detail ? ': ' + detail : ''}`);
    console.log(`PASS | ${name}${detail ? ' | ' + detail : ''}`);
    passed++;
}

function mimic(components = '') {
    return `<?xml version="1.0" encoding="utf-8"?>
<Mimic editorVersion="6.0.2.2"><Dependencies />
<Document><BackColor>#eef2f7</BackColor><Size><Width>1000</Width><Height>700</Height></Size></Document>
<Components>${components}</Components><Images /></Mimic>`;
}

function writeFixture(name, contents, modified) {
    const file = path.join(fixtureDir, name + '.mim');
    fs.writeFileSync(file, contents, 'utf8');
    fs.utimesSync(file, modified, modified);
    fixtureFiles.push({ name: path.basename(file), modified: modified.getTime() });
    return file;
}

function listUrl(query = fixtureName, sort = 'name', page = 1) {
    return base + listPath + '?' + new URLSearchParams({
        FileQuery: query, FileSort: sort, FilePage: String(page)
    });
}

async function rows(page) {
    return page.locator('#ulAvailableFiles > li').evaluateAll(items => items.map(item => ({
        path: item.querySelector('input[name="FileName"]')?.value || '',
        text: item.textContent.trim()
    })));
}

async function waitForResults(page, query, count, sort) {
    await page.waitForFunction(({ query, count, sort }) => {
        const url = new URL(location.href);
        const results = document.getElementById('fileLibraryResults');
        const rows = [...document.querySelectorAll('#ulAvailableFiles > li')];
        const words = query.toLowerCase().trim().split(/\s+/).filter(Boolean);
        return url.searchParams.get('FileQuery') === query &&
            (!sort || url.searchParams.get('FileSort') === sort) &&
            results && results.getAttribute('aria-busy') !== 'true' &&
            results.dataset.query === query && (!sort || results.dataset.sort === sort) &&
            rows.length === count && rows.every(row => {
                const text = (row.dataset.search || row.textContent).toLowerCase();
                return words.every(word => text.includes(word));
            });
    }, { query, count, sort });
}

async function search(page, query, count) {
    const response = page.waitForResponse(response => {
        const url = new URL(response.url());
        return response.request().method() === 'GET' && url.pathname === listPath &&
            url.searchParams.get('FileQuery') === query;
    });
    await page.fill('#txtFileSearch', query);
    const received = await response;
    assert.equal(received.status(), 200, 'Search must return HTTP 200');
    await received.finished();
    await waitForResults(page, query, count);
}

async function rowFor(page, name) {
    const fileRows = page.locator('#ulAvailableFiles > li');
    for (let index = 0; index < await fileRows.count(); index++) {
        const value = await fileRows.nth(index).locator('input[name="FileName"]').inputValue();
        if (value.replaceAll('\\', '/').endsWith('/' + name)) return fileRows.nth(index);
    }
    throw new Error('Fixture row not found: ' + name);
}

async function duplicate(page, sourceName, targetName, succeeds) {
    const row = await rowFor(page, sourceName);
    await row.locator('[data-bs-toggle="dropdown"]').click();
    const prompt = page.waitForEvent('dialog');
    const navigation = succeeds
        ? page.waitForURL(url => /\/MimicEditor\/MimicEdit\/\d+$/.test(url.pathname))
        : page.waitForResponse(response => response.request().method() === 'POST' &&
            response.url().includes('handler=Duplicate'));
    const click = row.locator('button[formaction*="Duplicate"]').click();
    const dialog = await prompt;
    assert.equal(dialog.type(), 'prompt', 'Duplicate must ask for the new file name');
    await dialog.accept(targetName);
    await click;
    await navigation;
    await page.waitForLoadState('domcontentloaded');
    if (succeeds) rememberEditorKey(page);
}

function rememberEditorKey(page) {
    const match = new URL(page.url()).pathname.match(/\/MimicEditor\/MimicEdit\/(\d+)$/);
    assert.ok(match, 'The file must open directly in the editor: ' + page.url());
    keys.add(match[1]);
    return match[1];
}

async function run() {
    fs.mkdirSync(fixtureDir, { recursive: false });
    fs.mkdirSync(screenshotDir, { recursive: true });
    for (let index = 0; index < 220; index++) {
        writeFixture(`Load-${String(index).padStart(3, '0')}`, mimic(),
            new Date(Date.UTC(2022, 0, 1, 0, 0, index)));
    }
    writeFixture('Oldest-Target', mimic(), new Date('1990-01-01T00:00:00Z'));
    writeFixture('Alpha-Only', mimic(), new Date('2023-01-01T00:00:00Z'));
    writeFixture('Beta-Only', mimic(), new Date('2023-01-02T00:00:00Z'));
    writeFixture('Beta-Alpha-Pump', mimic(), new Date('2023-01-03T00:00:00Z'));
    const source = writeFixture('Source', mimic('<Text><ID>1</ID><Name>Saved source</Name><Text>Saved source content</Text><Location><X>20</X><Y>20</Y></Location></Text>'),
        new Date('2023-01-04T00:00:00Z'));
    writeFixture('Nested-Issues', mimic(`<IndustrialTabs><ID>10</ID><Name>Parent group</Name><Tabs>One</Tabs>
<Components><IndustrialValve><ID>11</ID><Name>Nested valve check</Name><FeedbackChannel>2147483000</FeedbackChannel></IndustrialValve></Components>
</IndustrialTabs>`), new Date('2023-01-05T00:00:00Z'));
    writeFixture('Zero-Channels', mimic(`<Text><ID>1</ID><Name>No label property</Name><InCnlNum>0</InCnlNum></Text>
<Text><ID>2</ID><Name>Empty label and no binding</Name><InCnlNum>0</InCnlNum><Label /></Text>`),
        new Date('2023-01-06T00:00:00Z'));
    const sourceBytes = fs.readFileSync(source);
    const names = fixtureFiles.map(file => file.name).sort((a, b) =>
        a.toLowerCase() < b.toLowerCase() ? -1 : a.toLowerCase() > b.toLowerCase() ? 1 : 0);

    browser = await chromium.launch({ channel: 'msedge', headless: true });
    context = await browser.newContext({ viewport: { width: 1540, height: 1000 } });
    context.on('page', page => page.on('pageerror', error => errors.push(error.message)));
    const page = await context.newPage();
    await page.goto(base + '/Login');
    await page.fill('#txtUsername', process.env.SCADA_DEMO_USER || 'admin');
    await page.fill('#txtPassword', process.env.SCADA_DEMO_PASSWORD || '12345');
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Login')), page.click('#btnLogin')]);

    await page.goto(listUrl(fixtureName, 'modified'));
    await waitForResults(page, fixtureName, 50, 'modified');
    check('文件库搜索表单、排序与可访问状态存在',
        await page.locator('#frmFileSearch #txtFileSearch').count() === 1 &&
        await page.locator('#frmFileSearch #selFileSort').count() === 1 &&
        await page.locator('#fileLibraryStatus').getAttribute('aria-live') === 'polite' &&
        !!(await page.locator('#fileLibraryResults [role="status"]').textContent()).trim());
    const initialRows = await rows(page);
    check('超过 200 个文件时每页只显示 50 项', initialRows.length === 50 && fixtureFiles.length > 200);
    check('修改时间排序把新文件放在前面',
        path.basename(initialRows[0].path.replaceAll('\\', '/')) === 'Zero-Channels.mim' &&
        !initialRows.some(row => row.path.includes('Oldest-Target')));

    await page.evaluate(() => { window.librarySearchInput = document.getElementById('txtFileSearch'); });
    await search(page, fixtureName + ' Oldest-Target', 1);
    check('搜索覆盖 200 项以外的老文件', (await rows(page))[0].path.includes('Oldest-Target'));
    check('异步搜索保留输入框和键盘焦点', await page.evaluate(() =>
        window.librarySearchInput === document.getElementById('txtFileSearch') &&
        document.activeElement === window.librarySearchInput));

    await search(page, fixtureName + ' ALPHA BETA', 1);
    check('多词搜索忽略大小写且同时匹配所有词', (await rows(page))[0].path.includes('Beta-Alpha-Pump'));
    await search(page, fixtureName + ' No-Such-File', 0);
    check('无匹配结果仍有明确状态和搜索入口',
        await page.isVisible('#divNoFileMatch') &&
        !!(await page.locator('#fileLibraryResults [role="status"]').textContent()).trim() &&
        await page.isVisible('#txtFileSearch'));

    await search(page, fixtureName, 50);
    const sortResponse = page.waitForResponse(response => {
        const url = new URL(response.url());
        return url.pathname === listPath && url.searchParams.get('FileSort') === 'name';
    });
    await page.selectOption('#selFileSort', 'name');
    await (await sortResponse).finished();
    await waitForResults(page, fixtureName, 50, 'name');
    check('按名称排序并保留搜索条件',
        JSON.stringify((await rows(page)).map(row => path.basename(row.path.replaceAll('\\', '/')))) ===
        JSON.stringify(names.slice(0, 50)));

    const pageTwo = await page.locator('#fileLibraryResults a[href]').evaluateAll(links =>
        links.map((link, index) => ({ index, href: link.href })).find(item =>
            new URL(item.href).searchParams.get('FilePage') === '2'));
    check('分页链接保留搜索与排序参数', pageTwo &&
        new URL(pageTwo.href).searchParams.get('FileQuery') === fixtureName &&
        new URL(pageTwo.href).searchParams.get('FileSort') === 'name');
    await Promise.all([
        page.waitForURL(url => url.searchParams.get('FilePage') === '2'),
        page.locator('#fileLibraryResults a[href]').nth(pageTwo.index).click()
    ]);
    await waitForResults(page, fixtureName, 50, 'name');
    check('第二页显示下一组 50 项且无重复',
        JSON.stringify((await rows(page)).map(row => path.basename(row.path.replaceAll('\\', '/')))) ===
        JSON.stringify(names.slice(50, 100)));

    await search(page, fixtureName + ' Nested-Issues', 1);
    check('更换搜索条件回到第一页', new URL(page.url()).searchParams.get('FilePage') !== '2');
    const issueRow = await rowFor(page, 'Nested-Issues.mim');
    const details = issueRow.locator('details.file-issues');
    check('嵌套组件问题可展开查看', await details.count() === 1);
    await details.locator('summary').click();
    check('问题详情包含嵌套组件名称和通道属性',
        await details.getAttribute('open') !== null &&
        (await details.textContent()).includes('Nested valve check') &&
        (await details.textContent()).includes('FeedbackChannel') &&
        (await details.textContent()).includes('2147483000'));
    await page.screenshot({ path: path.join(screenshotDir, 'mimic-library-issues.png') });
    await Promise.all([
        page.waitForResponse(response => response.request().method() === 'POST' &&
            response.url().includes('handler=Open')),
        details.locator('button[name="componentID"][value="11"]').click()
    ]);
    await page.waitForLoadState('domcontentloaded');
    rememberEditorKey(page);
    await page.waitForFunction(() => typeof selectedComponents !== 'undefined' &&
        selectedComponents[0]?.id === 11 && !new URL(location.href).searchParams.has('componentID'));
    check('问题操作直接打开并选中嵌套组件，定位参数使用后清除',
        await page.evaluate(() => selectedComponents.length === 1 && selectedComponents[0].id === 11));
    await page.goto(listUrl(fixtureName + ' Nested-Issues'));

    await search(page, fixtureName + ' Zero-Channels', 1);
    check('没有 Label 属性及零通道的组件不误报空标签',
        await (await rowFor(page, 'Zero-Channels.mim')).locator('details.file-issues').count() === 0);

    await search(page, fixtureName + ' Source', 1);
    const sourceRow = await rowFor(page, 'Source.mim');
    await Promise.all([
        page.waitForResponse(response => response.request().method() === 'POST' &&
            response.url().includes('handler=Open')),
        sourceRow.locator('button[formaction*="Open"]').click()
    ]);
    await page.waitForLoadState('domcontentloaded');
    check('点击文件名直接打开编辑器', /\/MimicEditor\/MimicEdit\/\d+$/.test(new URL(page.url()).pathname),
        await page.locator('.alert-danger').allTextContents().then(messages => messages.join('; ')));
    rememberEditorKey(page);
    await page.waitForFunction(() => typeof mimic !== 'undefined' && mimic.componentMap.size === 1);

    const query = fixtureName + ' Source';
    await page.goto(listUrl(query, 'modified'));
    await waitForResults(page, query, 1, 'modified');
    await duplicate(page, 'Source.mim', 'Copied-source', true);
    await page.waitForFunction(() => typeof mimic !== 'undefined' && mimic.componentMap.size === 1);
    const copy = path.join(fixtureDir, 'Copied-source.mim');
    check('复制已保存文件到源目录并直接打开', fs.existsSync(copy) &&
        fs.readFileSync(copy).equals(sourceBytes));
    check('复制不会更改源文件', fs.readFileSync(source).equals(sourceBytes));

    await page.goto(listUrl(query, 'modified'));
    await waitForResults(page, query, 2, 'modified');
    await duplicate(page, 'Source.mim', 'Copied-source', false);
    await page.waitForFunction(() => document.querySelector('.alert-danger'));
    check('同名复制被拒绝且已有文件不被覆盖',
        new URL(page.url()).pathname === listPath && fs.readFileSync(copy).equals(sourceBytes) &&
        fs.readFileSync(source).equals(sourceBytes));
    check('复制失败后保留搜索与排序条件',
        await page.inputValue('#txtFileSearch') === query &&
        await page.inputValue('#selFileSort') === 'modified');
    check('列表和编辑器没有未处理脚本异常', errors.length === 0, errors.join('; '));
    console.log(`SUMMARY ${passed}/${passed} passed`);
}

(async () => {
    try {
        await run();
    } catch (error) {
        console.error(error);
        process.exitCode = 1;
    } finally {
        // Only keys opened by this run are closed; existing editor sessions are untouched.
        if (context) {
            for (const key of keys) {
                try {
                    const response = await context.request.post(base + '/Api/MimicEditor/Updater/CloseMimic?key=' +
                        encodeURIComponent(key) + '&save=false');
                    if (!response.ok()) throw new Error('HTTP ' + response.status());
                } catch (error) {
                    console.error('Close fixture ' + key + ': ' + error.message);
                    process.exitCode = 1;
                }
            }
        }
        if (browser) await browser.close();
        const target = path.resolve(fixtureDir);
        if (path.dirname(target) !== path.resolve(views) || !path.basename(target).startsWith('__library-check-')) {
            throw new Error('Unsafe fixture cleanup path: ' + target);
        }
        fs.rmSync(target, { recursive: true, force: true });
        if (fs.existsSync(target)) throw new Error('Fixture cleanup failed: ' + target);
        console.log('CLEANUP | Disposable library directory removed; only this run\'s editor keys were closed.');
    }
})();
