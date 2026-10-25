<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>运行布局</strong><span class="spacer" />
      <el-button type="primary" data-test="layout-new" @click="edit()">新建布局</el-button><el-button @click="load()">刷新</el-button></div>
    <el-table :data="layouts" size="small" data-test="layouts" empty-text="尚无运行布局">
      <el-table-column prop="name" label="名称" min-width="160" />
      <el-table-column label="显示器" min-width="260"><template #default="{ row }"><span class="tiny">{{ row.document.monitors.map((m: HmiMonitor, i: number) => `${m.name || i + 1}：${m.regions.length} 区`).join('；') }}</span></template></el-table-column>
      <el-table-column label="修改" width="170"><template #default="{ row }"><span class="tiny">r{{ row.revision }} · {{ row.updatedBy }}</span></template></el-table-column>
      <el-table-column label="操作" width="230"><template #default="{ row }">
        <el-button size="small" type="primary" @click="router.push(`/console/${row.id}`)">打开</el-button>
        <el-button size="small" @click="edit(row)">编辑</el-button><el-button size="small" type="danger" @click="remove(row)">删除</el-button>
      </template></el-table-column>
    </el-table>
  </div>
  <div class="panel" style="margin-top: 16px">
    <div class="toolbar"><strong>工作站</strong><el-tag size="small" :type="local ? 'success' : 'info'" data-test="local-station">本机：{{ local || '未登记' }}</el-tag>
      <el-button v-if="local" size="small" link @click="setLocal(null)">清除本机登记</el-button><span class="spacer" />
      <el-button data-test="station-new" @click="editStation()">登记工作站</el-button></div>
    <p class="tiny muted">在操作员电脑的浏览器中点“设为本机”，之后打开“运行布局”会自动使用分配给该工作站的布局。“只监视”工作站发出的控制请求会被服务端拒绝并审计（纵深防御，权限仍以用户区域控制权限为准）。</p>
    <el-table :data="stations" size="small" data-test="stations" empty-text="尚未登记工作站">
      <el-table-column prop="name" label="名称" min-width="140" />
      <el-table-column prop="description" label="说明" min-width="160" />
      <el-table-column label="布局" min-width="140"><template #default="{ row }">{{ layouts.find(l => l.id === row.layoutId)?.name ?? '-' }}</template></el-table-column>
      <el-table-column label="控制" width="90"><template #default="{ row }"><el-tag size="small" :type="row.allowControl ? 'success' : 'warning'">{{ row.allowControl ? '允许' : '只监视' }}</el-tag></template></el-table-column>
      <el-table-column label="操作" width="230"><template #default="{ row }">
        <el-button size="small" :data-test="`station-local-${row.name}`" @click="setLocal(row.name)">设为本机</el-button>
        <el-button size="small" @click="editStation(row)">编辑</el-button><el-button size="small" type="danger" @click="removeStation(row)">删除</el-button>
      </template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="visible" :title="form.id ? '编辑布局' : '新建布局'" width="1100px" data-test="layout-dialog">
    <el-form label-width="80px" size="small" inline>
      <el-form-item label="名称"><el-input v-model="form.name" maxlength="100" style="width: 220px" data-test="layout-name" /></el-form-item>
      <el-form-item><el-button @click="preset()">套用“横幅 + 总览 + 详情/报警”</el-button><el-button data-test="layout-add-monitor" @click="addMonitor()">添加显示器</el-button></el-form-item>
    </el-form>
    <el-tabs v-model="monitorTab" type="card">
      <el-tab-pane v-for="(m, mi) in form.document.monitors" :key="mi" :label="m.name || `显示器 ${mi + 1}`" :name="String(mi)">
        <div class="toolbar">
          名称 <el-input v-model="m.name" size="small" style="width: 120px" />
          列 <el-input-number v-model="m.columns" :min="1" :max="24" size="small" /> 行 <el-input-number v-model="m.rows" :min="1" :max="24" size="small" />
          <el-button size="small" :data-test="`layout-add-region-${mi}`" @click="addRegion(m)">添加区域</el-button>
          <el-button v-if="form.document.monitors.length > 1" size="small" type="danger" link @click="form.document.monitors.splice(mi, 1); monitorTab = '0'">删除显示器</el-button>
        </div>
        <div class="editor">
          <el-table :data="m.regions" size="small" style="flex: 1">
            <el-table-column label="编号" width="90"><template #default="{ row }"><el-input v-model="row.id" size="small" /></template></el-table-column>
            <el-table-column label="类型" width="120"><template #default="{ row }"><el-select v-model="row.kind" size="small" data-test="region-kind"><el-option v-for="(label, k) in kinds" :key="k" :value="k" :label="label" /></el-select></template></el-table-column>
            <el-table-column label="画面" min-width="150"><template #default="{ row }"><el-select v-if="row.kind === 'screen'" v-model="row.screenId" size="small" filterable data-test="region-screen"><el-option v-for="s in screens" :key="s.id" :value="s.id" :label="s.name" /></el-select></template></el-table-column>
            <el-table-column label="标题" width="110"><template #default="{ row }"><el-input v-model="row.title" size="small" /></template></el-table-column>
            <el-table-column label="行/列/跨行/跨列" width="270"><template #default="{ row }">
              <el-input-number v-model="row.row" :min="1" :max="m.rows" size="small" controls-position="right" style="width: 60px" />
              <el-input-number v-model="row.col" :min="1" :max="m.columns" size="small" controls-position="right" style="width: 60px" />
              <el-input-number v-model="row.rowSpan" :min="1" :max="m.rows" size="small" controls-position="right" style="width: 60px" />
              <el-input-number v-model="row.colSpan" :min="1" :max="m.columns" size="small" controls-position="right" style="width: 60px" />
            </template></el-table-column>
            <el-table-column width="50"><template #default="{ $index }"><el-button size="small" link type="danger" @click="m.regions.splice($index, 1)">删</el-button></template></el-table-column>
          </el-table>
          <div class="preview" :style="{ gridTemplateColumns: `repeat(${m.columns}, 1fr)`, gridTemplateRows: `repeat(${m.rows}, 1fr)` }">
            <div v-for="r in m.regions" :key="r.id" :class="`k-${r.kind}`" :style="{ gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }">{{ r.title || kinds[r.kind] }}<small v-if="r.kind === 'screen'">{{ screens.find(s => s.id === r.screenId)?.name }}</small></div>
          </div>
        </div>
      </el-tab-pane>
    </el-tabs>
    <template #footer><el-button @click="visible = false">取消</el-button><el-button type="primary" data-test="layout-save" @click="save()">保存</el-button></template>
  </el-dialog>

  <el-dialog v-model="stationVisible" :title="stationForm.id ? '编辑工作站' : '登记工作站'" width="480px">
    <el-form label-width="90px" size="small">
      <el-form-item label="名称"><el-input v-model="stationForm.name" placeholder="如 CCR-OP1" data-test="station-name" /></el-form-item>
      <el-form-item label="说明"><el-input v-model="stationForm.description" /></el-form-item>
      <el-form-item label="布局"><el-select v-model="stationForm.layoutId" clearable data-test="station-layout"><el-option v-for="l in layouts" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
      <el-form-item label="允许控制"><el-switch v-model="stationForm.allowControl" data-test="station-control" /></el-form-item>
    </el-form>
    <template #footer><el-button @click="stationVisible = false">取消</el-button><el-button type="primary" data-test="station-save" @click="saveStation()">保存</el-button></template>
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { hmiLayoutApi, WORKSTATION_KEY, type HmiLayout, type HmiLayoutDocument, type HmiMonitor, type HmiWorkstation, type RegionKind } from '../api/hmiLayouts'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import type { ScreenSummary } from '../screens/model'

