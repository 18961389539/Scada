/** 画面卡片上按角色收敛的主按钮：草稿/待发布变更以编辑为主，已发布运行面以运行为主。 */
export function screenCardPrimaryAction(
  screen: { publishedRevision?: number | null; hasDraftChanges?: boolean },
  canEdit: boolean
): 'edit' | 'run' | null {
  if (canEdit && (!screen.publishedRevision || screen.hasDraftChanges)) return 'edit'
  if (screen.publishedRevision) return 'run'
  return canEdit ? 'edit' : null
}
