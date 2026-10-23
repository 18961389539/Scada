// Real Edge checks use one disposable mimic, with no channel bindings or equipment commands.
// Requires playwright-core on NODE_PATH and the running local HelloWorld demo.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright-core');

const views = path.resolve(__dirname, '../.local/HelloWorld/Views');
const fixtureDir = path.join(views, `__canvas-check-${process.pid}-${Date.now()}`);
const fixture = path.join(fixtureDir, 'Navigation.mim');
const base = process.env.SCADA_BASE_URL || 'http://127.0.0.1:5000';
let browser;
let context;
let page;
let key;
let passed = 0;
const errors = [];

function check(name, value, detail = '') {
    assert.ok(value, name + (detail ? ': ' + detail : ''));
    passed++;
    console.log('PASS | ' + name + (detail ? ' | ' + detail : ''));
}

async function state() {
    return page.evaluate(() => {
        const wrapper = document.getElementById('divMimicWrapper');
        const viewport = wrapper.getBoundingClientRect();
        const bounds = selectedComponents.map(component => component.dom[0].getBoundingClientRect());
        return {
            ids: selectedComponents.map(component => component.id),
            model: JSON.stringify({ document: mimic.document, components: mimic.components.map(c => c.toPlainObject()) }),
            revision: editRevision, modified: mimicModified, queue: updateQueue.length,
            windowX: window.scrollX, windowY: window.scrollY,
            scrollLeft: wrapper.scrollLeft, scrollTop: wrapper.scrollTop,
            zoom: Number(mimicElem[0].style.zoom),
            errorX: bounds.length ? (Math.min(...bounds.map(r => r.left)) + Math.max(...bounds.map(r => r.right))) / 2 -
                viewport.left - wrapper.clientLeft - wrapper.clientWidth / 2 : null,
            errorY: bounds.length ? (Math.min(...bounds.map(r => r.top)) + Math.max(...bounds.map(r => r.bottom))) / 2 -
                viewport.top - wrapper.clientTop - wrapper.clientHeight / 2 : null
        };
    });
}

async function choose(ids) {
    await page.evaluate(ids => {
        selectComponents(ids.map(id => mimic.componentMap.get(id)));
        const wrapper = document.getElementById('divMimicWrapper');
        wrapper.scrollLeft = 0;
        wrapper.scrollTop = 0;
        document.activeElement?.blur();
    }, ids);
    await page.waitForFunction(() => !document.getElementById('btnEditorLocate').disabled);
}

async function verifyNavigation(name, action) {
    const before = await state();
    await action();
    const after = await state();
    check(name, Math.abs(after.errorX) <= 1 && Math.abs(after.errorY) <= 1,
        `center error ${after.errorX.toFixed(2)}, ${after.errorY.toFixed(2)} px`);
    check(name + ' preserves selection/model/page scroll',
        JSON.stringify(after.ids) === JSON.stringify(before.ids) && after.model === before.model &&
        after.revision === before.revision && after.modified === before.modified && after.queue === before.queue &&
        after.windowX === before.windowX && after.windowY === before.windowY);
}

async function setZoom(value) {
    await page.evaluate(value => localStorage.setItem('rs.mimicEditor.zoom', String(value)), value);
    await page.reload();
    await page.waitForFunction(value => typeof mimic !== 'undefined' && mimic.componentMap.size === 5 &&
        mimicElem.length > 0 && Number(mimicElem[0].style.zoom) === value, value);
}

