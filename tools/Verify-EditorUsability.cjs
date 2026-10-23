// Browser checks use a disposable mimic and never send equipment commands.
// Requires playwright-core and an existing local HelloWorld demo (system Edge is used).
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright-core');

const repo = path.resolve(__dirname, '..');
const views = path.join(repo, '.local/HelloWorld/Views');
const fixtureDir = path.join(views, `__editor-check-${process.pid}-${Date.now()}`);
const fixture = path.join(fixtureDir, 'Usability.mim');
const base = process.env.SCADA_BASE_URL || 'http://127.0.0.1:5000';
const screenshotDir = path.join(repo, '.local/verification');
let browser;
let context;
let key;
let passed = 0;

function check(name, ok) {
    assert.ok(ok, name);
    console.log('PASS | ' + name);
    passed++;
}

async function run() {
    fs.mkdirSync(fixtureDir, { recursive: true });
    fs.mkdirSync(screenshotDir, { recursive: true });
    fs.writeFileSync(fixture, `<?xml version="1.0" encoding="utf-8"?>
<Mimic editorVersion="6.0.2.2">
  <Dependencies />
  <Document><BackColor>#eef2f7</BackColor><Size><Width>1000</Width><Height>700</Height></Size></Document>
  <Components>
    <Text><ID>1</ID><Name>操作标题</Name><Text>操作标题</Text><AutoSize>false</AutoSize><Location><X>30</X><Y>30</Y></Location><Size><Width>220</Width><Height>40</Height></Size></Text>
    <IndustrialTabs><ID>10</ID><Name>泵站分组</Name><Tabs>液位</Tabs><Location><X>30</X><Y>100</Y></Location><Size><Width>400</Width><Height>240</Height></Size>
      <Components><Text><ID>11</ID><Name>目标液位子组件</Name><Text>液位反馈</Text><Location><X>20</X><Y>20</Y></Location></Text></Components>
    </IndustrialTabs>
    <IndustrialDataTable><ID>20</ID><Name>实时通道表</Name><Channels>201,301</Channels><Location><X>480</X><Y>100</Y></Location><Size><Width>400</Width><Height>240</Height></Size></IndustrialDataTable>
  </Components>
</Mimic>`, 'utf8');

    browser = await chromium.launch({ channel: 'msedge', headless: true });
    context = await browser.newContext({ viewport: { width: 1540, height: 960 } });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.dismiss());
    await page.goto(base + '/Login');
    await page.fill('#txtUsername', process.env.SCADA_DEMO_USER || 'admin');
    await page.fill('#txtPassword', process.env.SCADA_DEMO_PASSWORD || '12345');
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Login')), page.click('#btnLogin')]);
    await page.goto(base + '/MimicEditor/MimicOpen?fileName=' + encodeURIComponent(fixture));
    await page.waitForFunction(() => typeof mimic !== 'undefined' && mimic.componentMap.size === 4 && !!propGrid);
    key = await page.evaluate(() => mimicKey);

    await page.click('[data-bs-target="#divStructure"]');
    await page.fill('#structureSearch', '目标液位');
    await page.waitForFunction(() => document.querySelector('#structureSearchCount').textContent.includes('1 / 4'));
    check('结构搜索保留子组件及其父层级',
        await page.isVisible('#struct-comp-item11') && await page.isVisible('#struct-comp-item10') &&
        !await page.isVisible('#struct-comp-item1'));
    await page.click('#struct-comp-item11 > .node-comp');
    check('搜索结果仍可选中原组件', await page.evaluate(() => selectedComponents[0]?.id === 11));
    await page.screenshot({ path: path.join(screenshotDir, 'editor-structure-search.png') });

    await page.fill('#structureSearch', 'IndustrialDataTable');
    check('结构支持按类型搜索', await page.isVisible('#struct-comp-item20') && !await page.isVisible('#struct-comp-item11'));
    await page.fill('#structureSearch', '不存在的组件');
    check('结构无匹配提示', await page.isVisible('#structureSearchEmpty'));
    await page.press('#structureSearch', 'Escape');
    check('Esc 清空并恢复结构树', await page.inputValue('#structureSearch') === '' && await page.isVisible('#struct-comp-item1'));

    await page.fill('#structureSearch', '重命名测试');
    await page.evaluate(() => {
        const component = mimic.componentMap.get(1);
        component.properties.name = '重命名测试';
        handlePropertyChanged({ topObject: component, topPropertyName: 'name', topPropertyValue: component.properties.name });
    });
    await page.waitForFunction(() => !document.querySelector('#struct-comp-item1').hidden);
    check('组件改名后自动更新搜索结果', await page.isVisible('#struct-comp-item1'));

    await page.locator('body').click({ position: { x: 1530, y: 950 } });
    await page.keyboard.press('/');
    check('/ 切换到组件面板并聚焦搜索', await page.evaluate(() => document.activeElement.id === 'componentSearch' && document.querySelector('#divComponents').classList.contains('active')));
    await page.fill('#componentSearch', 'IndustrialDataTable');
    await page.press('#componentSearch', 'Escape');
    check('组件搜索 Esc 清空后返回画布操作', await page.inputValue('#componentSearch') === '' && await page.evaluate(() => document.activeElement === document.body));

    await page.evaluate(() => selectComponent(mimic.componentMap.get(1)));
    const originalX = await page.evaluate(() => selectedComponents[0].x);
    await page.keyboard.press('Control+ArrowRight');
    await page.keyboard.press('Control+z');
    check('Ctrl+Z 撤销移动', await page.evaluate(() => selectedComponents[0].x) === originalX);
    await page.keyboard.press('Control+Shift+z');
    check('Ctrl+Shift+Z 重做移动', await page.evaluate(() => selectedComponents[0].x) === originalX + 1);

    await page.evaluate(() => {
        const input = document.createElement('div');
        input.id = 'temporaryEditable';
        input.contentEditable = 'true';
        input.textContent = '编辑文本';
        document.querySelector('#divToolbar').appendChild(input);
        input.focus();
    });
    await page.keyboard.press('Delete');
    check('输入文本时 Delete 不会删除选中组件', await page.evaluate(() => mimic.componentMap.has(1)));
    await page.evaluate(() => document.querySelector('#temporaryEditable').remove());

    // Entering the factory default must immediately disappear from "modified only".
    await page.click('[data-bs-target="#divProperties"]');
    const label = await page.evaluate(() => PropGridHelper.getObjectDescriptor(selectedComponents[0]).propertyDescriptors.get('text').displayName);
    await page.fill('#txtPropSearch', label);
    await page.check('#chkPropModified');
    await page.evaluate(label => {
        const row = [...document.querySelectorAll('#tweakpane .tp-lblv')].find(element => element.querySelector('.tp-lblv_l')?.textContent.trim() === label);
        row.querySelector('input').setAttribute('data-editor-check', 'text');
    }, label);
    const defaultText = await page.evaluate(() => rs.mimic.FactorySet.componentFactories.get('Text').createComponent().properties.text);
    await page.fill('[data-editor-check="text"]', defaultText);
    await page.press('[data-editor-check="text"]', 'Enter');
    await page.waitForFunction(() => document.querySelector('#spanPropCount').textContent.startsWith('0 /'));
    check('属性改回默认值后即时刷新仅已修改筛选', await page.locator('[data-editor-check="text"]').isHidden());

    await page.evaluate(() => {
        const component = mimic.componentMap.get(20);
        const catalog = JSON.parse(document.querySelector('#editorChannelCatalog').textContent);
        component.properties.channels = catalog.filter(channel => channel.readable).slice(0, 16).map(channel => channel.number).join(',');
        handlePropertyChanged({ topObject: component, topPropertyName: 'channels', topPropertyValue: component.properties.channels });
        selectComponent(component);
    });
    await page.waitForFunction(() => !document.querySelector('#btnChannelPicker').disabled);
    await page.click('#btnChannelPicker');
    await page.selectOption('#channelPickerProperty', 'channels');
    await page.waitForFunction(() => document.querySelector('#channelPickerFeedback').textContent.includes('16 / 16'));
    check('16 个通道显示上限并禁用新项', await page.locator('#channelPickerResults button:disabled').count() > 0);
    const beforeDuplicate = await page.evaluate(() => editRevision);
    await page.locator('#channelPickerResults button[aria-pressed="true"]').first().click();
    check('重复选择已绑定通道不产生修改', await page.evaluate(() => editRevision) === beforeDuplicate);
    await page.selectOption('#channelPickerProperty', 'inCnlNum');
    await page.evaluate(() => {
        document.querySelector('#channelPickerProperty').value = '';
        document.querySelector('#channelPickerProperty').dispatchEvent(new Event('change', { bubbles: true }));
    });
    check('无效绑定属性不显示可应用通道', await page.locator('#channelPickerResults button').count() === 0 && await page.evaluate(() => !Object.hasOwn(selectedComponents[0].properties, 'null')));
    await page.click('#channelPicker .btn-close');
    await page.waitForFunction(() => !document.querySelector('#channelPicker').classList.contains('show'));
    await page.waitForFunction(() => !updateInProgress && updateQueue.length === 0);

    // Reject an update at the server boundary: it must survive until acceptance and save.
    let rejectUpdates = true;
    let saveRequests = 0;
    page.on('request', request => { if (request.url().includes('/Updater/SaveMimic?')) saveRequests++; });
    await page.route('**/Api/MimicEditor/Updater/UpdateMimic', route => rejectUpdates
        ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ok: false, msg: '测试：暂未接受更新' }) })
        : route.continue());
    const unchangedFile = fs.readFileSync(fixture, 'utf8');
    await page.evaluate(() => { selectComponent(mimic.componentMap.get(1)); moveComponents(7, 0); });
    await page.waitForFunction(() => document.querySelector('#editorSaveStatus').textContent.includes('同步失败'));
    await page.click('#btnSave');
    await page.waitForTimeout(1200);
    check('更新拒绝后保留队列，保存等待且原文件不变', await page.evaluate(() => updateQueue.length > 0 && queueEmptyFlags.saveRequired) && saveRequests === 0 && fs.readFileSync(fixture, 'utf8') === unchangedFile);
    rejectUpdates = false;
    await page.waitForFunction(() => !mimicModified && !updateInProgress && !saveInProgress && updateQueue.length === 0 && !queueEmptyFlags.saveRequired);
    check('恢复后提交修改并仅保存一次', saveRequests === 1 && fs.readFileSync(fixture, 'utf8') !== unchangedFile);
    check('恢复后保存状态清除失败提示', !(await page.textContent('#editorSaveStatus')).includes('失败'));
    check('编辑页面没有未处理的脚本异常', errors.length === 0);
    console.log(`SUMMARY ${passed}/${passed} passed`);
}

(async () => {
    try {
        await run();
    } catch (error) {
        console.error(error);
        process.exitCode = 1;
    } finally {
        if (context && key) {
            try { await context.request.post(base + '/Api/MimicEditor/Updater/CloseMimic?key=' + encodeURIComponent(key) + '&save=false'); }
            catch (error) { console.error('Close fixture: ' + error.message); }
        }
        if (browser) await browser.close();
        const target = path.resolve(fixtureDir);
        const expectedParent = path.resolve(views) + path.sep;
        if (!target.startsWith(expectedParent) || !path.basename(target).startsWith('__editor-check-'))
            throw new Error('Unsafe fixture cleanup path: ' + target);
        fs.rmSync(target, { recursive: true, force: true });
    }
})();
