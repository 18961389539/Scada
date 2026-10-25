import { tr } from '../i18n'
// 内置工业符号库（自研绘制，ISA-101 风格：灰度为主，状态色只用于运行/报警）。
// 统一 viewBox 0 0 100 100；`currentColor` 为状态填充色（停止=深灰、运行=浅色、报警=红、未知=暗灰），线条为中性灰。
export interface BuiltinSymbol { key: string; name: string; nameEn: string; category: string; svg: string; w: number; h: number }
const S = 'stroke="#c3cad2" stroke-width="3" stroke-linejoin="round" fill="none"'
const wrap = (body: string) => `<svg viewBox="0 0 100 100" xmlns="http://www.w3.org/2000/svg"><g ${S}>${body}</g></svg>`
export const builtinSymbols: BuiltinSymbol[] = [
  { key: 'isa:pump', name: tr('symbols.ka0dcad'), nameEn: 'Centrifugal pump', category: 'pumps', w: 100, h: 100, svg: wrap('<circle cx="50" cy="50" r="30" fill="currentColor"/><path d="M50 20 H92 V34"/><path d="M30 78 L22 92 H78 L70 78"/>') },
  { key: 'isa:pump-positive', name: tr('symbols.k51b670'), nameEn: 'Positive displacement pump', category: 'pumps', w: 100, h: 100, svg: wrap('<circle cx="50" cy="50" r="30" fill="currentColor"/><circle cx="50" cy="50" r="12"/><path d="M8 50 H20 M80 50 H92"/>') },
  { key: 'isa:compressor', name: tr('symbols.ka217f3'), nameEn: 'Compressor', category: 'pumps', w: 100, h: 100, svg: wrap('<circle cx="50" cy="50" r="32" fill="currentColor"/><path d="M28 27 L72 38 V62 L28 73 Z"/>') },
  { key: 'isa:valve', name: tr('symbols.k910392'), nameEn: 'Gate valve', category: 'valves', w: 100, h: 70, svg: wrap('<path d="M10 25 L90 75 V25 L10 75 Z" fill="currentColor"/><path d="M50 50 V10 M38 10 H62"/>') },
  { key: 'isa:valve-control', name: tr('symbols.kc3cdee'), nameEn: 'Control valve', category: 'valves', w: 100, h: 90, svg: wrap('<path d="M10 55 L90 90 V55 L10 90 Z" fill="currentColor"/><path d="M50 72 V30"/><path d="M28 30 A22 18 0 0 1 72 30 Z"/>') },
  { key: 'isa:valve-check', name: tr('symbols.k75db00'), nameEn: 'Check valve', category: 'valves', w: 100, h: 60, svg: wrap('<path d="M10 15 L90 50 V15 L10 50 Z" fill="currentColor"/><path d="M50 10 L50 55"/>') },
  { key: 'isa:valve-butterfly', name: tr('symbols.k265ec7'), nameEn: 'Butterfly valve', category: 'valves', w: 100, h: 80, svg: wrap('<path d="M10 20 V60 M90 20 V60 M10 40 H90"/><ellipse cx="50" cy="40" rx="10" ry="24" fill="currentColor"/>') },
  { key: 'isa:motor', name: tr('symbols.k758b20'), nameEn: 'Motor', category: 'motors', w: 100, h: 100, svg: wrap('<circle cx="50" cy="50" r="32" fill="currentColor"/><text x="50" y="62" text-anchor="middle" font-size="32" fill="#1d252c" stroke="none" font-family="sans-serif">M</text>') },
  { key: 'isa:fan', name: tr('symbols.k41eef0'), nameEn: 'Fan', category: 'motors', w: 100, h: 100, svg: wrap('<circle cx="50" cy="50" r="34"/><path d="M50 50 C30 20 60 16 56 40 Z M50 50 C80 40 78 70 58 58 Z M50 50 C40 80 18 66 38 54 Z" fill="currentColor"/>') },
  { key: 'isa:agitator', name: tr('symbols.k710d14'), nameEn: 'Agitator', category: 'motors', w: 80, h: 100, svg: wrap('<rect x="30" y="6" width="40" height="22" fill="currentColor"/><path d="M50 28 V88 M30 88 H70 M30 78 L50 88 L70 78"/>') },
  { key: 'isa:tank', name: tr('symbols.k30c9da'), nameEn: 'Vertical tank', category: 'vessels', w: 80, h: 100, svg: wrap('<path d="M12 18 Q40 2 68 18 V86 Q40 98 12 86 Z" fill="currentColor"/>') },
  { key: 'isa:tank-horizontal', name: tr('symbols.k8e6f15'), nameEn: 'Horizontal tank', category: 'vessels', w: 100, h: 60, svg: wrap('<path d="M18 10 H82 Q98 30 82 50 H18 Q2 30 18 10 Z" fill="currentColor"/>') },
  { key: 'isa:exchanger', name: tr('symbols.kc53a59'), nameEn: 'Heat exchanger', category: 'vessels', w: 100, h: 100, svg: wrap('<circle cx="50" cy="50" r="34" fill="currentColor"/><path d="M8 50 H30 L40 32 L60 68 L70 50 H92"/>') },
  { key: 'isa:filter', name: tr('symbols.kcc81ff'), nameEn: 'Filter', category: 'vessels', w: 80, h: 100, svg: wrap('<path d="M12 10 H68 V90 H12 Z" fill="currentColor"/><path d="M12 10 L68 90" stroke-dasharray="6 4"/>') },
  { key: 'isa:pipe', name: tr('symbols.kad30d9'), nameEn: 'Pipe', category: 'pipes', w: 100, h: 20, svg: '<svg viewBox="0 0 100 20" preserveAspectRatio="none" xmlns="http://www.w3.org/2000/svg"><rect x="0" y="5" width="100" height="10" fill="currentColor" stroke="#c3cad2" stroke-width="1.5"/></svg>' },
  { key: 'isa:pipe-elbow', name: tr('symbols.ka8eef6'), nameEn: 'Elbow', category: 'pipes', w: 60, h: 60, svg: '<svg viewBox="0 0 60 60" xmlns="http://www.w3.org/2000/svg"><path d="M0 25 H35 V60 H25 V35 H0 Z" fill="currentColor" stroke="#c3cad2" stroke-width="1.5"/></svg>' },
  { key: 'isa:pipe-tee', name: tr('symbols.k7a3407'), nameEn: 'Tee', category: 'pipes', w: 80, h: 60, svg: '<svg viewBox="0 0 80 60" xmlns="http://www.w3.org/2000/svg"><path d="M0 20 H80 V30 H45 V60 H35 V30 H0 Z" fill="currentColor" stroke="#c3cad2" stroke-width="1.5"/></svg>' },
  { key: 'isa:conveyor', name: tr('symbols.kd0c04f'), nameEn: 'Belt conveyor', category: 'conveyors', w: 160, h: 50, svg: '<svg viewBox="0 0 160 50" xmlns="http://www.w3.org/2000/svg"><g stroke="#c3cad2" stroke-width="3" fill="none"><rect x="6" y="10" width="148" height="30" rx="15" fill="currentColor"/><circle cx="21" cy="25" r="9"/><circle cx="139" cy="25" r="9"/><circle cx="80" cy="25" r="5"/></g></svg>' },
  { key: 'isa:screw-conveyor', name: tr('symbols.kbc57f3'), nameEn: 'Screw conveyor', category: 'conveyors', w: 160, h: 40, svg: '<svg viewBox="0 0 160 40" xmlns="http://www.w3.org/2000/svg"><g stroke="#c3cad2" stroke-width="3" fill="none"><rect x="4" y="6" width="152" height="28" fill="currentColor"/><path d="M14 34 L30 6 M34 34 L50 6 M54 34 L70 6 M74 34 L90 6 M94 34 L110 6 M114 34 L130 6 M134 34 L150 6"/></g></svg>' },
  { key: 'isa:transmitter', name: tr('symbols.kb03f18'), nameEn: 'Transmitter', category: 'sensors', w: 80, h: 80, svg: '<svg viewBox="0 0 80 80" xmlns="http://www.w3.org/2000/svg"><g stroke="#c3cad2" stroke-width="3" fill="none"><circle cx="40" cy="40" r="30" fill="currentColor"/><path d="M10 40 H70"/></g></svg>' },
  { key: 'isa:sensor-level', name: tr('symbols.k4ff2c1'), nameEn: 'Level gauge', category: 'sensors', w: 40, h: 100, svg: '<svg viewBox="0 0 40 100" xmlns="http://www.w3.org/2000/svg"><g stroke="#c3cad2" stroke-width="3" fill="none"><rect x="12" y="6" width="16" height="88" fill="currentColor"/><path d="M4 20 H12 M4 50 H12 M4 80 H12"/></g></svg>' },
  { key: 'isa:sensor-flow', name: tr('symbols.ke241ac'), nameEn: 'Flow meter', category: 'sensors', w: 100, h: 60, svg: '<svg viewBox="0 0 100 60" xmlns="http://www.w3.org/2000/svg"><g stroke="#c3cad2" stroke-width="3" fill="none"><path d="M0 30 H25 M75 30 H100"/><rect x="25" y="12" width="50" height="36" fill="currentColor"/><path d="M35 30 H65 M58 23 L65 30 L58 37"/></g></svg>' },
  { key: 'isa:indicator', name: tr('symbols.k01611e'), nameEn: 'Indicator lamp', category: 'indicators', w: 60, h: 60, svg: '<svg viewBox="0 0 60 60" xmlns="http://www.w3.org/2000/svg"><g stroke="#c3cad2" stroke-width="3"><circle cx="30" cy="30" r="22" fill="currentColor"/></g></svg>' },
  { key: 'isa:indicator-bar', name: tr('symbols.kcad7b2'), nameEn: 'Status bar', category: 'indicators', w: 100, h: 24, svg: '<svg viewBox="0 0 100 24" preserveAspectRatio="none" xmlns="http://www.w3.org/2000/svg"><rect x="2" y="2" width="96" height="20" rx="3" fill="currentColor" stroke="#c3cad2" stroke-width="2"/></svg>' },
  { key: 'isa:alarm-bell', name: tr('symbols.ka439d6'), nameEn: 'Horn / beacon', category: 'indicators', w: 80, h: 80, svg: wrap('<path d="M30 70 V40 A20 20 0 0 1 70 40 V70 Z" fill="currentColor"/><path d="M22 76 H78 M50 12 V20 M20 26 L26 32 M80 26 L74 32"/>') }
]
export const symbolCategories: Record<string, { zh: string; en: string }> = {
  pumps: { zh: tr('symbols.k2fa5ba'), en: 'Pumps & compressors' }, valves: { zh: tr('symbols.k257555'), en: 'Valves' }, motors: { zh: tr('symbols.kf43ea2'), en: 'Motors & fans' },
  vessels: { zh: tr('symbols.kff415e'), en: 'Vessels' }, pipes: { zh: tr('symbols.kad30d9'), en: 'Pipes' }, conveyors: { zh: tr('symbols.k7eb9b7'), en: 'Conveyors' },
  sensors: { zh: tr('symbols.kaad5df'), en: 'Instruments' }, indicators: { zh: tr('symbols.k9a950c'), en: 'Indicators' }
}
// ISA-101 状态色：停止为中灰（低饱和）、运行为亮灰白、报警为红、数据无效为暗灰。
export const isaColors = { stopped: '#6b7580', running: '#e9edf1', alarm: '#e03131', unknown: '#3d4650' }