async function run() {
    fs.mkdirSync(fixtureDir);
    fs.writeFileSync(fixture, `<?xml version="1.0" encoding="utf-8"?>
<Mimic editorVersion="6.0.2.2"><Dependencies />
<Document><BackColor>#eef2f7</BackColor><Size><Width>6000</Width><Height>4200</Height></Size></Document>
<Components>
  <Text><ID>1</ID><Name>Far target</Name><Text>Far target</Text><AutoSize>false</AutoSize><Location><X>2500</X><Y>1800</Y></Location><Size><Width>240</Width><Height>80</Height></Size></Text>
  <Text><ID>2</ID><Name>Left selection</Name><Text>Left</Text><AutoSize>false</AutoSize><Location><X>2100</X><Y>1500</Y></Location><Size><Width>200</Width><Height>100</Height></Size></Text>
  <Text><ID>3</ID><Name>Right selection</Name><Text>Right</Text><AutoSize>false</AutoSize><Location><X>3400</X><Y>2300</Y></Location><Size><Width>300</Width><Height>120</Height></Size></Text>
  <Panel><ID>10</ID><Name>Nested container</Name><Location><X>3600</X><Y>2400</Y></Location><Size><Width>600</Width><Height>500</Height></Size>
    <Components><Text><ID>11</ID><Name>Nested target</Name><Text>Nested target</Text><AutoSize>false</AutoSize><Location><X>120</X><Y>160</Y></Location><Size><Width>220</Width><Height>80</Height></Size></Text></Components>
  </Panel>
</Components><Images /></Mimic>`, 'utf8');
    const originalFile = fs.readFileSync(fixture, 'utf8');
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    context = await browser.newContext({ viewport: { width: 1540, height: 960 } });
    page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.dismiss());
    await page.goto(base + '/Login');
    await page.fill('#txtUsername', process.env.SCADA_DEMO_USER || 'admin');
    await page.fill('#txtPassword', process.env.SCADA_DEMO_PASSWORD || '12345');
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Login')), page.click('#btnLogin')]);
    await page.goto(base + '/MimicEditor/MimicOpen?fileName=' + encodeURIComponent(fixture));
    const openedPath = new URL(page.url()).pathname.match(/\/MimicEditor\/MimicEdit\/(\d+)$/);
    assert.ok(openedPath, 'opening the disposable file must return its editor URL');
    key = openedPath[1];
    await page.waitForFunction(() => typeof mimic !== 'undefined' && mimic.componentMap.size === 5 && mimicElem.length > 0);
    assert.equal(await page.evaluate(() => mimicKey), key, 'model and disposable editor URL must share one key');
    check('fixture opens with its nested child', await page.evaluate(() => mimic.componentMap.get(11).parentID === 10));

    for (const zoom of [0.5, 1.5]) {
        await setZoom(zoom);
        await choose([1]);
        await verifyNavigation(`${zoom * 100}% toolbar locate`, () => page.click('#btnEditorLocate'));
        await choose([1]);
        await verifyNavigation(`${zoom * 100}% F locate`, () => page.keyboard.press('f'));
        await choose([2, 3]);
        await verifyNavigation(`${zoom * 100}% multiselection union`, () => page.keyboard.press('f'));
        await choose([11]);
        await verifyNavigation(`${zoom * 100}% nested component`, () => page.click('#btnEditorLocate'));
    }

    await page.click('[data-bs-target="#divStructure"]');
    await page.click('#struct-comp-item11 > .node-comp');
    await choose([11]);
    await verifyNavigation('structure node double-click', () => page.dblclick('#struct-comp-item11 > .node-comp'));

    await choose([1]);
    await page.focus('#structureSearch');
    const beforeInput = await state();
    await page.keyboard.press('f');
    const afterInput = await state();
    check('F in search types text without moving the canvas', await page.inputValue('#structureSearch') === 'f' &&
        beforeInput.scrollLeft === afterInput.scrollLeft && beforeInput.scrollTop === afterInput.scrollTop &&
        beforeInput.model === afterInput.model && JSON.stringify(beforeInput.ids) === JSON.stringify(afterInput.ids));
    await page.fill('#structureSearch', '');

    await choose([1]);
    await page.evaluate(() => {
        document.documentElement.style.overflow = 'auto';
        document.body.style.overflow = 'auto';
        const spacer = document.createElement('div');
        spacer.style.height = '2400px';
        document.body.appendChild(spacer);
    });
    await page.waitForFunction(() => document.scrollingElement.scrollHeight > window.innerHeight);
    await page.evaluate(() => window.scrollTo(0, 80));
    await page.waitForFunction(() => window.scrollY === 80);
    check('page scroll fixture begins away from the top', (await state()).windowY > 0);
    await verifyNavigation('F with an independently scrolled outer page', () => page.keyboard.press('f'));

    for (const zoom of [0.5, 1.5]) {
        await page.evaluate(value => localStorage.setItem('rs.mimicEditor.zoom', String(value)), zoom);
        await page.goto(base + '/MimicEditor/MimicEdit/' + encodeURIComponent(key) + '?componentID=11&check=retained');
        await page.waitForFunction(value => Number(mimicElem[0]?.style.zoom) === value &&
            selectedComponents[0]?.id === 11 && !new URL(location.href).searchParams.has('componentID'), zoom);
        const requested = await state();
        check(`${zoom * 100}% componentID link selects and centers after restoring zoom`,
            requested.ids.join(',') === '11' && Math.abs(requested.errorX) <= 1 && Math.abs(requested.errorY) <= 1 &&
            new URL(page.url()).searchParams.get('check') === 'retained',
            `center error ${requested.errorX.toFixed(2)}, ${requested.errorY.toFixed(2)} px`);
        await choose([1]);
        await page.evaluate(() => mimic.componentMap.get(11).dom.toggleClass('navigation-check-redraw'));
        await page.waitForTimeout(60);
        check('consumed componentID does not override later selection', (await state()).ids.join(',') === '1');
    }

    check('original fixture file remains unchanged', fs.readFileSync(fixture, 'utf8') === originalFile);
    check('browser has no unhandled script errors', errors.length === 0, errors.join('; '));
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
            try {
                const response = await context.request.post(base + '/Api/MimicEditor/Updater/CloseMimic?key=' +
                    encodeURIComponent(key) + '&save=false');
                const result = await response.json();
                if (!response.ok() || !result.ok) throw new Error('Close fixture failed: ' + JSON.stringify(result));
            } catch (error) {
                console.error(error.message);
                process.exitCode = 1;
            }
        }
        if (browser) await browser.close();
        const target = path.resolve(fixtureDir);
        if (path.dirname(target) !== path.resolve(views) || !path.basename(target).startsWith('__canvas-check-'))
            throw new Error('Unsafe fixture cleanup path: ' + target);
        fs.rmSync(target, { recursive: true, force: true });
        if (fs.existsSync(target)) throw new Error('Fixture cleanup failed: ' + target);
        console.log('CLEANUP | Removed this run\'s disposable directory and closed only its editor key.');
    }
})();
