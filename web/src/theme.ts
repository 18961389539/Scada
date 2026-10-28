export type ThemeMode = 'light' | 'dark'
export type DensityMode = 'comfortable' | 'compact'

const THEME_KEY = 'scada_theme'
const DENSITY_KEY = 'scada_density'

export function currentTheme(): ThemeMode {
  return localStorage.getItem(THEME_KEY) === 'dark' ? 'dark' : 'light'
}

export function currentDensity(): DensityMode {
  return localStorage.getItem(DENSITY_KEY) === 'compact' ? 'compact' : 'comfortable'
}

export function applyTheme(mode: ThemeMode): void {
  localStorage.setItem(THEME_KEY, mode)
  document.documentElement.classList.toggle('dark', mode === 'dark')
  document.documentElement.dataset.theme = mode
}

export function applyDensity(mode: DensityMode): void {
  localStorage.setItem(DENSITY_KEY, mode)
  document.documentElement.dataset.density = mode
}

// 在挂载前执行，避免暗色主题闪一帧白底
export function initAppearance(): void {
  applyTheme(currentTheme())
  applyDensity(currentDensity())
}

export interface ChartPalette {
  text: string
  muted: string
  split: string
  annotationBg: string
  annotationText: string
  zoom: {
    borderColor: string
    backgroundColor: string
    fillerColor: string
    dataBackground: { lineStyle: { color: string }; areaStyle: { color: string } }
    selectedDataBackground: { lineStyle: { color: string }; areaStyle: { color: string } }
    handleStyle: { color: string; borderColor: string }
    moveHandleStyle: { color: string }
  }
}

// ECharts 用 canvas 绘制，不继承 CSS 变量，暗色下必须显式换色，否则图例是深灰、缩放条是白条。
export function chartPalette(): ChartPalette {
  if (currentTheme() === 'dark') {
    return {
      text: '#a9b8cc',
      muted: '#9baac0',
      split: '#273344',
      annotationBg: 'rgba(22,30,43,0.9)',
      annotationText: '#a9b8cc',
      zoom: {
        borderColor: '#36465c',
        backgroundColor: '#1b2534',
        fillerColor: 'rgba(99,230,190,0.14)',
        dataBackground: { lineStyle: { color: '#3a4a60' }, areaStyle: { color: '#243247' } },
        selectedDataBackground: { lineStyle: { color: '#63e6be' }, areaStyle: { color: '#1f3b3a' } },
        handleStyle: { color: '#161e2b', borderColor: '#63e6be' },
        moveHandleStyle: { color: '#4a5c74' }
      }
    }
  }
  return {
    text: '#5b6b82',
    muted: '#586a80',
    split: '#edf1f7',
    annotationBg: 'rgba(255,255,255,0.9)',
    annotationText: '#606266',
    zoom: {
      borderColor: '#e3e9f2',
      backgroundColor: '#f7fafd',
      fillerColor: 'rgba(22,104,220,0.10)',
      dataBackground: { lineStyle: { color: '#cdd9e8' }, areaStyle: { color: '#eaf0f8' } },
      selectedDataBackground: { lineStyle: { color: '#8fb4e8' }, areaStyle: { color: '#dbe8f8' } },
      handleStyle: { color: '#fff', borderColor: '#1668dc' },
      moveHandleStyle: { color: '#cbd8e8' }
    }
  }
}
