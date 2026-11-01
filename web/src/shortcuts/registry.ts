import { computed, ref } from 'vue'

// 快捷键一览的唯一数据源：全局组常驻，页面组由视图挂载时注册、离开时注销。
// 描述一律用文案键，避免在源码里写中文（i18n-check 会拦）。

export interface ShortcutItem {
  /** 按键展示文本，按顺序表示组合键 */
  keys: string[]
  descKey: string
}

export interface ShortcutGroup {
  id: string
  titleKey: string
  items: ShortcutItem[]
}

const globalGroup: ShortcutGroup = {
  id: 'global',
  titleKey: 'shortcuts.global',
  items: [
    { keys: ['Ctrl', 'K'], descKey: 'shortcuts.cmdPalette' },
    { keys: ['?'], descKey: 'shortcuts.help' },
    { keys: ['Esc'], descKey: 'shortcuts.closeLayer' }
  ]
}

const pageGroups = ref<ShortcutGroup[]>([])

export const shortcutHelpOpen = ref(false)

export const shortcutGroups = computed(() => [globalGroup, ...pageGroups.value])

export function registerShortcuts(group: ShortcutGroup): () => void {
  pageGroups.value = [...pageGroups.value.filter(item => item.id !== group.id), group]
  return () => {
    pageGroups.value = pageGroups.value.filter(item => item.id !== group.id)
  }
}

export function openShortcutHelp(): void {
  shortcutHelpOpen.value = true
}
