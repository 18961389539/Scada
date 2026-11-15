import type { AlarmRule, AlarmRuleInput, Tag } from '../types'

export const alarmLevelTypes: AlarmRule['type'][] = ['High', 'HighHigh', 'Low', 'LowLow']
export const discreteDataTypes = ['Bool', 'Int16', 'UInt16', 'Int32', 'UInt32']

export function emptyAlarmRuleInput(): AlarmRuleInput {
  return {
    tagId: 0,
    type: 'High',
    activeWhen: 'On',
    threshold: 0,
    level: 'Warning',
    category: 'Process',
    consequence: 'Major',
    responseTimeSec: 600,
    autoPriority: true,
    delaySec: 3,
    recoverDeadband: 0,
    enabled: true,
    message: '',
    messageEn: '',
    cause: '',
    causeEn: '',
    action: '',
    actionEn: '',
    offDelaySec: 0,
    bitIndex: null,
    expression: '',
    suppressTagId: null,
    suppressMode: 'None'
  }
}

export function isLevelRule(rule: { type: AlarmRule['type'] }): boolean {
  return alarmLevelTypes.includes(rule.type)
}

export function showsTiming(type: AlarmRule['type'], activeWhen?: AlarmRule['activeWhen']): boolean {
  return isLevelRule({ type }) || type === 'BadQuality' || type === 'Expression'
    || (type === 'Discrete' && (activeWhen ?? 'On') !== 'Change')
}

export function toAlarmRuleInput(rule: AlarmRule, patch: Partial<AlarmRuleInput> = {}): AlarmRuleInput {
  return {
    tagId: rule.tagId,
    type: rule.type,
    activeWhen: rule.type === 'StateChange' ? 'Change' : rule.activeWhen ?? 'On',
    threshold: rule.threshold,
    level: rule.level,
    category: rule.category ?? 'Process',
    consequence: rule.consequence ?? 'Major',
    responseTimeSec: rule.responseTimeSec ?? 600,
    autoPriority: rule.autoPriority ?? false,
    delaySec: rule.delaySec,
    recoverDeadband: rule.recoverDeadband,
    enabled: rule.enabled,
    message: rule.message ?? '',
    messageEn: rule.messageEn ?? '',
    cause: rule.cause ?? '',
    causeEn: rule.causeEn ?? '',
    action: rule.action ?? '',
    actionEn: rule.actionEn ?? '',
    offDelaySec: rule.offDelaySec ?? 0,
    bitIndex: rule.bitIndex ?? null,
    expression: rule.expression ?? '',
    suppressTagId: rule.suppressTagId ?? null,
    suppressMode: rule.suppressMode ?? 'None',
    ...patch
  }
}

export function alarmRuleTagLabel(tag: Tag): string {
  return `${tag.name} (#${tag.id})`
}
