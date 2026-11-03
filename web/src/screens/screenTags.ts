import type { ScreenNode } from './model'

export function collectScreenTags(node: ScreenNode, into: Set<number>): void {
  for (const id of [node.tagId, node.alarmTagId, node.animation?.tagId, node.action?.tagId]) if (id) into.add(id)
  for (const id of node.tags ?? []) if (id) into.add(id)
  for (const binding of node.bindings ?? []) if (binding.tagId) into.add(binding.tagId)
  for (const command of node.commands ?? []) if (command.tagId) into.add(command.tagId)
}