const router = useRouter()
const layouts = ref<HmiLayout[]>([]), stations = ref<HmiWorkstation[]>([]), screens = ref<ScreenSummary[]>([])
const kinds: Record<RegionKind, string> = { screen: '工艺画面', alarmBanner: '报警横幅', alarmList: '报警列表', navigation: '画面目录' }
const local = ref(localStorage.getItem(WORKSTATION_KEY))
async function guard(fn: () => Promise<unknown>, fallback: string): Promise<boolean> { try { await fn(); return true } catch (e) { ElMessage.error(errorMessage(e, fallback)); return false } }
async function load(): Promise<void> { await guard(async () => { [layouts.value, stations.value, screens.value] = await Promise.all([hmiLayoutApi.list(), hmiLayoutApi.workstations(), screenApi.list()]) }, '加载失败') }

const visible = ref(false), monitorTab = ref('0')
const form = reactive<{ id?: number; name: string; revision: number; document: HmiLayoutDocument }>({ name: '', revision: 0, document: { monitors: [] } })
function edit(row?: HmiLayout): void {
  Object.assign(form, row ? { id: row.id, name: row.name, revision: row.revision, document: JSON.parse(JSON.stringify(row.document)) } : { id: undefined, name: '', revision: 0, document: { monitors: [] } })
  if (!form.document.monitors.length) preset()
  monitorTab.value = '0'; visible.value = true
}
function preset(): void {
  const s = screens.value[0]?.id ?? null
  form.document.monitors = [
    { name: '总览', columns: 12, rows: 12, regions: [{ id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 12 }, { id: 'nav', kind: 'navigation', row: 2, col: 1, rowSpan: 11, colSpan: 2 }, { id: 'main', kind: 'screen', screenId: s, row: 2, col: 3, rowSpan: 11, colSpan: 10 }] },
    { name: '详情', columns: 12, rows: 12, regions: [{ id: 'detail', kind: 'screen', screenId: s, row: 1, col: 1, rowSpan: 8, colSpan: 12 }, { id: 'alarms', kind: 'alarmList', row: 9, col: 1, rowSpan: 4, colSpan: 12 }] }
  ]
}
function addMonitor(): void { form.document.monitors.push({ name: '', columns: 12, rows: 12, regions: [{ id: 'main', kind: 'screen', screenId: screens.value[0]?.id ?? null, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }); monitorTab.value = String(form.document.monitors.length - 1) }
function addRegion(m: HmiMonitor): void { let n = m.regions.length + 1; while (m.regions.some(r => r.id === `r${n}`)) n++; m.regions.push({ id: `r${n}`, kind: 'screen', screenId: screens.value[0]?.id ?? null, row: 1, col: 1, rowSpan: 1, colSpan: 1 }) }
async function save(): Promise<void> { if (await guard(() => hmiLayoutApi.save({ ...form }), '保存失败')) { ElMessage.success('已保存'); visible.value = false; await load() } }
async function remove(row: HmiLayout): Promise<void> { try { await ElMessageBox.confirm(`删除布局 ${row.name}？分配了它的工作站将不再自动打开布局。`, '删除', { type: 'warning' }) } catch { return } if (await guard(() => hmiLayoutApi.remove(row.id), '删除失败')) await load() }

const stationVisible = ref(false)
const stationForm = reactive<{ id?: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean }>({ name: '', description: null, layoutId: null, allowControl: true })
function editStation(row?: HmiWorkstation): void { Object.assign(stationForm, row ? { ...row } : { id: undefined, name: '', description: null, layoutId: layouts.value[0]?.id ?? null, allowControl: true }); stationVisible.value = true }
async function saveStation(): Promise<void> { if (await guard(() => hmiLayoutApi.saveWorkstation({ ...stationForm }), '保存失败')) { ElMessage.success('已保存'); stationVisible.value = false; await load() } }
async function removeStation(row: HmiWorkstation): Promise<void> { try { await ElMessageBox.confirm(`删除工作站 ${row.name}？`, '删除', { type: 'warning' }) } catch { return } if (await guard(() => hmiLayoutApi.removeWorkstation(row.id), '删除失败')) await load() }
function setLocal(name: string | null): void { if (name) localStorage.setItem(WORKSTATION_KEY, name); else localStorage.removeItem(WORKSTATION_KEY); local.value = name; ElMessage.success(name ? `本机已登记为 ${name}` : '已清除本机登记') }
onMounted(load)
</script>

<style scoped>
.editor { display: flex; gap: 12px; }
.preview { width: 300px; height: 200px; display: grid; gap: 2px; background: #0d1622; padding: 4px; border-radius: 4px; flex-shrink: 0; }
.preview > div { background: #22344d; color: #dce6f0; font-size: 11px; padding: 2px 4px; overflow: hidden; border-radius: 2px; }
.preview small { display: block; color: #8fb3d9; }
.preview .k-alarmBanner { background: #7a1010; } .preview .k-alarmList { background: #5c3b10; } .preview .k-navigation { background: #2c3e50; }
</style>
