import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { shouldRememberRoute } from './lastVisit'

const appLayout = { path: '/' }
const consoleRoute = { path: '/console/:layoutId?' }
const kioskRoute = { path: '/kiosk/screens/:id' }
const notFound = { path: '/:pathMatch(.*)*' }

describe('shouldRememberRoute', () => {
  it('remembers app-layout pages and console layouts, including a layout id', () => {
    expect(shouldRememberRoute({ path: '/screens', matched: [appLayout] })).toBe(true)
    expect(shouldRememberRoute({ path: '/console', matched: [consoleRoute] })).toBe(true)
    expect(shouldRememberRoute({ path: '/console/12', matched: [consoleRoute] })).toBe(true)
  })

  it('does not remember kiosk, layout preview, login, or unknown paths', () => {
    expect(shouldRememberRoute({ path: '/kiosk/screens/3', matched: [kioskRoute] })).toBe(false)
    expect(shouldRememberRoute({ path: '/config/layout-preview', matched: [{ path: '/config/layout-preview' }] })).toBe(false)
    expect(shouldRememberRoute({ path: '/login', meta: { public: true }, matched: [{ path: '/login' }] })).toBe(false)
    expect(shouldRememberRoute({ path: '/gone', matched: [appLayout, notFound] })).toBe(false)
  })
})

describe('PWA manifest', () => {
  it('starts at the root so startPath can land on the last console visit', () => {
    const manifest = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), '../../public/manifest.webmanifest'), 'utf8')) as { start_url: string }
    expect(manifest.start_url).toBe('/')
  })
})
