import { defaultLandingPath, type AccessHolder } from './policies'
import { getLocalWorkstation, hmiLayoutApi } from '../api/hmiLayouts'

export type LandingPath = '/overview' | '/screens' | '/console'

/** 本机已绑定工位，或现场已有已发布的控制室布局。 */
export async function hasRuntimeConsoleLanding(): Promise<boolean> {
  const local = getLocalWorkstation()
  if (local.id != null || !!local.name) return true
  try {
    const meta = await hmiLayoutApi.publishedMeta()
    return meta.length > 0
  } catch {
    return false
  }
}

/**
 * 角色默认首页：组态/系统账号仍进总览；操作员在有工位或已发布布局时进控制室布局，否则进画面目录。
 * 查询失败时退回原来的 /screens，避免把人送进空的运行页。
 */
export async function resolveLandingPath(
  user: AccessHolder,
  hasConsole: () => boolean | Promise<boolean> = hasRuntimeConsoleLanding
): Promise<LandingPath> {
  const fallback = defaultLandingPath(user)
  if (fallback !== '/screens') return fallback
  try {
    return await hasConsole() ? '/console' : '/screens'
  } catch {
    return '/screens'
  }
}
