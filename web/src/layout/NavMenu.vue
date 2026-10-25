<template>
  <div class="nav-wrap">
    <div class="nav-filter">
      <el-input
        v-model="filter"
        size="small"
        clearable
        data-test="nav-filter"
        :placeholder="$t('nav.menuFilter')"
        :prefix-icon="Search"
      />
    </div>
    <el-menu
      :default-active="route.path"
      router
      class="nav-menu"
      background-color="transparent"
      text-color="var(--scada-dark-muted)"
      active-text-color="var(--scada-accent)"
      :default-openeds="filter.trim() ? ['config'] : undefined"
    >
      <template v-for="item in opsItems" :key="item.id">
        <el-menu-item :index="item.path" :data-test="'nav-item-' + item.id">
          <el-icon><component :is="item.icon" /></el-icon>
          <span>{{ $t(item.titleKey) }}</span>
        </el-menu-item>
      </template>
      <el-sub-menu v-if="configItems.length" index="config" data-test="nav-config-group">
        <template #title>
          <el-icon><Setting /></el-icon>
          <span>{{ $t(CONFIG_GROUP_KEY) }}</span>
        </template>
        <el-menu-item
          v-for="item in configItems"
          :key="item.id"
          :index="item.path"
          :data-test="'nav-item-' + item.id"
        >
          {{ $t(item.titleKey) }}
        </el-menu-item>
      </el-sub-menu>
      <div v-if="!opsItems.length && !configItems.length" class="nav-empty" data-test="nav-filter-empty">
        {{ $t('nav.noResults') }}
      </div>
    </el-menu>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { Search, Setting } from '@element-plus/icons-vue'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'
import {
  CONFIG_GROUP_KEY,
  fuzzyMatch,
  navSearchText,
  visibleNavItems,
  type NavItemDef
} from '../nav/catalog'

const route = useRoute()
const auth = useAuthStore()
const filter = ref('')

const allowed = computed(() => visibleNavItems(auth.user))

function matches(item: NavItemDef): boolean {
  const q = filter.value.trim()
  if (!q) return true
  return fuzzyMatch(navSearchText(item, tr), q)
}

const opsItems = computed(() => allowed.value.filter((i) => i.group === 'ops' && matches(i)))
const configItems = computed(() => allowed.value.filter((i) => i.group === 'config' && matches(i)))
</script>

<style scoped>
.nav-wrap {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
.nav-filter {
  padding: 10px 12px 6px;
  flex-shrink: 0;
}
.nav-filter :deep(.el-input__wrapper) {
  background: rgba(255, 255, 255, 0.06);
  box-shadow: 0 0 0 1px var(--scada-dark-border) inset;
}
.nav-filter :deep(.el-input__inner) {
  color: var(--scada-dark-text);
}
.nav-filter :deep(.el-input__inner::placeholder) {
  color: var(--scada-dark-faint);
}
.nav-filter :deep(.el-input__prefix),
.nav-filter :deep(.el-input__suffix) {
  color: var(--scada-dark-muted);
}
.nav-menu {
  flex: 1;
  overflow: auto;
  border-right: none;
}
.nav-empty {
  padding: 16px 18px;
  color: var(--scada-dark-faint);
  font-size: 12px;
}
.nav-menu :deep(.el-sub-menu.is-active > .el-sub-menu__title) {
  color: var(--scada-accent) !important;
}
.nav-menu :deep(.el-menu-item .el-icon),
.nav-menu :deep(.el-sub-menu__title .el-icon) {
  color: inherit;
  margin-right: 8px;
}
</style>
