/** 点动按下必须按住保持；测点若要求操作确认，不能再走「确认后再下发」的路径。 */
export function isMomentaryPressBlockedByConfirm(phase: string | undefined, requireConfirm: boolean): boolean {
  return phase === 'press' && !!requireConfirm
}

/** 设计器：点动控件绑到需要操作确认的测点时给出警告。脉冲按钮仍可走确认，不在此列。 */
export function shouldWarnMomentaryConfirm(
  nodeType: string | undefined,
  tagId: number | null | undefined,
  confirmTagIds: Iterable<number>
): boolean {
  if (nodeType !== 'jog' || tagId == null) return false
  if (confirmTagIds instanceof Set) return confirmTagIds.has(tagId)
  for (const id of confirmTagIds) if (id === tagId) return true
  return false
}