// 用户 SVG 清洗（导入时）：只保留静态图形元素与安全属性；服务端还会再次拒绝脚本、事件属性和外部引用。
const allowedTags = new Set(['svg', 'g', 'path', 'rect', 'circle', 'ellipse', 'line', 'polyline', 'polygon', 'text', 'tspan', 'defs', 'lineargradient', 'radialgradient', 'stop', 'title', 'desc', 'clippath', 'mask', 'pattern', 'symbol'])
export function sanitizeSvg(input: string): string {
  const doc = new DOMParser().parseFromString(input, 'image/svg+xml')
  const root = doc.documentElement
  if (!root || root.nodeName.toLowerCase() !== 'svg' || doc.querySelector('parsererror')) throw new Error(tr('symbols.k3c8f18'))
  const walk = (el: Element) => {
    for (const child of [...el.children]) { if (!allowedTags.has(child.nodeName.toLowerCase())) child.remove(); else walk(child) }
    for (const attr of [...el.attributes]) {
      const name = attr.name.toLowerCase(), value = attr.value.trim().toLowerCase()
      if (name.startsWith('on') || name === 'style' || ((name === 'href' || name === 'xlink:href' || name === 'src') && !value.startsWith('#')) || value.includes('javascript:') || (value.includes('url(') && !/url\(\s*['"]?#/.test(value))) el.removeAttribute(attr.name)
    }
  }
  walk(root)
  return new XMLSerializer().serializeToString(root)
}
