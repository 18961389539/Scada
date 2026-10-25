<template>
  <div ref="navRef" class="nav-wrap">
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
      @open="onMenuOpen"
    >
      <template v-for="item in opsItems" :key="item.id">
        <el-menu-item :index="item.path" :data-test="'nav-item-' + item.id">
          <el-icon><component :is="item.icon" /></el-icon>
          <span>{{ $t(item.titleKey) }}</span>
        </el-menu-item>
      </template>
      <el-sub-menu v-if="configItems.length" index="config" data-test="nav-config-group">
        <template #title>
          <el-icon><Operation /></el-icon>
          <span>{{ $t(CONFIG_GROUP_KEY) }}</span>
        </template>
        <el-menu-item
          v-for="item in configItems"
          :key="item.id"
          :index="item.path"
          :data-test="'nav-item-' + item.id"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span>{{ $t(item.titleKey) }}</span>
        </el-menu-item>
      </el-sub-menu>
      <div v-if="!opsItems.length && !configItems.length" class="nav-empty" data-test="nav-filter-empty">
        {{ $t('nav.noResults') }}
      </div>
    </el-menu>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { Operation, Search } from '@element-plus/icons-vue'
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
const navRef = ref<HTMLElement | null>(null)
function menuEl(): HTMLElement | null {
  return navRef.value?.querySelector<HTMLElement>('.nav-menu') ?? null
}

// 组态配置在菜单底部，展开后子项会落到视口之外；展开即滚到整组可见，
// 否则用户只能看到贴着底边的一项，以为菜单里没有其他配置页。
function onMenuOpen(index: string): void {
  if (index !== 'config') return
  void nextTick(() => {
    requestAnimationFrame(() => {
      const el = menuEl()
      const groupEl = el?.querySelector<HTMLElement>('.el-sub-menu.is-opened')
      if (!el || !groupEl) return
      const menuRect = el.getBoundingClientRect()
      const groupRect = groupEl.getBoundingClientRect()
      const overflow = groupRect.bottom - menuRect.bottom
      if (overflow > 0) {
        el.scrollTo({ top: el.scrollTop + overflow + 8, behavior: 'smooth' })
      }
    })
  })
}

// 直接打开组态子页时，菜单会在初始化时自动展开父级但不触发 open；
// 这里补滚动。菜单高度要等布局与展开动画结束才稳定，所以多校正几次。
function revealActive(attempt = 0): void {
  void nextTick(() => {
    requestAnimationFrame(() => {
      const el = menuEl()
      const active = el?.querySelector<HTMLElement>('.el-menu-item.is-active')
      if (!el || !active) return
      const menuRect = el.getBoundingClientRect()
      const itemRect = active.getBoundingClientRect()
      if (itemRect.bottom > menuRect.bottom) {
        el.scrollTop += itemRect.bottom - menuRect.bottom + 8
      } else if (itemRect.top < menuRect.top) {
        el.scrollTop -= menuRect.top - itemRect.top + 8
      }
      if (attempt < 8) window.setTimeout(() => revealActive(attempt + 1), 80)
    })
  })
}

onMounted(revealActive)
watch(() => route.path, () => revealActive())

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
