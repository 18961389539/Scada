// Measures mimic rendering cost for a small and a generated large diagram.
// Numbers come from the editor's own console timings plus a long-task sample on the runtime page.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const BASE = 'http://127.0.0.1:5000';
const VIEWS_DIR = 'D:/projects/开发项目/scada/.local/HelloWorld/Views/HelloWorld';
const OVERVIEW = path.join(VIEWS_DIR, 'Overview.mim');
const LARGE_FILE = path.join(VIEWS_DIR, 'ZzPerf300.mim');
const LARGE_COUNT = 300;

function buildLargeMimic(count) {
    const components = [];

    for (let i = 1; i <= count; i++) {
        const col = (i - 1) % 20;
        const row = Math.floor((i - 1) / 20);
        components.push(`    <Text>
      <ID>${i}</ID>
      <Name>PerfText${i}</Name>
      <Enabled>true</Enabled>
      <Visible>true</Visible>
      <Location><X>${10 + col * 145}</X><Y>${10 + row * 40}</Y></Location>
      <Size><Width>130</Width><Height>24</Height></Size>
      <Text>组件 ${i}</Text>
      <AutoSize>false</AutoSize>
      <Font><Name>Microsoft YaHei</Name><Size>12</Size></Font>
      <ForeColor>#213047</ForeColor>
    </Text>`);
    }

    return `<?xml version='1.0' encoding='utf-8'?>
<Mimic editorVersion="6.0.2.2">
  <Dependencies />
  <Document>
    <BackColor>#eef2f7</BackColor>
    <Font>
      <Name>Microsoft YaHei</Name>
      <Size>14</Size>
    </Font>
    <ForeColor>#213047</ForeColor>
    <Size>
      <Width>3000</Width>
      <Height>${Math.ceil(count / 20) * 40 + 60}</Height>
    </Size>
  </Document>
  <Components>
${components.join('\n')}
  </Components>
  <Images />
</Mimic>`;
}

async function login(context) {
    const page = await context.newPage();
    await page.goto(BASE + '/Login');
    await page.fill('#txtUsername', 'admin');
    await page.fill('#txtPassword', '12345');
    await Promise.all([page.waitForNavigation(), page.click('#btnLogin')]);
    return page;
}

function parseMs(logs, marker) {
    for (const line of logs) {
        if (line.includes(marker)) {
            const match = line.match(/([\d.]+)\s*ms/);
            if (match) {
                return Number(match[1]);
            }
        }
    }
    return null;
}

async function measureEditor(context, file, label) {
    const page = await context.newPage();
    const logs = [];

    page.on('console', msg => {
        const text = msg.text();
        if (text.includes('Mimic loading completed') || text.includes('Mimic DOM created')) {
            logs.push(text);
        }
    });

    const startedAt = Date.now();
    await page.goto(BASE + '/MimicEditor/MimicOpen?fileName=' + encodeURIComponent(file));
    await page.waitForFunction(() => document.querySelectorAll('#divMimicWrapper .comp').length > 0, { timeout: 30000 })
        .catch(() => null);
    const readyMs = Date.now() - startedAt;
    await page.waitForTimeout(2500);

    const stats = await page.evaluate(() => ({
        components: document.querySelectorAll('#divMimicWrapper .comp').length,
        domNodes: document.querySelectorAll('#divMimicWrapper *').length
    }));

    await page.close();

    return {
        label,
        components: stats.components,
        domNodes: stats.domNodes,
        readyMs,
        loadMs: parseMs(logs, 'Mimic loading completed'),
        domMs: parseMs(logs, 'Mimic DOM created')
    };
}

async function measureRuntime(context, viewID, label) {
    const page = await context.newPage();
    await page.goto(BASE + '/Mimic/MimicView/' + viewID);
    await page.waitForTimeout(4000);

    await page.evaluate(() => {
        window.__longTasks = [];
        window.__heap = 0;

        try {
            new PerformanceObserver(list => {
                for (const entry of list.getEntries()) {
                    window.__longTasks.push(Math.round(entry.duration));
                }
            }).observe({ entryTypes: ['longtask'] });
        } catch (error) {
            // longtask is Chromium-only.
        }
    });

    await page.waitForTimeout(10000);

    const result = await page.evaluate(() => ({
        components: document.querySelectorAll('#divMimicWrapper .comp').length,
        longTasks: window.__longTasks || [],
        heapMb: performance.memory ? Math.round(performance.memory.usedJSHeapSize / 1048576) : null
    }));

    await page.close();

    return {
        label,
        components: result.components,
        longTaskCount: result.longTasks.length,
        longTaskMax: result.longTasks.length ? Math.max(...result.longTasks) : 0,
        heapMb: result.heapMb
    };
}

(async () => {
    fs.writeFileSync(LARGE_FILE, buildLargeMimic(LARGE_COUNT), 'utf8');

    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const context = await browser.newContext({ viewport: { width: 1680, height: 1000 } });
    await login(context);

    const small = await measureEditor(context, OVERVIEW, '编辑器 Overview（28 组件）');
    const large = await measureEditor(context, LARGE_FILE, `编辑器 测试画面（${LARGE_COUNT} 组件）`);
    const runtime = await measureRuntime(context, 10, '运行页 视图 10（28 组件）');

    await browser.close();
    fs.rmSync(LARGE_FILE, { force: true });

    console.log('\n=== Mimic 渲染基线 ===');
    for (const item of [small, large]) {
        console.log(`${item.label}: 组件=${item.components} DOM节点=${item.domNodes} ` +
            `首屏=${item.readyMs}ms 解析=${item.loadMs}ms DOM创建=${item.domMs}ms`);
    }
    console.log(`${runtime.label}: 组件=${runtime.components} ` +
        `10s 长任务=${runtime.longTaskCount} 个，最长=${runtime.longTaskMax}ms JS堆=${runtime.heapMb}MB`);
})().catch(error => {
    console.error('ERROR: ' + error.message);
    try { fs.rmSync(LARGE_FILE, { force: true }); } catch { /* Best effort. */ }
    process.exit(1);
});
