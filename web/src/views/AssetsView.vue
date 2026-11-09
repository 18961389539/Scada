<template>
  <div class="page assets-page">
    <div class="panel">
      <div class="toolbar">
        <span class="hint">{{ $t('assets.subtitle') }}</span>
        <span class="spacer" />
        <el-button data-test="assets-refresh" @click="loadAll">{{ $t('assets.refresh') }}</el-button>
      </div>
      <el-alert v-if="catalogError" :title="catalogError" type="error" :closable="false" show-icon data-test="assets-catalog-error" />

      <el-tabs v-model="tab" class="assets-tabs">
        <el-tab-pane :label="$t('assets.tabs.ledger')" name="ledger">
          <div class="toolbar filter-row">
            <el-input v-model="assetSearch" :placeholder="$t('assets.searchAssets')" clearable style="width: 260px"
              data-test="assets-search" @keyup.enter="loadAssets" @clear="loadAssets" />
            <el-select v-model="assetStatus" clearable :placeholder="$t('assets.allStatuses')" style="width: 140px"
              data-test="assets-status" @change="loadAssets">
              <el-option v-for="s in assetStatusValues" :key="s" :label="assetStatusLabels[s]" :value="s" />
            </el-select>
            <el-select v-model="assetArea" clearable :placeholder="$t('assets.allAreas')" style="width: 140px"
              data-test="assets-area" @change="loadAssets">
              <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
            </el-select>
            <el-checkbox v-model="assetEnabledOnly" @change="loadAssets">{{ $t('assets.enabledOnly') }}</el-checkbox>
            <span class="spacer" />
            <el-button v-if="canConfigureAny" type="primary" data-test="asset-add" @click="openAsset()">{{ $t('assets.addAsset') }}</el-button>
          </div>
          <el-alert v-if="assetError" :title="assetError" type="error" :closable="false" show-icon data-test="asset-error" />
          <el-table :data="assets" size="small" v-loading="assetLoading" :empty-text="$t('assets.asset.empty')" data-test="asset-table">
            <el-table-column prop="code" :label="$t('assets.asset.code')" min-width="120" show-overflow-tooltip />
            <el-table-column prop="name" :label="$t('assets.asset.name')" min-width="150" show-overflow-tooltip />
            <el-table-column prop="category" :label="$t('assets.asset.category')" width="110" show-overflow-tooltip />
            <el-table-column prop="model" :label="$t('assets.asset.model')" width="130" show-overflow-tooltip />
            <el-table-column prop="location" :label="$t('assets.asset.location')" width="130" show-overflow-tooltip />
            <el-table-column :label="$t('assets.asset.area')" width="110" show-overflow-tooltip>
              <template #default="{ row }">{{ areaName(row.areaId) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.asset.device')" min-width="140" show-overflow-tooltip>
              <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.asset.status')" width="110">
              <template #default="{ row }">
                <el-tag :type="assetStatusType(row.status)" size="small">{{ assetStatusLabel(row.status) }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('assets.asset.warranty')" width="120">
              <template #default="{ row }">{{ formatDay(row.warrantyUntilTs) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.asset.enabled')" width="80">
              <template #default="{ row }">{{ row.enabled ? $t('assets.yes') : $t('assets.no') }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.actions')" width="150" fixed="right">
              <template #default="{ row }">
                <el-button v-if="canConfigureArea(row.areaId)" link type="primary" data-test="asset-edit" @click="openAsset(row)">{{ $t('assets.edit') }}</el-button>
                <el-popconfirm v-if="canConfigureArea(row.areaId)" :title="$t('assets.confirmDeleteAsset', [row.name])" @confirm="removeAsset(row)">
                  <template #reference><el-button link type="danger">{{ $t('assets.delete') }}</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane :label="$t('assets.tabs.workOrders')" name="workOrders">
          <div class="toolbar filter-row">
            <el-input v-model="orderSearch" :placeholder="$t('assets.searchWorkOrders')" clearable style="width: 240px"
              data-test="wo-search" @keyup.enter="loadOrders" @clear="loadOrders" />
            <el-select v-model="orderStatus" clearable :placeholder="$t('assets.allStatuses')" style="width: 130px"
              data-test="wo-status-filter" @change="loadOrders">
              <el-option v-for="s in workOrderStatusValues" :key="s" :label="workOrderStatusLabels[s]" :value="s" />
            </el-select>
            <el-select v-model="orderType" clearable :placeholder="$t('assets.allTypes')" style="width: 130px"
              data-test="wo-type-filter" @change="loadOrders">
              <el-option v-for="t in workOrderTypeValues" :key="t" :label="workOrderTypeLabels[t]" :value="t" />
            </el-select>
            <el-select v-model="orderPriority" clearable :placeholder="$t('assets.allPriorities')" style="width: 120px"
              data-test="wo-priority-filter" @change="loadOrders">
              <el-option v-for="p in workOrderPriorityValues" :key="p" :label="workOrderPriorityLabels[p]" :value="p" />
            </el-select>
            <el-select v-model="orderArea" clearable :placeholder="$t('assets.allAreas')" style="width: 130px"
              data-test="wo-area-filter" @change="loadOrders">
              <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
            </el-select>
            <span class="spacer" />
            <el-button v-if="canControlAny" type="primary" data-test="wo-add" @click="openOrder()">{{ $t('assets.addWorkOrder') }}</el-button>
          </div>
          <el-alert v-if="orderError" :title="orderError" type="error" :closable="false" show-icon data-test="wo-error" />
          <el-table :data="orders" size="small" v-loading="orderLoading" :empty-text="$t('assets.workOrder.empty')" data-test="wo-table">
            <el-table-column prop="number" :label="$t('assets.workOrder.number')" width="170" show-overflow-tooltip />
            <el-table-column prop="title" :label="$t('assets.workOrder.title')" min-width="160" show-overflow-tooltip />
            <el-table-column :label="$t('assets.workOrder.asset')" min-width="130" show-overflow-tooltip>
              <template #default="{ row }">{{ assetName(row.assetId) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.workOrder.area')" width="105" show-overflow-tooltip>
              <template #default="{ row }">{{ areaName(row.areaId) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.workOrder.type')" width="105">
              <template #default="{ row }">{{ workOrderTypeLabel(row.type) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.workOrder.priority')" width="90">
              <template #default="{ row }">
                <el-tag :type="priorityType(row.priority)" size="small">{{ workOrderPriorityLabel(row.priority) }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('assets.workOrder.status')" width="100">
              <template #default="{ row }">
                <el-tag :type="orderStatusType(row.status)" size="small">{{ workOrderStatusLabel(row.status) }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="assignee" :label="$t('assets.workOrder.assignee')" width="110" show-overflow-tooltip />
            <el-table-column :label="$t('assets.workOrder.createdAt')" width="150">
              <template #default="{ row }">{{ formatTime(row.createdTs) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.actions')" width="300" fixed="right">
              <template #default="{ row }">
                <el-button link data-test="wo-parts" @click="openOrderParts(row)">{{ $t('assets.workOrder.parts') }}</el-button>
                <template v-if="canControlArea(row.areaId)">
                  <el-button v-if="row.status === 'Open'" link type="primary" data-test="wo-start" @click="setOrderStatus(row, 'InProgress')">{{ $t('assets.workOrder.start') }}</el-button>
                  <el-button v-if="row.status === 'InProgress'" link type="warning" data-test="wo-hold" @click="setOrderStatus(row, 'OnHold')">{{ $t('assets.workOrder.hold') }}</el-button>
                  <el-button v-if="row.status === 'OnHold'" link type="primary" data-test="wo-resume" @click="setOrderStatus(row, 'InProgress')">{{ $t('assets.workOrder.resume') }}</el-button>
                  <el-button v-if="row.status === 'InProgress' || row.status === 'OnHold'" link type="success" data-test="wo-complete" @click="openComplete(row)">{{ $t('assets.workOrder.complete') }}</el-button>
                  <el-button v-if="row.status !== 'Completed' && row.status !== 'Cancelled'" link type="danger" data-test="wo-cancel" @click="cancelOrder(row)">{{ $t('assets.workOrder.cancelOrder') }}</el-button>
                </template>
                <el-button v-if="canConfigureArea(row.areaId) && row.status !== 'Completed' && row.status !== 'Cancelled'" link type="primary" data-test="wo-edit" @click="openOrder(row)">{{ $t('assets.edit') }}</el-button>
                <el-popconfirm v-if="canConfigureArea(row.areaId) && (row.status === 'Open' || row.status === 'Cancelled')"
                  :title="$t('assets.confirmDeleteWorkOrder', [row.number])" @confirm="removeOrder(row)">
                  <template #reference><el-button link type="danger">{{ $t('assets.delete') }}</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane :label="$t('assets.tabs.spareParts')" name="spareParts">
          <div class="toolbar filter-row">
            <el-input v-model="partSearch" :placeholder="$t('assets.searchParts')" clearable style="width: 240px"
              data-test="part-search" @keyup.enter="loadParts" @clear="loadParts" />
            <el-checkbox v-model="partLowStock" data-test="part-low-stock-filter" @change="loadParts">{{ $t('assets.lowStockOnly') }}</el-checkbox>
            <el-checkbox v-model="partEnabledOnly" @change="loadParts">{{ $t('assets.enabledOnly') }}</el-checkbox>
            <span class="spacer" />
            <el-button v-if="canConfigureAny" type="primary" data-test="part-add" @click="openPart()">{{ $t('assets.addPart') }}</el-button>
          </div>
          <el-alert v-if="partError" :title="partError" type="error" :closable="false" show-icon data-test="part-error" />
          <el-table :data="parts" size="small" v-loading="partLoading" :empty-text="$t('assets.part.empty')" data-test="part-table">
            <el-table-column prop="code" :label="$t('assets.part.code')" min-width="120" show-overflow-tooltip />
            <el-table-column prop="name" :label="$t('assets.part.name')" min-width="150" show-overflow-tooltip />
            <el-table-column prop="specification" :label="$t('assets.part.specification')" min-width="130" show-overflow-tooltip />
            <el-table-column prop="unit" :label="$t('assets.part.unit')" width="70" />
            <el-table-column :label="$t('assets.part.stock')" width="130">
              <template #default="{ row }">
                <span :class="{ 'low-stock': row.stock < row.minStock }">{{ formatQty(row.stock) }}</span>
                <el-tag v-if="row.stock < row.minStock" type="danger" size="small" class="low-stock-tag">{{ $t('assets.part.lowStock') }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('assets.part.minStock')" width="100">
              <template #default="{ row }">{{ formatQty(row.minStock) }}</template>
            </el-table-column>
            <el-table-column prop="location" :label="$t('assets.part.location')" width="110" show-overflow-tooltip />
            <el-table-column prop="supplier" :label="$t('assets.part.supplier')" width="120" show-overflow-tooltip />
            <el-table-column :label="$t('assets.part.unitPrice')" width="100">
              <template #default="{ row }">{{ row.unitPrice == null ? '-' : formatQty(row.unitPrice) }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.part.enabled')" width="80">
              <template #default="{ row }">{{ row.enabled ? $t('assets.yes') : $t('assets.no') }}</template>
            </el-table-column>
            <el-table-column :label="$t('assets.actions')" width="270" fixed="right">
              <template #default="{ row }">
                <el-button link data-test="part-movements" @click="openPartMovements(row)">{{ $t('assets.part.movements') }}</el-button>
                <el-button v-if="canControlAny" link type="primary" data-test="part-in" @click="openMovement(row, 'In')">{{ $t('assets.part.stockIn') }}</el-button>
                <el-button v-if="canControlAny" link type="warning" data-test="part-out" @click="openMovement(row, 'Out')">{{ $t('assets.part.stockOut') }}</el-button>
                <el-button v-if="canConfigureAny" link type="primary" data-test="part-edit" @click="openPart(row)">{{ $t('assets.edit') }}</el-button>
                <el-popconfirm v-if="canConfigureAny" :title="$t('assets.confirmDeletePart', [row.name])" @confirm="removePart(row)">
                  <template #reference><el-button link type="danger">{{ $t('assets.delete') }}</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="assetDialog" :title="assetForm.id ? $t('assets.asset.editTitle') : $t('assets.asset.addTitle')" width="760px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="asset-save-error" />
      <el-form label-width="110px" size="small">
        <el-row :gutter="12">
          <el-col :span="12"><el-form-item :label="$t('assets.asset.code')"><el-input v-model="assetForm.code" data-test="asset-code" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.asset.name')"><el-input v-model="assetForm.name" data-test="asset-name" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.asset.category')"><el-input v-model="assetForm.category" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.asset.model')"><el-input v-model="assetForm.model" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.asset.manufacturer')"><el-input v-model="assetForm.manufacturer" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.asset.serialNumber')"><el-input v-model="assetForm.serialNumber" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.asset.location')"><el-input v-model="assetForm.location" /></el-form-item></el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.asset.area')">
              <el-select v-model="assetForm.areaId" style="width: 100%" data-test="asset-area" @change="onAssetAreaChange">
                <el-option v-for="a in configurableAreas" :key="a.id" :label="a.name" :value="a.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.asset.device')">
              <el-select v-model="assetForm.deviceId" clearable filterable :placeholder="$t('assets.none')" style="width: 100%" @change="onAssetDeviceChange">
                <el-option v-for="d in assetDeviceOptions" :key="d.id" :label="d.name" :value="d.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.asset.status')">
              <el-select v-model="assetForm.status" style="width: 100%">
                <el-option v-for="s in assetStatusValues" :key="s" :label="assetStatusLabels[s]" :value="s" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.asset.commissioned')">
              <el-date-picker v-model="assetForm.commissioned" type="date" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.asset.warranty')">
              <el-date-picker v-model="assetForm.warranty" type="date" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.asset.enabled')"><el-switch v-model="assetForm.enabled" /></el-form-item></el-col>
          <el-col :span="24">
            <el-form-item :label="$t('assets.asset.description')"><el-input v-model="assetForm.description" type="textarea" :rows="2" /></el-form-item>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="assetDialog = false">{{ $t('assets.cancel') }}</el-button>
        <el-button type="primary" :loading="assetSaving" data-test="asset-save" @click="saveAsset">{{ $t('assets.save') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="orderDialog" :title="orderForm.id ? $t('assets.workOrder.editTitle') : $t('assets.workOrder.addTitle')" width="760px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="wo-save-error" />
      <el-form label-width="110px" size="small">
        <el-form-item :label="$t('assets.workOrder.title')"><el-input v-model="orderForm.title" data-test="wo-title" /></el-form-item>
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item :label="$t('assets.workOrder.asset')">
              <el-select v-model="orderForm.assetId" clearable filterable :placeholder="$t('assets.none')" style="width: 100%" data-test="wo-asset" @change="onOrderAssetChange">
                <el-option v-for="a in orderAssetOptions" :key="a.id" :label="`${a.code} ${a.name}`" :value="a.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.asset.device')">
              <el-select v-model="orderForm.deviceId" clearable filterable :placeholder="$t('assets.none')" style="width: 100%" @change="onOrderDeviceChange">
                <el-option v-for="d in orderDeviceOptions" :key="d.id" :label="d.name" :value="d.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.workOrder.area')">
              <el-select v-model="orderForm.areaId" style="width: 100%" data-test="wo-area" @change="onOrderAreaChange">
                <el-option v-for="a in orderAreaOptions" :key="a.id" :label="a.name" :value="a.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.workOrder.type')">
              <el-select v-model="orderForm.type" style="width: 100%">
                <el-option v-for="t in workOrderTypeValues" :key="t" :label="workOrderTypeLabels[t]" :value="t" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.workOrder.priority')">
              <el-select v-model="orderForm.priority" style="width: 100%">
                <el-option v-for="p in workOrderPriorityValues" :key="p" :label="workOrderPriorityLabels[p]" :value="p" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.workOrder.assignee')"><el-input v-model="orderForm.assignee" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.workOrder.reporter')"><el-input v-model="orderForm.reporter" /></el-form-item></el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.workOrder.plannedStart')">
              <el-date-picker v-model="orderForm.plannedStart" type="datetime" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('assets.workOrder.plannedEnd')">
              <el-date-picker v-model="orderForm.plannedEnd" type="datetime" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="24">
            <el-form-item :label="$t('assets.workOrder.description')"><el-input v-model="orderForm.description" type="textarea" :rows="3" /></el-form-item>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="orderDialog = false">{{ $t('assets.cancel') }}</el-button>
        <el-button type="primary" :loading="orderSaving" data-test="wo-save" @click="saveOrder">{{ $t('assets.save') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="completeDialog" :title="$t('assets.workOrder.completeTitle', [completeTarget?.number ?? ''])" width="560px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="wo-complete-error" />
      <el-form label-width="110px" size="small">
        <el-form-item :label="$t('assets.workOrder.resolution')">
          <el-input v-model="completeForm.resolution" type="textarea" :rows="4" data-test="wo-resolution" />
        </el-form-item>
        <el-form-item :label="$t('assets.workOrder.downtime')">
          <el-input-number v-model="completeForm.downtimeMinutes" :min="0" :max="100000" data-test="wo-downtime" />
        </el-form-item>
        <div class="hint">{{ $t('assets.workOrder.downtimeHint') }}</div>
      </el-form>
      <template #footer>
        <el-button @click="completeDialog = false">{{ $t('assets.cancel') }}</el-button>
        <el-button type="primary" :loading="statusSaving" data-test="wo-complete-submit" @click="submitComplete">{{ $t('assets.workOrder.complete') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="orderPartsDialog" :title="$t('assets.workOrder.partsTitle', [orderPartsTarget?.number ?? ''])" width="760px" destroy-on-close>
      <el-alert v-if="partsError" :title="partsError" type="error" :closable="false" show-icon data-test="wo-parts-error" />
      <div v-if="orderPartsTarget && canControlArea(orderPartsTarget.areaId) && !isClosed(orderPartsTarget.status)" class="toolbar filter-row">
        <el-select v-model="issueForm.partId" filterable :placeholder="$t('assets.workOrder.part')" style="width: 240px" data-test="wo-part-select">
          <el-option v-for="p in enabledParts" :key="p.id" :label="`${p.code} ${p.name} (${formatQty(p.stock)} ${p.unit})`" :value="p.id" />
        </el-select>
        <el-input-number v-model="issueForm.quantity" :min="0.001" :step="1" data-test="wo-part-qty" />
        <el-input v-model="issueForm.note" :placeholder="$t('assets.workOrder.note')" style="width: 180px" />
        <el-button type="primary" :loading="issueSaving" data-test="wo-part-issue" @click="submitIssuePart">{{ $t('assets.workOrder.issuePart') }}</el-button>
      </div>
      <el-table :data="orderParts" size="small" :empty-text="$t('assets.workOrder.partEmpty')" data-test="wo-parts-table">
        <el-table-column :label="$t('assets.part.time')" width="150">
          <template #default="{ row }">{{ formatTime(row.ts) }}</template>
        </el-table-column>
        <el-table-column :label="$t('assets.workOrder.part')" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">{{ partName(row.partId) }}</template>
        </el-table-column>
        <el-table-column :label="$t('assets.workOrder.quantity')" width="100">
          <template #default="{ row }">{{ formatQty(row.quantity) }}</template>
        </el-table-column>
        <el-table-column :label="$t('assets.part.balanceAfter')" width="100">
          <template #default="{ row }">{{ formatQty(row.balanceAfter) }}</template>
        </el-table-column>
        <el-table-column prop="operator" :label="$t('assets.part.operator')" width="110" show-overflow-tooltip />
        <el-table-column prop="note" :label="$t('assets.part.note')" min-width="120" show-overflow-tooltip />
      </el-table>
    </el-dialog>

    <el-dialog v-model="partDialog" :title="partForm.id ? $t('assets.part.editTitle') : $t('assets.part.addTitle')" width="680px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="part-save-error" />
      <el-form label-width="100px" size="small">
        <el-row :gutter="12">
          <el-col :span="12"><el-form-item :label="$t('assets.part.code')"><el-input v-model="partForm.code" data-test="part-code" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.name')"><el-input v-model="partForm.name" data-test="part-name" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.specification')"><el-input v-model="partForm.specification" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.unit')"><el-input v-model="partForm.unit" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.stock')"><el-input-number v-model="partForm.stock" :min="0" style="width: 100%" data-test="part-stock" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.minStock')"><el-input-number v-model="partForm.minStock" :min="0" style="width: 100%" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.location')"><el-input v-model="partForm.location" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.supplier')"><el-input v-model="partForm.supplier" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.unitPrice')"><el-input-number v-model="partForm.unitPrice" :min="0" :precision="2" style="width: 100%" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('assets.part.enabled')"><el-switch v-model="partForm.enabled" /></el-form-item></el-col>
          <el-col :span="24"><el-form-item :label="$t('assets.part.description')"><el-input v-model="partForm.description" type="textarea" :rows="2" /></el-form-item></el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="partDialog = false">{{ $t('assets.cancel') }}</el-button>
        <el-button type="primary" :loading="partSaving" data-test="part-save" @click="savePart">{{ $t('assets.save') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="movementDialog" :title="$t('assets.part.movementTitle')" width="520px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="part-movement-error" />
      <el-form label-width="110px" size="small">
        <el-form-item :label="$t('assets.part.movementType')">
          <el-radio-group v-model="movementForm.type" data-test="part-movement-type">
            <el-radio-button v-for="t in movementTypeValues" :key="t" :value="t">{{ movementTypeLabel(t) }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="movementForm.type === 'Adjust' ? $t('assets.part.adjustQuantity') : $t('assets.part.quantity')">
          <el-input-number v-model="movementForm.quantity" :min="movementForm.type === 'Adjust' ? 0 : 0.001" :step="1" data-test="part-movement-qty" />
        </el-form-item>
        <el-form-item v-if="movementForm.type === 'Out'" :label="$t('assets.part.workOrder')">
          <el-select v-model="movementForm.workOrderId" clearable :placeholder="$t('assets.none')" style="width: 100%">
            <el-option v-for="o in openOrders" :key="o.id" :label="`${o.number} ${o.title}`" :value="o.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('assets.part.note')"><el-input v-model="movementForm.note" type="textarea" :rows="2" /></el-form-item>
        <div v-if="movementForm.type === 'Adjust'" class="hint">{{ $t('assets.part.adjustHint') }}</div>
      </el-form>
      <template #footer>
        <el-button @click="movementDialog = false">{{ $t('assets.cancel') }}</el-button>
        <el-button type="primary" :loading="movementSaving" data-test="part-movement-save" @click="submitMovement">{{ $t('assets.save') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="movementsDialog" :title="$t('assets.part.movementsTitle', [movementsTarget?.name ?? ''])" width="760px" destroy-on-close>
      <el-alert v-if="partsError" :title="partsError" type="error" :closable="false" show-icon data-test="part-movements-error" />
      <el-table :data="partMovements" size="small" :empty-text="$t('assets.part.noMovements')" data-test="part-movements-table">
        <el-table-column :label="$t('assets.part.time')" width="150">
          <template #default="{ row }">{{ formatTime(row.ts) }}</template>
        </el-table-column>
        <el-table-column :label="$t('assets.part.movementType')" width="100">
          <template #default="{ row }">{{ movementTypeLabel(row.type) }}</template>
        </el-table-column>
        <el-table-column :label="$t('assets.part.quantity')" width="100">
          <template #default="{ row }">{{ formatQty(row.quantity) }}</template>
        </el-table-column>
        <el-table-column :label="$t('assets.part.balanceAfter')" width="100">
          <template #default="{ row }">{{ formatQty(row.balanceAfter) }}</template>
        </el-table-column>
        <el-table-column :label="$t('assets.part.workOrder')" width="150" show-overflow-tooltip>
          <template #default="{ row }">{{ orderNumber(row.workOrderId) }}</template>
        </el-table-column>
        <el-table-column prop="operator" :label="$t('assets.part.operator')" width="110" show-overflow-tooltip />
        <el-table-column prop="note" :label="$t('assets.part.note')" min-width="120" show-overflow-tooltip />
      </el-table>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { areaApi, assetApi, deviceApi, sparePartApi, workOrderApi } from '../api'
import { errorMessage } from '../api/http'
import { can } from '../access/policies'
import { currentLocale, tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { formatTime } from '../utils'
import type {
  Area, Asset, AssetInput, AssetStatus, Device, MaintenanceWorkOrder, SparePart, SparePartInput,
  SparePartMovement, SparePartMovementType, WorkOrderInput, WorkOrderPriority, WorkOrderStatus, WorkOrderType
} from '../types'

const auth = useAuthStore()
const tab = ref('ledger')
const areas = ref<Area[]>([])
const devices = ref<Device[]>([])
const assets = ref<Asset[]>([])
const orders = ref<MaintenanceWorkOrder[]>([])
const parts = ref<SparePart[]>([])

const catalogError = ref('')
const assetError = ref('')
const orderError = ref('')
const partError = ref('')
const dialogError = ref('')
const partsError = ref('')

const assetLoading = ref(false), orderLoading = ref(false), partLoading = ref(false)
const assetSaving = ref(false), orderSaving = ref(false), partSaving = ref(false)
const statusSaving = ref(false), issueSaving = ref(false), movementSaving = ref(false)

const assetSearch = ref(''), assetStatus = ref<AssetStatus | ''>(''), assetArea = ref<number | null>(null), assetEnabledOnly = ref(false)
const orderSearch = ref(''), orderStatus = ref<WorkOrderStatus | ''>(''), orderType = ref<WorkOrderType | ''>('')
const orderPriority = ref<WorkOrderPriority | ''>(''), orderArea = ref<number | null>(null)
const partSearch = ref(''), partLowStock = ref(false), partEnabledOnly = ref(false)

const assetStatusValues: AssetStatus[] = ['InService', 'Standby', 'Maintenance', 'Retired']
const workOrderStatusValues: WorkOrderStatus[] = ['Open', 'InProgress', 'OnHold', 'Completed', 'Cancelled']
const workOrderTypeValues: WorkOrderType[] = ['Corrective', 'Preventive', 'Inspection', 'Improvement']
const workOrderPriorityValues: WorkOrderPriority[] = ['Low', 'Normal', 'High', 'Urgent']
const movementTypeValues: SparePartMovementType[] = ['In', 'Out', 'Adjust']
const assetStatusLabels: Record<AssetStatus, string> = {
  InService: tr('assets.status.InService'), Standby: tr('assets.status.Standby'),
  Maintenance: tr('assets.status.Maintenance'), Retired: tr('assets.status.Retired')
}
const workOrderStatusLabels: Record<WorkOrderStatus, string> = {
  Open: tr('assets.workOrderStatus.Open'), InProgress: tr('assets.workOrderStatus.InProgress'),
  OnHold: tr('assets.workOrderStatus.OnHold'), Completed: tr('assets.workOrderStatus.Completed'),
  Cancelled: tr('assets.workOrderStatus.Cancelled')
}
const workOrderTypeLabels: Record<WorkOrderType, string> = {
  Corrective: tr('assets.type.Corrective'), Preventive: tr('assets.type.Preventive'),
  Inspection: tr('assets.type.Inspection'), Improvement: tr('assets.type.Improvement')
}
const workOrderPriorityLabels: Record<WorkOrderPriority, string> = {
  Low: tr('assets.priority.Low'), Normal: tr('assets.priority.Normal'),
  High: tr('assets.priority.High'), Urgent: tr('assets.priority.Urgent')
}

const canConfigureAny = computed(() => can(auth.user, 'Configure'))
const canControlAny = computed(() => can(auth.user, 'Control'))
const configurableAreas = computed(() => areas.value.filter(a => can(auth.user, 'Configure', a.id)))
const controlAreas = computed(() => areas.value.filter(a => can(auth.user, 'Control', a.id)))
const canConfigureArea = (areaId: number) => can(auth.user, 'Configure', areaId)
const canControlArea = (areaId: number) => can(auth.user, 'Control', areaId)
const isClosed = (status: WorkOrderStatus) => status === 'Completed' || status === 'Cancelled'

const assetDialog = ref(false)
const orderDialog = ref(false)
const completeDialog = ref(false)
const orderPartsDialog = ref(false)
const partDialog = ref(false)
const movementDialog = ref(false)
const movementsDialog = ref(false)

const completeTarget = ref<MaintenanceWorkOrder | null>(null)
const orderPartsTarget = ref<MaintenanceWorkOrder | null>(null)
const movementTarget = ref<SparePart | null>(null)
const movementsTarget = ref<SparePart | null>(null)
const orderParts = ref<SparePartMovement[]>([])
const partMovements = ref<SparePartMovement[]>([])

const assetForm = reactive({
  id: 0,
  code: '', name: '', category: '', model: '', manufacturer: '', serialNumber: '', location: '',
  areaId: null as number | null,
  deviceId: null as number | null,
  status: 'InService' as AssetStatus,
  commissioned: null as Date | null,
  warranty: null as Date | null,
  description: '',
  enabled: true
})

const orderForm = reactive({
  id: 0,
  title: '',
  assetId: null as number | null,
  deviceId: null as number | null,
  areaId: null as number | null,
  type: 'Corrective' as WorkOrderType,
  priority: 'Normal' as WorkOrderPriority,
  description: '',
  assignee: '',
  reporter: '',
  plannedStart: null as Date | null,
  plannedEnd: null as Date | null
})

const completeForm = reactive({ resolution: '', downtimeMinutes: 0 })
const issueForm = reactive({ partId: null as number | null, quantity: 1, note: '' })
const partForm = reactive({
  id: 0,
  code: '', name: '', specification: '', unit: '', stock: 0, minStock: 0, location: '', supplier: '',
  unitPrice: null as number | null, description: '', enabled: true
})
const movementForm = reactive({
  type: 'In' as SparePartMovementType,
  quantity: 1,
  note: '',
  workOrderId: null as number | null
})

const orderAreaOptions = computed(() => areas.value.filter(a =>
  orderForm.id ? (canControlArea(a.id) || canConfigureArea(a.id)) : canControlArea(a.id)))
const orderAssetOptions = computed(() => assets.value.filter(a =>
  (canControlArea(a.areaId) || canConfigureArea(a.areaId)) && (!orderForm.areaId || a.areaId === orderForm.areaId)))
const orderDeviceOptions = computed(() => devices.value.filter(d => {
  const areaId = d.areaId ?? 0
  return (canControlArea(areaId) || canConfigureArea(areaId)) && (!orderForm.areaId || areaId === orderForm.areaId)
}))
const assetDeviceOptions = computed(() => devices.value.filter(d => {
  const areaId = d.areaId ?? 0
  return canConfigureArea(areaId) && (!assetForm.areaId || areaId === assetForm.areaId)
}))
const enabledParts = computed(() => parts.value.filter(p => p.enabled))
const openOrders = computed(() => orders.value.filter(o => !isClosed(o.status)))

function areaName(id?: number | null): string {
  if (id == null) return '-'
  return areas.value.find(a => a.id === id)?.name ?? `#${id}`
}
function deviceName(id?: number | null): string {
  if (id == null) return '-'
  return devices.value.find(d => d.id === id)?.name ?? `#${id}`
}
function assetName(id?: number | null): string {
  if (id == null) return '-'
  const asset = assets.value.find(a => a.id === id)
  return asset ? `${asset.code} ${asset.name}` : `#${id}`
}
function partName(id: number): string {
  const part = parts.value.find(p => p.id === id)
  return part ? `${part.code} ${part.name}` : `#${id}`
}
function orderNumber(id?: number | null): string {
  if (id == null) return '-'
  return orders.value.find(o => o.id === id)?.number ?? `#${id}`
}
function formatDay(ms?: number | null): string {
  if (!ms) return '-'
  return new Date(ms).toLocaleDateString(currentLocale())
}
function formatQty(value: number): string {
  if (!Number.isFinite(value)) return '-'
  return value.toLocaleString(currentLocale(), { maximumFractionDigits: 3 })
}
function assetStatusLabel(status: AssetStatus): string { return assetStatusLabels[status] }
function workOrderStatusLabel(status: WorkOrderStatus): string { return workOrderStatusLabels[status] }
function workOrderTypeLabel(type: WorkOrderType): string { return workOrderTypeLabels[type] }
function workOrderPriorityLabel(priority: WorkOrderPriority): string { return workOrderPriorityLabels[priority] }
function movementTypeLabel(type: SparePartMovementType): string {
  if (type === 'In') return tr('assets.part.stockIn')
  if (type === 'Out') return tr('assets.part.stockOut')
  return tr('assets.part.adjust')
}
function assetStatusType(status: AssetStatus): string {
  if (status === 'InService') return 'success'
  if (status === 'Maintenance') return 'warning'
  if (status === 'Retired') return 'danger'
  return 'info'
}
function orderStatusType(status: WorkOrderStatus): string {
  if (status === 'Completed') return 'success'
  if (status === 'InProgress') return 'warning'
  if (status === 'OnHold') return 'warning'
  if (status === 'Cancelled') return 'info'
  return 'info'
}
function priorityType(priority: WorkOrderPriority): string {
  if (priority === 'Urgent') return 'danger'
  if (priority === 'High') return 'warning'
  if (priority === 'Low') return 'info'
  return 'info'
}

async function loadAssets() {
  assetLoading.value = true
  try {
    assets.value = await assetApi.list({
      search: assetSearch.value.trim() || undefined,
      status: assetStatus.value || undefined,
      areaId: assetArea.value ?? undefined,
      enabled: assetEnabledOnly.value || undefined
    })
    assetError.value = ''
  } catch (e) {
    assetError.value = errorMessage(e, tr('assets.loadFailed'))
  } finally {
    assetLoading.value = false
  }
}
async function loadOrders() {
  orderLoading.value = true
  try {
    orders.value = await workOrderApi.list({
      search: orderSearch.value.trim() || undefined,
      status: orderStatus.value || undefined,
      type: orderType.value || undefined,
      priority: orderPriority.value || undefined,
      areaId: orderArea.value ?? undefined
    })
    orderError.value = ''
  } catch (e) {
    orderError.value = errorMessage(e, tr('assets.loadFailed'))
  } finally {
    orderLoading.value = false
  }
}
async function loadParts() {
  partLoading.value = true
  try {
    parts.value = await sparePartApi.list({
      search: partSearch.value.trim() || undefined,
      lowStock: partLowStock.value || undefined,
      enabled: partEnabledOnly.value || undefined
    })
    partError.value = ''
  } catch (e) {
    partError.value = errorMessage(e, tr('assets.loadFailed'))
  } finally {
    partLoading.value = false
  }
}
async function loadAll() {
  await Promise.all([loadAssets(), loadOrders(), loadParts()])
}

function defaultArea(list: Area[]): number | null {
  return list[0]?.id ?? null
}

function openAsset(row?: Asset) {
  assetForm.id = row?.id ?? 0
  assetForm.code = row?.code ?? ''
  assetForm.name = row?.name ?? ''
  assetForm.category = row?.category ?? ''
  assetForm.model = row?.model ?? ''
  assetForm.manufacturer = row?.manufacturer ?? ''
  assetForm.serialNumber = row?.serialNumber ?? ''
  assetForm.location = row?.location ?? ''
  assetForm.areaId = row?.areaId ?? defaultArea(configurableAreas.value)
  assetForm.deviceId = row?.deviceId ?? null
  assetForm.status = row?.status ?? 'InService'
  assetForm.commissioned = row?.commissionedTs ? new Date(row.commissionedTs) : null
  assetForm.warranty = row?.warrantyUntilTs ? new Date(row.warrantyUntilTs) : null
  assetForm.description = row?.description ?? ''
  assetForm.enabled = row?.enabled ?? true
  dialogError.value = ''
  assetDialog.value = true
}
function onAssetAreaChange() {
  if (assetForm.deviceId != null) {
    const device = devices.value.find(d => d.id === assetForm.deviceId)
    if (device && device.areaId !== assetForm.areaId) assetForm.deviceId = null
  }
}
function onAssetDeviceChange(id: number | null) {
  if (id == null) return
  const device = devices.value.find(d => d.id === id)
  if (device?.areaId != null) assetForm.areaId = device.areaId
}
async function saveAsset() {
  assetSaving.value = true
  dialogError.value = ''
  try {
    const areaId = assetForm.areaId ?? defaultArea(configurableAreas.value)
    if (areaId == null) {
      dialogError.value = tr('assets.noAreas')
      return
    }
    const input: AssetInput = {
      code: assetForm.code.trim(), name: assetForm.name.trim(), category: assetForm.category.trim(),
      model: assetForm.model.trim(), manufacturer: assetForm.manufacturer.trim(), serialNumber: assetForm.serialNumber.trim(),
      location: assetForm.location.trim(), areaId, deviceId: assetForm.deviceId, status: assetForm.status,
      commissionedTs: assetForm.commissioned?.getTime() ?? null, warrantyUntilTs: assetForm.warranty?.getTime() ?? null,
      description: assetForm.description.trim(), enabled: assetForm.enabled
    }
    if (assetForm.id) await assetApi.update(assetForm.id, input)
    else await assetApi.create(input)
    assetDialog.value = false
    await loadAssets()
    ElMessage.success(tr('assets.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('assets.saveFailed'))
  } finally {
    assetSaving.value = false
  }
}
async function removeAsset(row: Asset) {
  assetError.value = ''
  try {
    await assetApi.remove(row.id)
    await loadAssets()
    ElMessage.success(tr('assets.deleted'))
  } catch (e) {
    assetError.value = errorMessage(e, tr('assets.deleteFailed'))
  }
}

function openOrder(row?: MaintenanceWorkOrder) {
  orderForm.id = row?.id ?? 0
  orderForm.title = row?.title ?? ''
  orderForm.assetId = row?.assetId ?? null
  orderForm.deviceId = row?.deviceId ?? null
  orderForm.areaId = row?.areaId ?? defaultArea(controlAreas.value)
  orderForm.type = row?.type ?? 'Corrective'
  orderForm.priority = row?.priority ?? 'Normal'
  orderForm.description = row?.description ?? ''
  orderForm.assignee = row?.assignee ?? ''
  orderForm.reporter = row?.reporter ?? ''
  orderForm.plannedStart = row?.plannedStartTs ? new Date(row.plannedStartTs) : null
  orderForm.plannedEnd = row?.plannedEndTs ? new Date(row.plannedEndTs) : null
  dialogError.value = ''
  orderDialog.value = true
}
function onOrderAreaChange() {
  const asset = assets.value.find(a => a.id === orderForm.assetId)
  if (asset && asset.areaId !== orderForm.areaId) orderForm.assetId = null
  const device = devices.value.find(d => d.id === orderForm.deviceId)
  if (device && device.areaId !== orderForm.areaId) orderForm.deviceId = null
}
function onOrderAssetChange(id: number | null) {
  if (id == null) return
  const asset = assets.value.find(a => a.id === id)
  if (!asset) return
  orderForm.areaId = asset.areaId
  if (asset.deviceId != null) orderForm.deviceId = asset.deviceId
}
function onOrderDeviceChange(id: number | null) {
  if (id == null) return
  const device = devices.value.find(d => d.id === id)
  if (device?.areaId != null) orderForm.areaId = device.areaId
}
async function saveOrder() {
  orderSaving.value = true
  dialogError.value = ''
  try {
    const areaId = orderForm.areaId ?? defaultArea(controlAreas.value)
    if (areaId == null) {
      dialogError.value = tr('assets.noControlAreas')
      return
    }
    const input: WorkOrderInput = {
      title: orderForm.title.trim(), assetId: orderForm.assetId, deviceId: orderForm.deviceId, areaId,
      type: orderForm.type, priority: orderForm.priority, description: orderForm.description.trim(),
      assignee: orderForm.assignee.trim(), reporter: orderForm.reporter.trim(),
      plannedStartTs: orderForm.plannedStart?.getTime() ?? null, plannedEndTs: orderForm.plannedEnd?.getTime() ?? null
    }
    if (orderForm.id) await workOrderApi.update(orderForm.id, input)
    else await workOrderApi.create(input)
    orderDialog.value = false
    await Promise.all([loadOrders(), loadAssets()])
    ElMessage.success(tr('assets.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('assets.saveFailed'))
  } finally {
    orderSaving.value = false
  }
}
async function setOrderStatus(row: MaintenanceWorkOrder, status: WorkOrderStatus) {
  orderError.value = ''
  try {
    await workOrderApi.setStatus(row.id, { status })
    await Promise.all([loadOrders(), loadAssets()])
  } catch (e) {
    orderError.value = errorMessage(e, tr('assets.actionFailed'))
  }
}
async function cancelOrder(row: MaintenanceWorkOrder) {
  try {
    await ElMessageBox.confirm(tr('assets.workOrder.confirmCancel', [row.number]), tr('assets.workOrder.cancelOrder'), { type: 'warning' })
  } catch {
    return
  }
  await setOrderStatus(row, 'Cancelled')
}
function openComplete(row: MaintenanceWorkOrder) {
  completeTarget.value = row
  completeForm.resolution = ''
  completeForm.downtimeMinutes = 0
  dialogError.value = ''
  completeDialog.value = true
}
async function submitComplete() {
  if (!completeTarget.value) return
  if (!completeForm.resolution.trim()) {
    dialogError.value = tr('assets.workOrder.resolutionRequired')
    return
  }
  statusSaving.value = true
  dialogError.value = ''
  try {
    await workOrderApi.setStatus(completeTarget.value.id, {
      status: 'Completed', resolution: completeForm.resolution.trim(), downtimeMinutes: completeForm.downtimeMinutes
    })
    completeDialog.value = false
    await Promise.all([loadOrders(), loadAssets()])
    ElMessage.success(tr('assets.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('assets.actionFailed'))
  } finally {
    statusSaving.value = false
  }
}
async function removeOrder(row: MaintenanceWorkOrder) {
  orderError.value = ''
  try {
    await workOrderApi.remove(row.id)
    await loadOrders()
    ElMessage.success(tr('assets.deleted'))
  } catch (e) {
    orderError.value = errorMessage(e, tr('assets.deleteFailed'))
  }
}
let detailSeq = 0
async function openOrderParts(row: MaintenanceWorkOrder) {
  const seq = ++detailSeq
  orderPartsTarget.value = row
  orderParts.value = []
  issueForm.partId = null
  issueForm.quantity = 1
  issueForm.note = ''
  partsError.value = ''
  orderPartsDialog.value = true
  try {
    const parts = await workOrderApi.parts(row.id)
    if (seq !== detailSeq || orderPartsTarget.value?.id !== row.id) return
    orderParts.value = parts
  } catch (e) {
    if (seq !== detailSeq || orderPartsTarget.value?.id !== row.id) return
    partsError.value = errorMessage(e, tr('assets.loadFailed'))
  }
}
async function submitIssuePart() {
  if (!orderPartsTarget.value) return
  if (issueForm.partId == null || !Number.isFinite(issueForm.quantity) || issueForm.quantity <= 0) {
    partsError.value = tr('assets.part.quantityHint')
    return
  }
  issueSaving.value = true
  partsError.value = ''
  try {
    await workOrderApi.issuePart(orderPartsTarget.value.id, {
      partId: issueForm.partId, quantity: issueForm.quantity, note: issueForm.note.trim()
    })
    orderParts.value = await workOrderApi.parts(orderPartsTarget.value.id)
    await loadParts()
    issueForm.quantity = 1
    issueForm.note = ''
    ElMessage.success(tr('assets.saved'))
  } catch (e) {
    partsError.value = errorMessage(e, tr('assets.actionFailed'))
  } finally {
    issueSaving.value = false
  }
}

function openPart(row?: SparePart) {
  partForm.id = row?.id ?? 0
  partForm.code = row?.code ?? ''
  partForm.name = row?.name ?? ''
  partForm.specification = row?.specification ?? ''
  partForm.unit = row?.unit ?? ''
  partForm.stock = row?.stock ?? 0
  partForm.minStock = row?.minStock ?? 0
  partForm.location = row?.location ?? ''
  partForm.supplier = row?.supplier ?? ''
  partForm.unitPrice = row?.unitPrice ?? null
  partForm.description = row?.description ?? ''
  partForm.enabled = row?.enabled ?? true
  dialogError.value = ''
  partDialog.value = true
}
async function savePart() {
  partSaving.value = true
  dialogError.value = ''
  try {
    const input: SparePartInput = {
      code: partForm.code.trim(), name: partForm.name.trim(), specification: partForm.specification.trim(),
      unit: partForm.unit.trim(), stock: partForm.stock, minStock: partForm.minStock, location: partForm.location.trim(),
      supplier: partForm.supplier.trim(), unitPrice: partForm.unitPrice, description: partForm.description.trim(),
      enabled: partForm.enabled
    }
    if (partForm.id) await sparePartApi.update(partForm.id, input)
    else await sparePartApi.create(input)
    partDialog.value = false
    await loadParts()
    ElMessage.success(tr('assets.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('assets.saveFailed'))
  } finally {
    partSaving.value = false
  }
}
async function removePart(row: SparePart) {
  partError.value = ''
  try {
    await sparePartApi.remove(row.id)
    await loadParts()
    ElMessage.success(tr('assets.deleted'))
  } catch (e) {
    partError.value = errorMessage(e, tr('assets.deleteFailed'))
  }
}
function openMovement(row: SparePart, type: SparePartMovementType) {
  movementTarget.value = row
  movementForm.type = type
  movementForm.quantity = type === 'Adjust' ? row.stock : 1
  movementForm.note = ''
  movementForm.workOrderId = null
  dialogError.value = ''
  movementDialog.value = true
}
async function submitMovement() {
  if (!movementTarget.value) return
  if (!Number.isFinite(movementForm.quantity) || (movementForm.type === 'Adjust' ? movementForm.quantity < 0 : movementForm.quantity <= 0)) {
    dialogError.value = tr('assets.part.quantityHint')
    return
  }
  movementSaving.value = true
  dialogError.value = ''
  try {
    await sparePartApi.addMovement(movementTarget.value.id, {
      type: movementForm.type, quantity: movementForm.quantity, note: movementForm.note.trim(),
      workOrderId: movementForm.type === 'Out' ? movementForm.workOrderId : null
    })
    movementDialog.value = false
    await loadParts()
    ElMessage.success(tr('assets.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('assets.actionFailed'))
  } finally {
    movementSaving.value = false
  }
}
async function openPartMovements(row: SparePart) {
  const seq = ++detailSeq
  movementsTarget.value = row
  partMovements.value = []
  partsError.value = ''
  movementsDialog.value = true
  try {
    const movements = await sparePartApi.movements(row.id)
    if (seq !== detailSeq || movementsTarget.value?.id !== row.id) return
    partMovements.value = movements
  } catch (e) {
    if (seq !== detailSeq || movementsTarget.value?.id !== row.id) return
    partsError.value = errorMessage(e, tr('assets.loadFailed'))
  }
}

onMounted(async () => {
  try {
    const [areaList, deviceList] = await Promise.all([areaApi.list(), deviceApi.list()])
    areas.value = areaList
    devices.value = deviceList
    catalogError.value = ''
  } catch (e) {
    catalogError.value = errorMessage(e, tr('assets.loadFailed'))
  }
  await loadAll()
})
</script>

<style scoped>
.assets-page :deep(.el-tabs__header) {
  margin-bottom: 8px;
}
.filter-row {
  flex-wrap: wrap;
}
.low-stock {
  color: var(--el-color-danger);
  font-weight: 600;
}
.low-stock-tag {
  margin-left: 4px;
}
</style>
