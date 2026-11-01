import type { ScreenDocument, ScreenNode, ScreenPanelCommand } from './model'

export interface TemplatePlaceholder { key: string; tagId: number }

export class TemplateBindingError extends Error {
  constructor(readonly nodeId: string, readonly binding: string) {
    super(`Invalid template binding ${binding} on ${nodeId}`)
    this.name = 'TemplateBindingError'
  }
}

function tagIds(node: ScreenNode): (number | null | undefined)[] {
  return [node.tagId, node.alarmTagId, node.action.tagId, ...(node.commands ?? []).map(command => command.tagId),
    ...(node.tags ?? []), ...(node.bindings ?? []).map(binding => binding.tagId), node.animation?.tagId]
}

export function templatePlaceholderInUse(document: ScreenDocument, placeholder: TemplatePlaceholder): boolean {
  return document.nodes.some(node => tagIds(node).includes(placeholder.tagId)
    || [node.bind, node.alarmBind, node.action.bind, ...(node.commands ?? []).map(command => command.bind),
      ...(node.tagBinds ?? []), node.animationBind].includes(placeholder.key))
}

// 编辑器用负数伪测点引用占位符，模板文档用命名绑定；正数编号是真实辅助测点，原样保留。
const isPseudo = (id: number | null | undefined): id is number => id != null && id < 0

export function templateToEditor(document: ScreenDocument, placeholders: readonly TemplatePlaceholder[]): ScreenDocument {
  const ids = new Map(placeholders.map(placeholder => [placeholder.key, placeholder.tagId]))
  const idOf = (key: string | null | undefined, nodeId: string): number | null => {
    if (!key) return null
    const id = ids.get(key)
    if (id == null) throw new TemplateBindingError(nodeId, key)
    return id
  }
  return { ...document, nodes: document.nodes.map(node => {
    const next: ScreenNode = { ...node,
      tagId: idOf(node.bind, node.id), alarmTagId: idOf(node.alarmBind, node.id), bind: null, alarmBind: null,
      action: { ...node.action, tagId: idOf(node.action.bind, node.id), bind: null }
    }
    if (node.tagBinds?.length) next.tags = node.tagBinds.map(key => idOf(key, node.id) as number)
    delete next.tagBinds
    if (node.animation) next.animation = { ...node.animation, tagId: node.animationBind ? idOf(node.animationBind, node.id) : node.animation.tagId }
    delete next.animationBind
    if (node.commands) next.commands = node.commands.map(command => ({ ...command, tagId: idOf(command.bind, node.id), bind: null }))
    return next
  }) }
}

export function templateFromEditor(document: ScreenDocument, placeholders: readonly TemplatePlaceholder[]): ScreenDocument {
  const keys = new Map(placeholders.map(placeholder => [placeholder.tagId, placeholder.key]))
  const keyOf = (id: number | null | undefined, nodeId: string): string | null => {
    if (id == null) return null
    const key = keys.get(id)
    if (!key) throw new TemplateBindingError(nodeId, String(id))
    return key
  }
  return { ...document, nodes: document.nodes.map(node => {
    const next: ScreenNode = { ...node, tagId: null, alarmTagId: null,
      action: { ...node.action, tagId: null, bind: node.action.type === 'command' ? keyOf(node.action.tagId, node.id) : null } }
    if (node.tagId != null) next.bind = keyOf(node.tagId, node.id)
    else delete next.bind
    if (node.alarmTagId != null) next.alarmBind = keyOf(node.alarmTagId, node.id)
    else delete next.alarmBind
    if (next.action.bind == null) delete next.action.bind
    const pseudoTags = (node.tags ?? []).filter(isPseudo)
    if (pseudoTags.length) {
      next.tagBinds = (node.tags ?? []).map(id => keyOf(id, node.id) as string)
      delete next.tags
    } else {
      delete next.tagBinds
    }
    if (node.animation) {
      if (isPseudo(node.animation.tagId)) {
        next.animationBind = keyOf(node.animation.tagId, node.id)
        next.animation = { ...node.animation, tagId: null }
      } else {
        next.animation = { ...node.animation }
        delete next.animationBind
      }
    } else {
      delete next.animation
      delete next.animationBind
    }
    if (node.commands) {
      next.commands = node.commands.map(command => {
        const converted: ScreenPanelCommand = { ...command }
        if (isPseudo(converted.tagId)) { converted.bind = keyOf(converted.tagId, node.id); converted.tagId = null }
        else delete converted.bind
        return converted
      })
    } else {
      delete next.commands
    }
    for (const binding of node.bindings ?? []) if (isPseudo(binding.tagId)) keyOf(binding.tagId, node.id)
    return next
  }) }
}
