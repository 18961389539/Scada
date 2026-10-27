import type { Permission, UserAccess } from '../types'

export type AccessHolder = { role?: string; access?: UserAccess } | null | undefined

// 权限判断：前端只用于隐藏/禁用入口，最终以服务端校验为准。
// 这里是唯一实现，utils 只做兼容再导出。
export function hasGlobal(user: AccessHolder, permission: Permission): boolean {
  if (!user) return false
  if (user.access) return user.access.global.includes(permission)
  return legacy(user.role, permission)
}

export function hasAny(user: AccessHolder, permission: Permission): boolean {
  if (!user) return false
  if (user.access) return user.access.global.includes(permission) || user.access.areas.some(a => a.permissions.includes(permission))
  return legacy(user.role, permission)
}

export function hasInArea(user: AccessHolder, permission: Permission, areaId?: number | null): boolean {
  if (!user) return false
  if (hasGlobal(user, permission)) return true
  if (areaId == null || !user.access) return false
  return user.access.areas.some(a => a.areaId === areaId && a.permissions.includes(permission))
}

function legacy(role: string | undefined, permission: Permission): boolean {
  if (role === 'Admin') return true
  if (role === 'Operator') return permission === 'View' || permission === 'Control' || permission === 'AckAlarm'
  if (role === 'Viewer') return permission === 'View'
  return false
}

export type Policy =
  | { scope: 'global'; permission: Permission }
  | { scope: 'any-area'; permission: Permission }
  | { scope: 'any-global'; permissions: Permission[] }

const POLICY_DEFS = {
  'global-configure': { scope: 'global', permission: 'Configure' },
  'any-configure': { scope: 'any-area', permission: 'Configure' },
  'any-control': { scope: 'any-area', permission: 'Control' },
  'any-view': { scope: 'any-area', permission: 'View' },
  'global-system': { scope: 'global', permission: 'System' },
  // 系统管理页由用户管理、系统维护或全局配置任一权限进入，页内再按标签细分。
  'system-admin': { scope: 'any-global', permissions: ['ManageUsers', 'System', 'Configure'] }
} as const satisfies Record<string, Policy>

export type PolicyId = keyof typeof POLICY_DEFS

export const POLICIES = POLICY_DEFS

/**
 * 路由守卫与导航目录共用的唯一判定入口。
 * 页面内按钮请用 `can`/`canGlobal`，不要再直接组合 hasAny/hasGlobal。
 */
export function allows(user: AccessHolder, policy?: PolicyId): boolean {
  if (!policy) return true
  const definition = POLICIES[policy]
  if (definition.scope === 'global') return hasGlobal(user, definition.permission)
  if (definition.scope === 'any-area') return hasAny(user, definition.permission)
  return definition.permissions.some(permission => hasGlobal(user, permission))
}

/** 页面动作：不传 areaId 表示“任一区域”，传 areaId 表示该区域（全局权限自动覆盖区域）。 */
export function can(user: AccessHolder, permission: Permission, areaId?: number | null): boolean {
  return areaId == null ? hasAny(user, permission) : hasInArea(user, permission, areaId)
}

/** 只允许全局权限的动作（阈值、API Key、工程级组态等）。 */
export function canGlobal(user: AccessHolder, permission: Permission): boolean {
  return hasGlobal(user, permission)
}

declare module 'vue-router' {
  interface RouteMeta {
    policy?: PolicyId
    public?: boolean
  }
}